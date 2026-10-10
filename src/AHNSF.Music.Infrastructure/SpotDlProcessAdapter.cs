using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AHNSF.Music.Core;

namespace AHNSF.Music.Infrastructure;

public sealed record ImportToolManifest(string SpotDlVersion, string SpotDlSha256, string FFmpegVersion, string FFmpegSha256,
    string? DenoVersion = null, string? DenoSha256 = null);
public sealed record ImportProcessResult(int ExitCode, string Stdout, string Stderr);
/// <summary>How the helper is launched. Production resolves verified packaged paths; tests inject a simulated process.</summary>
public sealed record SpotDlLaunch(string Executable, IReadOnlyList<string> PrefixArguments, string FFmpeg);

/// <summary>Runs only a pinned, packaged executable; never invokes a shell, PATH lookups, pip or dependency downloads.</summary>
public sealed class SpotDlProcessAdapter : IImportMetadataProvider
{
    public const string SupportedSpotDlVersion = "4.5.2";
    public const string SupportedFFmpegVersion = "9.0.2";
    public const string SupportedDenoVersion = "2.9.7";
    public static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(20); // Real run: 49 tracks took ~2 min with SpotipyFree.
    private readonly string root;
    private readonly SpotDlLaunch? simulated;
    private readonly TimeSpan perTrack;
    private readonly Serilog.ILogger? logger;
    private SpotDlLaunch? verified;
    private string? verifiedStamp;
    private bool denoReady;

    public SpotDlProcessAdapter(string installRoot, Serilog.ILogger? logger = null) : this(installRoot, null, null, logger) { }
    /// <summary>Test seam: <paramref name="launch"/> bypasses package verification and is never used by the application.</summary>
    public SpotDlProcessAdapter(string installRoot, SpotDlLaunch? launch, TimeSpan? perTrackTimeout, Serilog.ILogger? logger = null, bool simulatedDeno = true)
    {
        root = Path.GetFullPath(installRoot); simulated = launch; perTrack = perTrackTimeout ?? TimeSpan.FromMinutes(3);
        this.logger = logger; denoReady = launch is not null && simulatedDeno;
    }
    private string SpotDl => Path.Combine(root, "Tools", "spotdl", "spotdl.exe");
    private string FFmpeg => Path.Combine(root, "Tools", "ffmpeg", "ffmpeg.exe");
    private string Manifest => Path.Combine(root, "Tools", "manifest.json");
    private string Deno => Path.Combine(root, "Tools", "deno", "deno.exe");
    private string Stamp() => string.Join('|', new[] { SpotDl, FFmpeg, Manifest, Deno }.Select(p => new FileInfo(p))
        .Select(f => f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-"));

    public async Task<ImportToolStatus> DiagnoseAsync(CancellationToken token = default)
    {
        if (simulated is not null) return new(true, "Ferramentas simuladas.", SupportedSpotDlVersion, SupportedFFmpegVersion);
        verified = null;
        try
        {
            if (!File.Exists(Manifest) || !File.Exists(SpotDl) || !File.Exists(FFmpeg))
                return new(false, "Ferramentas internas ausentes. Inclua spotDL e FFmpeg no pacote verificado.");
            var manifest = JsonSerializer.Deserialize<ImportToolManifest>(await File.ReadAllTextAsync(Manifest, token).ConfigureAwait(false));
            if (manifest is null || manifest.SpotDlVersion != SupportedSpotDlVersion || manifest.FFmpegVersion != SupportedFFmpegVersion)
                return new(false, "Versão do pacote auxiliar incompatível.");
            if (!await MatchesAsync(SpotDl, manifest.SpotDlSha256, token).ConfigureAwait(false) ||
                !await MatchesAsync(FFmpeg, manifest.FFmpegSha256, token).ConfigureAwait(false))
                return new(false, "Integridade dos executáveis não confirmada (SHA-256).");
            // Deno is optional, but Tools\deno is on the helper PATH: a present binary must be the pinned, hashed one.
            var hasDeno = File.Exists(Deno);
            if (hasDeno && (manifest.DenoVersion != SupportedDenoVersion || !await MatchesAsync(Deno, manifest.DenoSha256 ?? "", token).ConfigureAwait(false)))
                return new(false, "Deno do pacote com versão ou integridade (SHA-256) não confirmada.");
            var work = CreateWork();
            try
            {
                var env = Environment(work);
                var version = await ImportProcessRunner.RunAsync(SpotDl, ["--version"], work, TimeSpan.FromSeconds(60), null, token, env).ConfigureAwait(false);
                var ffmpeg = await ImportProcessRunner.RunAsync(FFmpeg, ["-version"], work, TimeSpan.FromSeconds(15), null, token, env).ConfigureAwait(false);
                if (version.ExitCode != 0 || !(version.Stdout + version.Stderr).Contains(SupportedSpotDlVersion, StringComparison.Ordinal) ||
                    ffmpeg.ExitCode != 0 || !ffmpeg.Stdout.Contains(SupportedFFmpegVersion, StringComparison.Ordinal))
                    return new(false, "Executável ou versão interna incompatível.");
                if (hasDeno)
                {
                    var deno = await ImportProcessRunner.RunAsync(Deno, ["--version"], work, TimeSpan.FromSeconds(30), null, token, env).ConfigureAwait(false);
                    if (deno.ExitCode != 0 || !deno.Stdout.Contains("deno " + SupportedDenoVersion, StringComparison.Ordinal))
                        return new(false, "Deno do pacote incompatível.");
                }
            }
            finally { DeleteWork(work); }
            verified = new(SpotDl, [], FFmpeg); verifiedStamp = Stamp(); denoReady = hasDeno;
            return new(true, hasDeno
                ? $"Ferramentas verificadas: spotDL {SupportedSpotDlVersion}, FFmpeg {SupportedFFmpegVersion} e Deno {SupportedDenoVersion}."
                : $"Ferramentas verificadas: spotDL {SupportedSpotDlVersion} e FFmpeg {SupportedFFmpegVersion}. Deno ausente: alguns downloads do YouTube podem falhar.",
                SupportedSpotDlVersion, SupportedFFmpegVersion);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, "Falha ao verificar as ferramentas internas. Consulte os logs técnicos."); }
    }
    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    }
    private async Task<SpotDlLaunch> LaunchAsync(CancellationToken token)
    {
        if (simulated is not null) return simulated;
        // Re-verify hashes/versions whenever the packaged files change; otherwise avoid re-hashing ~150 MB per request.
        if (verified is not null && verifiedStamp == Stamp()) return verified;
        var status = await DiagnoseAsync(token).ConfigureAwait(false);
        if (!status.Ready || verified is null) throw new InvalidOperationException(status.Message);
        return verified;
    }

    public async Task<ImportPreview> PreviewAsync(ImportAddress address, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        if (address.Platform != ImportPlatform.Spotify) throw new NotSupportedException("O spotDL aceita links do Spotify. Playlists do YouTube não são suportadas nesta versão.");
        var launch = await LaunchAsync(token).ConfigureAwait(false);
        var work = CreateWork();
        try
        {
            var saved = Path.Combine(work, "preview.spotdl");
            progress?.Report(new(0, 0, "Consultando a playlist…"));
            // SpotipyFree (default since 4.5.0): public metadata only, no account, no credentials, no lyrics scraping.
            var args = launch.PrefixArguments.Concat(["save", address.Uri.AbsoluteUri, "--save-file", saved, "--lyrics",
                "--ffmpeg", launch.FFmpeg, "--log-level", "ERROR", "--simple-tui"]).ToArray();
            var result = await ImportProcessRunner.RunAsync(launch.Executable, args, work, PreviewTimeout, progress, token, Environment(work)).ConfigureAwait(false);
            var info = new FileInfo(saved);
            if (result.ExitCode != 0 || !info.Exists) throw new IOException("A consulta da playlist falhou. Verifique o link e a conexão com a internet.");
            if (info.Length is 0 or > 16_000_000) throw new InvalidDataException("Resposta de metadados inválida ou excessiva.");
            var preview = ParseSavedMetadata(await File.ReadAllTextAsync(saved, token).ConfigureAwait(false), address.Platform);
            if (preview.Tracks.Count == 0) throw new InvalidDataException("A playlist não contém faixas acessíveis.");
            return preview;
        }
        finally { DeleteWork(work); }
    }

    public static ImportPreview ParseSavedMetadata(string json, ImportPlatform platform)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Metadados de playlist inválidos.");
        var rows = new List<ImportTrack>(); string name = "Playlist consultada";
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (rows.Count == 5000) throw new InvalidDataException("A playlist excede 5.000 faixas.");
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var title = Text(entry, "name") ?? Text(entry, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var artist = Text(entry, "artist") ?? "Artista desconhecido";
            name = Text(entry, "list_name") ?? name;
            var position = entry.TryGetProperty("list_position", out var p) && p.TryGetInt32(out var number) && number >= 0 ? number : rows.Count;
            var duration = entry.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) && seconds is > 0 and < 86400 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
            rows.Add(new(Guid.NewGuid(), position, title[..Math.Min(title.Length, 200)], artist[..Math.Min(artist.Length, 200)], duration,
                SourceId: Text(entry, "song_id"), SourceData: entry.GetRawText()));
        }
        // Provider order is kept; positions become contiguous 0-based indexes for display and playlist order.
        var ordered = rows.Select((r, i) => (r, i)).OrderBy(x => x.r.Position).ThenBy(x => x.i).Select((x, i) => x.r with { Position = i }).ToArray();
        return new(name[..Math.Min(name.Length, 200)], platform, ordered, "Playlist online");
    }
    private static string? Text(JsonElement entry, string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async Task<ImportDownloadResult> DownloadAsync(IReadOnlyList<ImportTrack> tracks, string staging,
        IProgress<ImportProgress>? progress, CancellationToken token)
    {
        if (tracks.Count is 0 or > 5000 || tracks.Any(t => t.SourceData is null)) throw new ArgumentException("Faixas inválidas para obtenção.");
        if (!Path.IsPathFullyQualified(staging) || Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any())
            throw new ArgumentException("Diretório temporário de obtenção inválido.");
        var launch = await LaunchAsync(token).ConfigureAwait(false);
        var output = Path.Combine(staging, "audio");
        Directory.CreateDirectory(output);
        var work = CreateWork();
        try
        {
            // Our own 1-based index replaces list metadata, so each output file maps back to exactly one requested entry.
            var entries = new JsonArray();
            for (var i = 0; i < tracks.Count; i++)
            {
                if (JsonNode.Parse(tracks[i].SourceData!) is not JsonObject entry) throw new InvalidDataException("Metadados de faixa inválidos.");
                entry["list_position"] = i + 1; entry["list_length"] = tracks.Count;
                entries.Add(entry);
            }
            var request = Path.Combine(work, "request.spotdl");
            await File.WriteAllTextAsync(request, entries.ToJsonString(), new UTF8Encoding(false), token).ConfigureAwait(false);
            var args = launch.PrefixArguments.Concat(["download", request, "--output", Path.Combine(output, "{list-position}.{output-ext}"),
                "--format", "mp3", "--overwrite", "skip", "--lyrics", "--threads", "2", "--ffmpeg", launch.FFmpeg,
                "--log-level", "WARNING", "--simple-tui", "--print-errors"]).ToArray();
            var timeout = TimeSpan.FromTicks(Math.Min(ImportProcessRunner.MaximumTimeout.Ticks, perTrack.Ticks * (tracks.Count + 1L)));
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitor = ReportFilesAsync(output, tracks.Count, progress, poll.Token);
            // Cancellation/timeout kill the process tree but still return what finished; the caller verifies every file.
            ImportProcessResult? run = null; var stopped = ImportFailureKind.Unknown;
            try { run = await ImportProcessRunner.RunAsync(launch.Executable, args, work, timeout, null, token, Environment(work)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { stopped = ImportFailureKind.Cancelled; }
            catch (TimeoutException) { stopped = ImportFailureKind.Timeout; }
            finally { poll.Cancel(); await monitor.ConfigureAwait(false); }
            // Exit code is not trusted either way: only files that map to a requested index are returned.
            var files = new Dictionary<Guid, string>();
            foreach (var file in Directory.EnumerateFiles(output, "*.mp3"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var index) && index >= 1 && index <= tracks.Count
                    && new FileInfo(file).Length >= 1024)
                    files[tracks[index - 1].Id] = file;
            progress?.Report(new(files.Count, tracks.Count, $"{files.Count} de {tracks.Count} música(s) recebida(s); verificando arquivos…"));
            var missing = tracks.Where(t => !files.ContainsKey(t.Id)).ToArray();
            var failures = stopped != ImportFailureKind.Unknown
                ? missing.ToDictionary(t => t.Id, _ => stopped)
                : SpotDlOutputClassifier.Classify(run is null ? "" : run.Stdout + "\n" + run.Stderr, missing, denoReady);
            if (missing.Length > 0 && run is not null)
                logger?.Warning("spotDL não obteve {Missing} de {Total} faixa(s) ({Kinds}). Saída sanitizada:\n{Output}", missing.Length, tracks.Count,
                    string.Join(", ", failures.Values.GroupBy(k => k).Select(g => $"{g.Key}={g.Count()}")),
                    SpotDlOutputClassifier.Sanitize(run.Stdout + "\n" + run.Stderr, [staging, work, root]));
            return new(files, failures);
        }
        finally { DeleteWork(work); }
    }
    private static async Task ReportFilesAsync(string output, int total, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        try
        {
            var last = -1;
            while (true)
            {
                var count = Math.Min(total, Directory.EnumerateFiles(output, "*.mp3").Count());
                if (count != last) { last = count; progress?.Report(new(count, total, $"Baixando: {count} de {total} música(s)…")); }
                await Task.Delay(750, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    // Helper state (spotDL home, caches, config, PyInstaller extraction) lives in a per-run folder deleted afterwards.
    private static string CreateWork()
    {
        var work = Path.Combine(Path.GetTempPath(), "AHNSF Music", "Import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "home")); Directory.CreateDirectory(Path.Combine(work, "tmp"));
        return work;
    }
    private static void DeleteWork(string work)
    {
        // Only our own GUID child of the fixed import temp root is removable here.
        for (var attempt = 0; attempt < 3 && Path.GetFileName(work).Length == 32 && Directory.Exists(work); attempt++)
            try { Directory.Delete(work, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
    }
    private IReadOnlyDictionary<string, string?> Environment(string work)
    {
        var system = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        var home = Path.Combine(work, "home"); var temp = Path.Combine(work, "tmp");
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = home, ["HOME"] = home, ["TEMP"] = temp, ["TMP"] = temp,
            ["APPDATA"] = home, ["LOCALAPPDATA"] = home, ["XDG_CACHE_HOME"] = home,
            // Only packaged helpers and Windows itself are reachable: no global Python, FFmpeg or Deno.
            ["PATH"] = string.Join(';', new[] { Path.GetDirectoryName(FFmpeg)!, Path.Combine(root, "Tools", "deno"), system,
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows) }),
            ["PYTHONHOME"] = null, ["PYTHONPATH"] = null, ["PYTHONSTARTUP"] = null, ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1", ["COLUMNS"] = "4000", // Wide console: rich must not wrap error lines we classify.
            ["SPOTIPY_CLIENT_ID"] = null, ["SPOTIPY_CLIENT_SECRET"] = null, ["SPOTIPY_REDIRECT_URI"] = null,
            ["HTTP_PROXY"] = null, ["HTTPS_PROXY"] = null,
        };
    }
}

public static class ImportProcessRunner
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(8);
    public static async Task<ImportProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string work,
        TimeSpan timeout, IProgress<ImportProgress>? progress, CancellationToken token, IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !Path.IsPathFullyQualified(work) ||
            timeout < TimeSpan.FromSeconds(1) || timeout > MaximumTimeout) throw new ArgumentException("Processo auxiliar inválido.");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) { if (argument.Contains('\0') || argument.Length > 4096) throw new ArgumentException("Argumento inválido."); start.ArgumentList.Add(argument); }
        if (environment is not null)
            foreach (var (key, value) in environment) { if (value is null) start.Environment.Remove(key); else start.Environment[key] = value; }
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Processo auxiliar não iniciou.");
        process.StandardInput.Close(); // A helper waiting for console input gets EOF instead of hanging.
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
        // Keep the newest 256 KiB per stream: spotDL prints its error summary at the very end.
        var stdout = new Queue<string>(); var stderr = new Queue<string>();
        async Task CaptureAsync(StreamReader stream, Queue<string> target)
        {
            long size = 0;
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var kept = line[..Math.Min(line.Length, 4096)];
                lock (target) { target.Enqueue(kept); size += kept.Length; while (size > 262144 && target.Count > 1) size -= target.Dequeue().Length; }
                // spotDL console progress is unstable. Display only a generic heartbeat, never parse it as a contract.
                progress?.Report(new(0, 0, "Ferramenta auxiliar em execução…"));
            }
        }
        var outTask = CaptureAsync(process.StandardOutput, stdout);
        var errTask = CaptureAsync(process.StandardError, stderr);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return new(process.ExitCode, string.Join('\n', stdout), string.Join('\n', stderr));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Tempo limite da ferramenta auxiliar excedido.");
        }
    }
}

/// <summary>
/// Maps spotDL/yt-dlp console text to a failure kind per track. Text is diagnostic only: a pattern that is not
/// recognised degrades to Unknown, never to success. spotDL 4.5.2 ends with "--print-errors" lines
/// "{spotify url} - {Exception}: {message}"; yt-dlp writes its own "ERROR: [youtube] {id}: ..." lines.
/// </summary>
public static class SpotDlOutputClassifier
{
    private static readonly Regex SongError = new(@"(?<song>https://open\.spotify\.com/track/[A-Za-z0-9]+)\s+-\s+(?<type>[A-Za-z]+(?:Error|Exception))\s*:\s*(?<message>.*)", RegexOptions.CultureInvariant);
    private static readonly Regex VideoId = new(@"(?:[?&]v=|youtu\.be/|/shorts/)(?<id>[A-Za-z0-9_-]{11})", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<Guid, ImportFailureKind> Classify(string output, IReadOnlyList<ImportTrack> missing, bool denoAvailable)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var errors = new Dictionary<string, (string Type, string Message)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
            if (SongError.Match(line) is { Success: true } m) errors[m.Groups["song"].Value] = (m.Groups["type"].Value, m.Groups["message"].Value);
        var global = Global(lines, denoAvailable);
        var result = new Dictionary<Guid, ImportFailureKind>();
        foreach (var track in missing)
        {
            if (SongUrl(track) is not { } url || !errors.TryGetValue(url, out var error)) { result[track.Id] = global ?? ImportFailureKind.Unknown; continue; }
            // yt-dlp's reason is on its own line, keyed by the video id spotDL quotes in its error.
            var detail = VideoId.Match(error.Message) is { Success: true } v
                ? string.Join("\n", lines.Where(l => l.Contains(v.Groups["id"].Value, StringComparison.Ordinal) && l.Contains("ERROR", StringComparison.Ordinal)))
                : "";
            result[track.Id] = error.Type switch
            {
                "LookupError" => ImportFailureKind.NotFound,
                "FFmpegError" => ImportFailureKind.Conversion,
                _ => FromText(error.Message + "\n" + detail, denoAvailable)
                    ?? (error.Type == "AudioProviderError" && detail.Length == 0 ? global : null) ?? ImportFailureKind.Unknown,
            };
        }
        return result;
    }
    private static ImportFailureKind? Global(IEnumerable<string> lines, bool deno) =>
        FromText(string.Join("\n", lines.Where(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("Deno", StringComparison.Ordinal))), deno);
    private static ImportFailureKind? FromText(string text, bool deno)
    {
        bool Has(params string[] parts) => parts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("not a bot", "Sign in to confirm", "confirm your age", "HTTP Error 403", "HTTP Error 429", "Too Many Requests")) return ImportFailureKind.SourceVerification;
        if (!deno && Has("require Deno", "JavaScript runtime", "js runtime", "n challenge", "nsig", "Signature extraction failed")) return ImportFailureKind.RuntimeMissing;
        if (Has("Video unavailable", "Private video", "is not available", "has been removed", "No results found", "Could not find a match")) return ImportFailureKind.NotFound;
        if (Has("ffmpeg", "Conversion failed")) return ImportFailureKind.Conversion;
        if (Has("getaddrinfo", "Failed to resolve", "Temporary failure in name resolution", "Network is unreachable", "Connection reset",
            "Connection refused", "timed out", "ConnectionError", "URLError")) return ImportFailureKind.Network;
        return null;
    }
    private static string? SongUrl(ImportTrack track)
    {
        try { return JsonNode.Parse(track.SourceData ?? "")?["url"]?.GetValue<string>()?.Split('?')[0]; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Last lines for the technical log: no local paths, query strings (except the YouTube video id) or tokens.</summary>
    public static string Sanitize(string output, IEnumerable<string> paths)
    {
        var text = output;
        foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)).OrderByDescending(p => p.Length)) text = text.Replace(path, "<pasta>", StringComparison.OrdinalIgnoreCase);
        var profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) text = text.Replace(profile, "<perfil>", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(https?://[^\s?#""']+)\?([^\s#""']*)", m =>
        {
            var id = Regex.Match(m.Groups[2].Value, @"(?:^|&)v=([A-Za-z0-9_-]{11})");
            return m.Groups[1].Value + (id.Success ? "?v=" + id.Groups[1].Value : "");
        });
        text = Regex.Replace(text, @"(?i)\b(token|cookies?|authorization|secret|password|api[_-]?key)(\s*[=:]\s*)\S+", "$1$2<removido>");
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).TakeLast(60).Select(l => l.Length > 400 ? l[..400] + "…" : l);
        return string.Join("\n", lines);
    }
}
