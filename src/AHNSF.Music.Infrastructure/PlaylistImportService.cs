using System.Security.Cryptography;
using AHNSF.Music.Core;

namespace AHNSF.Music.Infrastructure;

/// <summary>Imports operator-supplied MP3 files and reuses catalogued tracks. Remote metadata never grants download rights.</summary>
public sealed class PlaylistImportService(IImportMetadataProvider metadata, ILibraryService library, IPlaylistService playlists,
    ITrackMetadataReader tags, IOperationalHistoryService? history = null) : IPlaylistImportService
{
    private readonly SemaphoreSlim oneJob = new(1, 1);
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    public Task<ImportToolStatus> DiagnoseAsync(CancellationToken token = default) => metadata.DiagnoseAsync(token);

    public async Task<ImportPreview> PreviewUrlAsync(string url, CancellationToken token = default)
    {
        var address = ImportAddress.Parse(url);
        var preview = await metadata.PreviewAsync(address, null, token).ConfigureAwait(false);
        if (preview.Tracks.Count > 5000) throw new InvalidDataException("A playlist excede 5.000 faixas.");
        var catalog = (await library.QueryAsync(new(), token).ConfigureAwait(false)).Tracks.Where(t => t.Availability == AvailabilityStatus.Available).ToArray();
        var tracks = preview.Tracks.Select(t => t with { ExistingTrackId = FindMetadataMatch(t, catalog) }).ToArray();
        return preview with { Tracks = tracks };
    }

    public async Task<ImportPreview> PreviewLocalAsync(IReadOnlyList<string> paths, CancellationToken token = default)
    {
        if (paths.Count is 0 or > 5000) throw new ArgumentException("Selecione entre 1 e 5.000 arquivos MP3.");
        var catalog = (await library.QueryAsync(new(), token).ConfigureAwait(false)).Tracks.Where(t => t.Availability == AvailabilityStatus.Available).ToArray();
        var result = new List<ImportTrack>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(paths[i]);
            var title = Path.GetFileNameWithoutExtension(path);
            try
            {
                ValidateSource(path);
                var track = await Task.Run(() => tags.Read(path, Path.GetDirectoryName(path)!, token), token).ConfigureAwait(false);
                if (track.Availability != AvailabilityStatus.Available || track.Duration <= TimeSpan.Zero) throw new InvalidDataException("MP3 inválido.");
                var candidates = catalog.Where(t => t.FileSize == new FileInfo(path).Length);
                Guid? duplicate = null;
                var sourceHash = await HashAsync(path, token).ConfigureAwait(false);
                foreach (var existing in candidates)
                    if (Paths.Equals(existing.Path, path) || sourceHash.SequenceEqual(await HashAsync(existing.Path, token).ConfigureAwait(false)))
                    { duplicate = existing.Id; break; }
                result.Add(new(Guid.NewGuid(), i, Limit(track.Title), Limit(track.Artist), track.Duration, path, duplicate,
                    SourceId: Convert.ToHexString(sourceHash)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { result.Add(new(Guid.NewGuid(), i, Limit(title), "", TimeSpan.Zero, Issue: ex is FileNotFoundException ? "Arquivo indisponível." : "Arquivo MP3 inválido ou inacessível.")); }
        }
        return new("Arquivos selecionados", null, result, "Arquivos locais autorizados pelo operador");
    }

    public async Task<ImportResult> ImportAsync(ImportPreview preview, IReadOnlySet<Guid> selected, string root, string playlistName,
        IProgress<ImportProgress>? progress, CancellationToken token)
    {
        if (!await oneJob.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Já existe uma importação em andamento.");
        try { return await ImportCoreAsync(preview, selected, root, playlistName, progress, token).ConfigureAwait(false); }
        finally { oneJob.Release(); }
    }

    private async Task<ImportResult> ImportCoreAsync(ImportPreview preview, IReadOnlySet<Guid> selected, string root, string playlistName,
        IProgress<ImportProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new DirectoryNotFoundException("Configure uma pasta musical disponível antes de importar.");
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(playlistName) || playlistName.Trim().Length > 200) throw new ArgumentException("Informe um nome de playlist com até 200 caracteres.");
        if (selected.Count == 0) throw new ArgumentException("Selecione ao menos uma faixa.");
        var ordered = preview.Tracks.Where(t => selected.Contains(t.Id)).OrderBy(t => t.Position).ToArray();
        if (ordered.Length != selected.Count || ordered.Select(t => t.Id).Distinct().Count() != ordered.Length) throw new ArgumentException("A seleção não pertence à prévia atual.");
        Observe(OperationalEventType.ImportStarted, "Importação iniciada.");
        var rows = new List<ImportItemResult>();
        var pending = new List<(Guid Id, string Path, string Title)>();
        var copiedHashes = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var repeated = new Dictionary<Guid, Guid>();
        var count = 0;
        string? staging = null;
        // After a cancelled download, finished files are still verified, moved and indexed so they are never lost.
        var cancelled = false;
        try
        {
            IReadOnlyDictionary<Guid, string> obtained = new Dictionary<Guid, string>();
            var wanted = ordered.Where(t => t.Downloadable).GroupBy(t => t.SourceId ?? t.Id.ToString("N")).Select(g => g.First()).ToArray();
            if (wanted.Length > 0)
            {
                staging = Path.Combine(Path.GetTempPath(), "AHNSF Music", "Import", Guid.NewGuid().ToString("N"));
                progress?.Report(new(0, wanted.Length, $"Baixando: 0 de {wanted.Length} música(s)…"));
                obtained = await metadata.DownloadAsync(wanted, staging, progress, token).ConfigureAwait(false);
                cancelled = token.IsCancellationRequested;
            }
            var work = cancelled ? CancellationToken.None : token;
            foreach (var item in ordered)
            {
                work.ThrowIfCancellationRequested(); count++;
                var source = item.LocalPath ?? (obtained.TryGetValue(item.Id, out var file) ? file : null);
                if (item.ExistingTrackId is { } known) rows.Add(new(item.Id, item.Title, known, "Reutilizada"));
                else if (item.SourceId is { } hash && copiedHashes.TryGetValue(hash, out var first))
                { repeated[item.Id] = first; rows.Add(new(item.Id, item.Title, null, "Aguardando duplicata")); }
                else if (source is not null)
                    try
                    {
                        if (item.LocalPath is null) await VerifyObtainedAsync(source, item, work).ConfigureAwait(false);
                        var name = item.LocalPath is null ? $"{item.Artist} - {item.Title}" : Path.GetFileNameWithoutExtension(source);
                        var target = await CopyNewAsync(source, root, name, work).ConfigureAwait(false);
                        pending.Add((item.Id, target, item.Title)); rows.Add(new(item.Id, item.Title, null, "Copiada"));
                        if (item.SourceId is { } copiedHash) copiedHashes[copiedHash] = item.Id;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { rows.Add(new(item.Id, item.Title, null, "Indisponível", SafeError(ex))); }
                else rows.Add(new(item.Id, item.Title, null, "Indisponível", item.Issue ?? (!item.Downloadable ? "Fonte de áudio autorizada não fornecida."
                    : cancelled ? "Download cancelado." : "Não foi possível obter o áudio desta faixa.")));
                progress?.Report(new(count, ordered.Length, $"{count} de {ordered.Length} faixas organizadas."));
            }
            if (pending.Count > 0)
            {
                // The library waits 500 ms for file stability. Explicit reconciliation then gives stable TrackIds.
                await Task.Delay(650, work).ConfigureAwait(false);
                await library.ReconcileAsync(false, work).ConfigureAwait(false);
                var catalog = (await library.QueryAsync(new(), work).ConfigureAwait(false)).Tracks.ToDictionary(t => LibraryText.NormalizePath(t.Path));
                for (var i = 0; i < rows.Count; i++)
                    if (pending.FirstOrDefault(p => p.Id == rows[i].ImportTrackId) is { Path: not null } copied)
                        rows[i] = catalog.TryGetValue(LibraryText.NormalizePath(copied.Path), out var indexed) && indexed.Availability == AvailabilityStatus.Available
                            ? rows[i] with { LibraryTrackId = indexed.Id, Status = "Importada" }
                            : rows[i] with { Status = "Indisponível", Error = "Arquivo copiado, mas a indexação ainda não o confirmou." };
            }
            for (var i = 0; i < rows.Count; i++)
                if (repeated.TryGetValue(rows[i].ImportTrackId, out var first))
                {
                    var source = rows.First(r => r.ImportTrackId == first);
                    rows[i] = source.LibraryTrackId is { } id
                        ? rows[i] with { LibraryTrackId = id, Status = "Reutilizada" }
                        : rows[i] with { Status = "Indisponível", Error = "A primeira cópia não foi indexada." };
                }
            if (cancelled)
            {
                var stopped = Result(null, rows, true);
                Observe(OperationalEventType.ImportCancelled, Summary(stopped));
                return stopped;
            }
            var ids = rows.Where(r => r.LibraryTrackId is not null).Select(r => r.LibraryTrackId!.Value).ToArray();
            Guid? playlistId = null;
            if (ids.Length > 0)
            {
                var created = await playlists.CreateAsync(playlistName.Trim(), "Importada pelo AHNSF Music; fontes e direitos verificados pelo operador.", token).ConfigureAwait(false);
                playlistId = created.Id;
                await playlists.AddAsync(created.Id, ids, token).ConfigureAwait(false);
            }
            var result = Result(playlistId, rows, false);
            Observe(result.Unavailable > 0 ? OperationalEventType.ImportFailed : OperationalEventType.ImportCompleted, Summary(result), result.Unavailable > 0 ? OperationalEventSeverity.Warning : OperationalEventSeverity.Information, playlistId);
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = Result(null, rows, true);
            Observe(OperationalEventType.ImportCancelled, Summary(result));
            return result;
        }
        catch
        {
            Observe(OperationalEventType.ImportFailed, "Importação interrompida por falha; arquivos concluídos foram preservados.", OperationalEventSeverity.Error);
            throw;
        }
        finally
        {
            // Staging is our own GUID folder in the temp root; library files were already copied out of it.
            if (staging is not null && Directory.Exists(staging))
                try { Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private async Task VerifyObtainedAsync(string path, ImportTrack expected, CancellationToken token)
    {
        ValidateSource(path);
        // spotDL tags the finished file; a file cut by cancellation or a different song does not carry the requested title.
        var track = await Task.Run(() => tags.Read(path, Path.GetDirectoryName(path)!, token), token).ConfigureAwait(false);
        if (track.Availability != AvailabilityStatus.Available || track.Duration <= TimeSpan.Zero ||
            LibraryText.Fold(track.Title.Trim()) != LibraryText.Fold(expected.Title.Trim()))
            throw new InvalidDataException("Arquivo obtido incompleto ou inválido.");
    }
    private static ImportResult Result(Guid? playlist, IReadOnlyList<ImportItemResult> rows, bool cancelled) =>
        new(playlist, rows.Count(r => r.Status == "Importada"), rows.Count(r => r.Status == "Reutilizada"), rows.Count(r => r.Status == "Indisponível"), cancelled, rows.ToArray());
    private static string Summary(ImportResult result) => $"Importação: {result.Imported} arquivo(s), {result.Duplicates} reutilizado(s), {result.Unavailable} indisponível(is).";
    private void Observe(OperationalEventType type, string description, OperationalEventSeverity severity = OperationalEventSeverity.Information, Guid? playlist = null)
    { try { history?.Record(type, "Importação", description, severity, OperationalActionOrigin.Manual, playlistId: playlist); } catch { /* Observability cannot affect import or playback. */ } }
    private static string Limit(string text) => string.IsNullOrWhiteSpace(text) ? "Sem título" : text.Trim()[..Math.Min(text.Trim().Length, 200)];
    private static string SafeError(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException ? "Arquivo indisponível."
        : ex is InvalidDataException ? "Arquivo inválido ou incompleto." : "Falha de leitura ou cópia do arquivo.";
    private static Guid? FindMetadataMatch(ImportTrack track, IEnumerable<Track> catalog) => catalog
        .Where(t => LibraryText.Fold(t.Title.Trim()) == LibraryText.Fold(track.Title.Trim()) && LibraryText.Fold(t.Artist.Trim()) == LibraryText.Fold(track.Artist.Trim())
            && Math.Abs((t.Duration - track.Duration).TotalSeconds) <= 2).Select(t => (Guid?)t.Id).FirstOrDefault();
    private static void ValidateSource(string source)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException();
        if (!source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || info.Length is < 1024 or > 200_000_000) throw new InvalidDataException("MP3 inválido.");
    }
    private static async Task<byte[]> HashAsync(string path, CancellationToken token)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true); return await SHA256.HashDataAsync(stream, token).ConfigureAwait(false); }
    private static async Task<string> CopyNewAsync(string source, string root, string name, CancellationToken token)
    {
        ValidateSource(source);
        var baseName = Sanitize(name);
        var target = Path.Combine(root, baseName + ".mp3");
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(root, $"{baseName} ({n}).mp3");
        var staging = Path.Combine(root, ".ahnsf-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            if (new FileInfo(staging).Length != new FileInfo(source).Length) throw new IOException("Cópia incompleta.");
            token.ThrowIfCancellationRequested();
            File.Move(staging, target, false); // Same-volume rename; never overwrite existing MP3.
            return target;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    public static string Sanitize(string title)
    {
        var value = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (value.Length > 100) value = value[..100].TrimEnd('.', ' ');
        if (value.Length == 0 || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "LPT1", "LPT2", "LPT3" }.Contains(value, StringComparer.OrdinalIgnoreCase)) value = "Música-" + Guid.NewGuid().ToString("N")[..8];
        return value;
    }
}
