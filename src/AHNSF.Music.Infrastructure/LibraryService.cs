using System.Diagnostics;
using System.Threading.Channels;
using AHNSF.Music.Core;
using Serilog;

namespace AHNSF.Music.Infrastructure;

public sealed record LibraryOptions
{
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan StabilityDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromMinutes(5);
    public bool EnableAutomaticSync { get; init; } = true;
    public bool UseFileSystemWatcher { get; init; } = true;
}

public sealed class LibraryService(ILibraryRepository repository, ITrackMetadataReader metadata, IArtworkCache artwork,
    ILogger logger, LibraryOptions? options = null, IOperationalHistoryService? history = null) : ILibraryService
{
    private readonly LibraryOptions options = options ?? new();
    private readonly SemaphoreSlim switching = new(1, 1);
    private readonly SemaphoreSlim indexing = new(1, 1);
    private readonly object stateLock = new();
    private CancellationTokenSource? session;
    private string? root;
    private FileSystemWatcher? watcher;
    private Task? worker;
    private Channel<bool>? signals;
    private int watcherFault;
    private bool disposed;
    private bool historyUnavailable;
    private DateTime lastCacheCleanupUtc;
    public event EventHandler<LibraryNotification>? Changed;

    public async Task SetDirectoryAsync(string directory, CancellationToken token)
    {
        await switching.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopSessionAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (stateLock)
            {
                root = Path.GetFullPath(directory);
                session = CancellationTokenSource.CreateLinkedTokenSource(token);
                signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
            }
            if (options.UseFileSystemWatcher) StartWatcher();
            var active = session; var channel = signals; var activeDirectory = root!;
            if (options.EnableAutomaticSync) worker = Task.Run(() => MonitorAsync(channel!, activeDirectory, active!.Token));
        }
        finally { switching.Release(); }
    }
    // Event signals are coalesced. Tests may simulate duplicates and overflow through these entry points.
    public void SignalChange() => signals?.Writer.TryWrite(true);
    public void ReportWatcherError(Exception error)
    {
        logger.Warning(error, "Erro do FileSystemWatcher; reconciliação completa solicitada");
        Interlocked.Exchange(ref watcherFault, 1); SignalChange();
    }
    private void StartWatcher()
    {
        watcher?.Dispose(); watcher = null;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                Filter = "*", IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024
            };
            watcher.Created += OnFileEvent; watcher.Deleted += OnFileEvent; watcher.Changed += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += (sender, e) => { if (ReferenceEquals(sender, watcher)) ReportWatcherError(e.GetException()); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { logger.Warning(ex, "Monitoramento indisponível: {Root}", root); watcher?.Dispose(); watcher = null; }
    }
    private void OnFileEvent(object sender, FileSystemEventArgs args) { if (ReferenceEquals(sender, watcher)) SignalChange(); }
    private async Task MonitorAsync(Channel<bool> channel, string activeDirectory, CancellationToken token)
    {
        // Catalog is loaded by the UI before the first background reconciliation starts.
        channel.Writer.TryWrite(true);
        var nextInterval = options.ReconcileInterval;
        int pendingRetries = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(nextInterval);
                try { await channel.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(options.Debounce, token).ConfigureAwait(false);
                while (channel.Reader.TryRead(out _)) { }
                try
                {
                    var result = await ReconcileAsync(false, token).ConfigureAwait(false);
                    if (result.Pending > 0)
                        nextInterval = TimeSpan.FromMilliseconds(Math.Min(options.ReconcileInterval.TotalMilliseconds,
                            options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(pendingRetries++, 6))));
                    else { pendingRetries = 0; nextInterval = options.ReconcileInterval; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                { logger.Error(ex, "Falha de sincronização da biblioteca"); Changed?.Invoke(this, new("Falha de indexação. Consulte os logs e tente atualizar a biblioteca.", RootPath: activeDirectory)); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async Task<LibraryResult> QueryAsync(LibraryQuery query, CancellationToken token)
    {
        string? directory; lock (stateLock) directory = root;
        return directory is null ? new LibraryResult([], 0, [], []) : await repository.QueryAsync(directory, query, token).ConfigureAwait(false);
    }
    public async Task<LibrarySyncResult> ReconcileAsync(bool force, CancellationToken token)
    {
        string directory; CancellationToken sessionToken;
        lock (stateLock) { directory = root ?? throw new InvalidOperationException("Pasta não configurada."); sessionToken = session!.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        await indexing.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await Task.Run(() => ReconcileCoreAsync(directory, force, linked.Token), linked.Token).ConfigureAwait(false); }
        finally { indexing.Release(); }
    }
    private async Task<LibrarySyncResult> ReconcileCoreAsync(string directory, bool force, CancellationToken token)
    {
        Changed?.Invoke(this, new("Sincronizando biblioteca…", true, RootPath: directory));
        var clock = Stopwatch.StartNew();
        var warnings = new List<string>();
        if (!Directory.Exists(directory)) return Unavailable();
        if (options.UseFileSystemWatcher && (watcher is null || Interlocked.Exchange(ref watcherFault, 0) != 0)) StartWatcher();
        var stored = (await repository.LoadAsync(directory, token, includeMissing: false).ConfigureAwait(false)).ToDictionary(t => LibraryText.NormalizePath(t.Path));
        var seen = new HashSet<string>(); var files = new List<FileInfo>(); var incomplete = false;
        var folders = new Stack<string>(); folders.Push(directory);
        while (folders.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var child in Directory.GetDirectories(folder))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) folders.Push(child);
                foreach (var path in Directory.GetFiles(folder).Where(p => Path.GetExtension(p).Equals(".mp3", StringComparison.OrdinalIgnoreCase)))
                { seen.Add(LibraryText.NormalizePath(path)); files.Add(new FileInfo(path)); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Diretório não pode ser enumerado: {Folder}", folder);
                if (folder == directory) return Unavailable();
                incomplete = true; warnings.Add($"Subpasta indisponível: {folder}");
            }
        }
        var candidates = new List<(FileInfo File, long Size, DateTime Modified, Track? Old)>();
        int pending = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); stored.TryGetValue(LibraryText.NormalizePath(file.FullName), out var old);
            try
            {
                var size = file.Length; var modified = file.LastWriteTimeUtc;
                if (!force && old is not null && old.FileSize == size && old.LastModifiedUtc == modified && old.Availability != AvailabilityStatus.Missing && (old.ArtworkCacheKey is null || artwork.GetPath(old.ArtworkCacheKey) is not null)) continue;
                candidates.Add((file, size, modified, old));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.Warning(ex, "Arquivo indisponível: {Path}", file.FullName); pending++; }
        }
        if (historyUnavailable) { historyUnavailable = false; history.Observe(OperationalEventType.LibraryRecovered, "Biblioteca", "Disponibilidade da pasta recuperada.", origin: OperationalActionOrigin.Library); }
        if (candidates.Count > 0) history.Observe(OperationalEventType.IndexingStarted, "Biblioteca", $"Indexação de {candidates.Count} candidato(s) iniciada.", origin: OperationalActionOrigin.Library);
        if (candidates.Count > 0) await Task.Delay(options.StabilityDelay, token).ConfigureAwait(false);
        var changed = new List<Track>(); int processed = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                candidate.File.Refresh();
                if (!candidate.File.Exists || candidate.Size == 0 || candidate.File.Length != candidate.Size || candidate.File.LastWriteTimeUtc != candidate.Modified) { pending++; continue; }
                // Deny writers during metadata extraction; partially copied files are retried.
                using var guard = File.Open(candidate.File.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                var track = metadata.Read(candidate.File.FullName, directory, token);
                candidate.File.Refresh();
                if (candidate.File.Length != candidate.Size || candidate.File.LastWriteTimeUtc != candidate.Modified) { pending++; continue; }
                if (candidate.Old is not null) track = track with { Id = candidate.Old.Id, IndexedAtUtc = candidate.Old.IndexedAtUtc };
                changed.Add(track); processed++;
                if (track.Availability == AvailabilityStatus.Invalid) warnings.Add($"MP3 inválido: {track.FileName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.Warning(ex, "Arquivo em cópia, uso ou indisponível: {Path}", candidate.File.FullName); pending++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.Warning(ex, "Falha individual na indexação: {Path}", candidate.File.FullName); warnings.Add($"Não foi possível indexar {candidate.File.Name}."); }
        }
        // Any inaccessible subtree makes deletion detection conservative. Recheck root before committing.
        if (!Directory.Exists(directory)) return Unavailable();
        if (!incomplete)
            foreach (var old in stored.Values.Where(t => t.Availability != AvailabilityStatus.Missing && !seen.Contains(LibraryText.NormalizePath(t.Path))))
                changed.Add(old with { Availability = AvailabilityStatus.Missing, ArtworkCacheKey = null });
        await repository.ApplyAsync(changed, token).ConfigureAwait(false);
        if (changed.Count > 0 || DateTime.UtcNow - lastCacheCleanupUtc >= TimeSpan.FromHours(1))
        {
            try { artwork.Clean(await repository.ArtworkKeysAsync(token).ConfigureAwait(false), token); lastCacheCleanupUtc = DateTime.UtcNow; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.Warning(ex, "Falha de limpeza do cache"); warnings.Add("Não foi possível limpar o cache de capas."); }
        }
        if (candidates.Count > 0 || changed.Count > 0) history.Observe(OperationalEventType.IndexingCompleted, "Biblioteca", $"Indexação concluída: {processed} lidos, {changed.Count} alterações, {pending} pendências.", origin: OperationalActionOrigin.Library);
        if (warnings.Count > 0) history.Observe(incomplete ? OperationalEventType.LibraryAccessFailed : OperationalEventType.LibraryReadError, "Biblioteca", $"Indexação com {warnings.Count} aviso(s); consulte o log técnico.", OperationalEventSeverity.Warning, OperationalActionOrigin.Library);
        logger.Information("Biblioteca: {Processed} lidos, {Changed} alterados, {Pending} pendentes em {Ms} ms", processed, changed.Count, pending, clock.ElapsedMilliseconds);
        var message = warnings.Count > 0 ? $"Biblioteca sincronizada com {warnings.Count} aviso(s). {warnings[0]}" : pending > 0 ? $"{pending} arquivo(s) ainda em cópia ou uso. Nova tentativa automática." : "Biblioteca sincronizada.";
        if (watcher is null) message += " Monitoramento indisponível; reconciliação periódica ativa.";
        Changed?.Invoke(this, new(message, RootPath: directory));
        return new(true, processed, changed.Count, pending, warnings);
        LibrarySyncResult Unavailable()
        {
            if (!historyUnavailable) { historyUnavailable = true; history.Observe(OperationalEventType.LibraryUnavailable, "Biblioteca", "Pasta indisponível; catálogo preservado.", OperationalEventSeverity.Warning, OperationalActionOrigin.Library); }
            logger.Warning("Pasta da biblioteca indisponível: {Root}; catálogo preservado", directory);
            Changed?.Invoke(this, new("Pasta indisponível. Catálogo preservado; verifique o caminho e as permissões.", false, false, directory));
            return new(false, 0, 0, 0, ["Pasta indisponível."]);
        }
    }
    private async Task StopSessionAsync()
    {
        session?.Cancel(); watcher?.Dispose(); watcher = null;
        if (worker is not null) await worker.ConfigureAwait(false);
        await indexing.WaitAsync().ConfigureAwait(false);
        indexing.Release();
        watcher?.Dispose(); watcher = null;
        lock (stateLock) { session?.Dispose(); session = null; signals = null; worker = null; }
    }
    public async ValueTask DisposeAsync()
    {
        await switching.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; await StopSessionAsync().ConfigureAwait(false); }
        finally { switching.Release(); }
    }
}
