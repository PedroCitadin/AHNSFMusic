using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

/// <summary>Phase 0.8.2: link → preview → download → library/playlist, with a simulated spotDL process (no network, no real downloads).</summary>
public sealed class PlaylistDownloadTests
{
    private const string Url = "https://open.spotify.com/playlist/1234567890abcdef123456?si=secret";
    private const string FakeSpotDl = """
        $op = $args[0]
        $dir = $PSScriptRoot
        $mode = if (Test-Path -LiteralPath (Join-Path $dir 'mode.txt')) { (Get-Content -LiteralPath (Join-Path $dir 'mode.txt')).Trim() } else { '' }
        Add-Content -LiteralPath (Join-Path $dir 'calls.txt') -Value ($args -join "`n") -Encoding UTF8
        Add-Content -LiteralPath (Join-Path $dir 'calls.txt') -Value '----' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $dir 'env.txt') -Value "$env:USERPROFILE`n$env:TEMP`n$env:PATH" -Encoding UTF8
        if ($op -eq 'save') {
            $target = $args[[array]::IndexOf($args, '--save-file') + 1]
            Copy-Item -LiteralPath (Join-Path $dir 'playlist.json') -Destination $target
            exit 0
        }
        if ($op -eq 'download') {
            $request = [IO.File]::ReadAllText($args[1], [Text.Encoding]::UTF8) | ConvertFrom-Json
            Copy-Item -LiteralPath $args[1] -Destination (Join-Path $dir ('request-' + [guid]::NewGuid().ToString('N') + '.json'))
            $output = $args[[array]::IndexOf($args, '--output') + 1]
            if ($mode -eq 'hang') { Start-Sleep -Seconds 30 }
            if ($mode -eq 'bot') {
                foreach ($entry in $request) {
                    [Console]::Error.WriteLine("ERROR: [youtube] AbCdEfGhIjK: Sign in to confirm you" + [char]0x2019 + "re not a bot. Use --cookies-from-browser or --cookies for the authentication.")
                    [Console]::Error.WriteLine("WARNING: Some YouTube downloads require Deno. Run spotdl --download-deno or install Deno system-wide.")
                    Write-Output ($entry.url + " - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=AbCdEfGhIjK&si=segredo")
                }
                exit 1
            }
            foreach ($entry in $request) {
                $source = Join-Path $dir ($entry.song_id + '.mp3')
                if (-not (Test-Path -LiteralPath $source)) { Write-Output "LookupError: $($entry.name)"; continue }
                $target = $output.Replace('{list-position}', [string]$entry.list_position).Replace('{output-ext}', 'mp3')
                Copy-Item -LiteralPath $source -Destination $target
                if ($mode -eq 'slow') { Start-Sleep -Seconds 30 }
            }
            exit 1
        }
        exit 2
        """;

    private sealed class Harness : IAsyncDisposable
    {
        public LibraryFixture F { get; } = new();
        public string Tool => Path.Combine(F.Base, "fake spot dl");
        public SqlitePlaylistService Playlists { get; private set; } = null!;
        public PlaylistImportService Service { get; private set; } = null!;
        public OperationalHistoryService? History { get; private set; }
        public SpotDlProcessAdapter Adapter { get; private set; } = null!;
        public async Task StartAsync(string json, TimeSpan? perTrack = null, bool history = false, YouTubeCookieSetting? cookies = null)
        {
            Directory.CreateDirectory(Tool);
            await File.WriteAllTextAsync(Path.Combine(Tool, "spotdl.ps1"), FakeSpotDl);
            await File.WriteAllTextAsync(Path.Combine(Tool, "playlist.json"), json);
            await F.StartAsync();
            Playlists = new SqlitePlaylistService(F.Database, F.Repository);
            Adapter = new SpotDlProcessAdapter(F.Base, new(PowerShell(), ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Tool, "spotdl.ps1")],
                Path.Combine(Tool, "ffmpeg.exe")), perTrack, cookies: cookies);
            if (history) { History = new(new SqliteOperationalHistoryRepository(F.Database, F.Repository)); await History.StartAsync(false); }
            Service = new PlaylistImportService(Adapter, F.Service, Playlists, F.Metadata, History);
        }
        public string Song(string id, string title, string artist) => F.WriteTrack($"../fake spot dl/{id}.mp3", title, artist);
        public void Mode(string mode) => File.WriteAllText(Path.Combine(Tool, "mode.txt"), mode);
        public string Calls => File.ReadAllText(Path.Combine(Tool, "calls.txt"));
        public string[] Requests => Directory.GetFiles(Tool, "request-*.json");
        public async ValueTask DisposeAsync() { if (History is not null) await History.DisposeAsync(); await F.DisposeAsync(); }
    }

    private static string Entry(string id, string title, string artist, int position, string list = "Sala de espera") =>
        $$"""{"name":"{{title}}","artists":["{{artist}}"],"artist":"{{artist}}","duration":1,"song_id":"{{id}}","url":"https://open.spotify.com/track/{{id}}","list_name":"{{list}}","list_position":{{position}},"list_length":3}""";
    private static string Playlist(params string[] entries) => "[" + string.Join(",", entries) + "]";
    private static string PowerShell() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    private static HashSet<Guid> All(ImportPreview preview) => preview.Tracks.Select(t => t.Id).ToHashSet();

    [Fact] public async Task PreviewRunsSaveWithSafeArgumentsIsolatedEnvironmentAndKeepsOrder()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("bbbbbbbbbb", "Segunda", "B", 2), Entry("aaaaaaaaaa", "Primeira", "A", 1)));
        var preview = await h.Service.PreviewUrlAsync(Url);
        Assert.Equal("Sala de espera", preview.Name);
        Assert.Equal(ImportPlatform.Spotify, preview.Platform);
        Assert.Equal(new[] { "Primeira", "Segunda" }, preview.Tracks.Select(t => t.Title));
        Assert.Equal(new[] { 0, 1 }, preview.Tracks.Select(t => t.Position));
        Assert.All(preview.Tracks, t => { Assert.True(t.Downloadable); Assert.False(t.Available); });
        var args = h.Calls.Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal("save", args[0]);
        Assert.Contains("https://open.spotify.com/playlist/1234567890abcdef123456", args);
        Assert.DoesNotContain(args, a => a.Contains("secret") || a == "--use-official-api" || a == "--cookie-file" || a == "--user-auth");
        Assert.Contains("--lyrics", args);
        var env = File.ReadAllLines(Path.Combine(h.Tool, "env.txt"));
        var importRoot = Path.Combine(Path.GetTempPath(), "AHNSF Music", "Import");
        Assert.StartsWith(importRoot, env[0]); Assert.StartsWith(importRoot, env[1]);
        Assert.DoesNotContain("Python", env[2], StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.GetDirectoryName(env[0])), "Diretório temporário da ferramenta deve ser removido.");
    }

    [Fact] public async Task YouTubePlaylistIsNotSentToSpotDl()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1)));
        await Assert.ThrowsAsync<NotSupportedException>(() => h.Service.PreviewUrlAsync("https://www.youtube.com/playlist?list=PLabcdefgh123"));
        Assert.False(File.Exists(Path.Combine(h.Tool, "calls.txt")));
    }

    [Fact] public async Task DownloadMovesVerifiedFilesIndexesAndCreatesOrderedPlaylistWithPartialFailure()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("cccccccccc", "Terceira", "C", 1), Entry("missingxyz", "Indisponível", "X", 2), Entry("aaaaaaaaaa", "Primeira", "A", 3)), history: true);
        h.Song("cccccccccc", "Terceira", "C"); h.Song("aaaaaaaaaa", "Primeira", "A");
        var preview = await h.Service.PreviewUrlAsync(Url);
        var progress = new List<ImportProgress>();
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Sala de espera", new Sync<ImportProgress>(progress.Add), default);
        Assert.False(result.Cancelled);
        Assert.Equal(2, result.Imported); Assert.Equal(1, result.Unavailable); Assert.Equal(0, result.Duplicates);
        Assert.Equal("Não foi possível obter o áudio desta faixa.", result.Items[1].Error);
        var saved = (await h.Playlists.GetAsync(result.PlaylistId!.Value))!;
        Assert.Equal("Sala de espera", saved.Playlist.Name);
        Assert.Equal(new[] { "Terceira", "Primeira" }, saved.Items.Select(i => i.Track!.Title));
        Assert.True(File.Exists(Path.Combine(h.F.Music, "C - Terceira.mp3")));
        Assert.True(File.Exists(Path.Combine(h.F.Music, "A - Primeira.mp3")));
        Assert.Equal(2, (await h.F.Service.QueryAsync(new(), default)).Count);
        Assert.Contains(progress, p => p.Total == 3 && p.Message.StartsWith("Baixando"));
        var args = h.Calls.Split("----")[1].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("download", args[0]);
        Assert.EndsWith("{list-position}.{output-ext}", args[Array.IndexOf(args, "--output") + 1]);
        Assert.DoesNotContain(h.F.Music, args[Array.IndexOf(args, "--output") + 1]);
        await h.History!.FlushAsync();
        var events = (await h.History.QueryAsync(new())).Events;
        var failed = Assert.Single(events, e => e.Type == OperationalEventType.ImportFailed);
        Assert.Contains("2 arquivo(s)", failed.Description); Assert.Contains("1 indisponível", failed.Description);
        Assert.DoesNotContain("https:", failed.Description);
    }

    [Fact] public async Task RetryDownloadsOnlyMissingTracksAndDoesNotDuplicateFiles()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1), Entry("bbbbbbbbbb", "Segunda", "B", 2)));
        h.Song("aaaaaaaaaa", "Primeira", "A");
        var first = await h.Service.PreviewUrlAsync(Url);
        var r1 = await h.Service.ImportAsync(first, All(first), h.F.Music, "Tentativa 1", null, default);
        Assert.Equal(1, r1.Imported); Assert.Equal(1, r1.Unavailable);
        h.Song("bbbbbbbbbb", "Segunda", "B");
        var second = await h.Service.PreviewUrlAsync(Url);
        Assert.NotNull(second.Tracks[0].ExistingTrackId); Assert.True(second.Tracks[1].Downloadable);
        var r2 = await h.Service.ImportAsync(second, All(second), h.F.Music, "Tentativa 2", null, default);
        Assert.Equal(1, r2.Imported); Assert.Equal(1, r2.Duplicates); Assert.Equal(0, r2.Unavailable);
        Assert.Equal(2, Directory.GetFiles(h.F.Music, "*.mp3").Length);
        var lastRequest = h.Requests.OrderBy(File.GetLastWriteTimeUtc).Last();
        Assert.DoesNotContain("aaaaaaaaaa", await File.ReadAllTextAsync(lastRequest));
        Assert.Equal(new[] { "Primeira", "Segunda" }, (await h.Playlists.GetAsync(r2.PlaylistId!.Value))!.Items.Select(i => i.Track!.Title));
    }

    [Fact] public async Task RepeatedSongInPlaylistIsDownloadedOnceAndKeepsBothOccurrences()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1), Entry("bbbbbbbbbb", "Segunda", "B", 2), Entry("aaaaaaaaaa", "Primeira", "A", 3)));
        h.Song("aaaaaaaaaa", "Primeira", "A"); h.Song("bbbbbbbbbb", "Segunda", "B");
        var preview = await h.Service.PreviewUrlAsync(Url);
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Repetida", null, default);
        Assert.Equal(2, result.Imported); Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, Directory.GetFiles(h.F.Music, "*.mp3").Length);
        Assert.Equal(new[] { "Primeira", "Segunda", "Primeira" }, (await h.Playlists.GetAsync(result.PlaylistId!.Value))!.Items.Select(i => i.Track!.Title));
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(h.Requests))).RootElement.GetArrayLength());
    }

    [Fact] public async Task ExistingMusicFileWithSameNameIsNeverOverwritten()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1)));
        var existing = h.F.WriteTrack("A - Primeira.mp3", "Outra música", "Outro");
        var before = await File.ReadAllBytesAsync(existing);
        await h.F.Service.ReconcileAsync(false, default);
        h.Song("aaaaaaaaaa", "Primeira", "A");
        var preview = await h.Service.PreviewUrlAsync(Url);
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Sem sobrescrita", null, default);
        Assert.Equal(1, result.Imported);
        Assert.Equal(before, await File.ReadAllBytesAsync(existing));
        Assert.True(File.Exists(Path.Combine(h.F.Music, "A - Primeira (2).mp3")));
    }

    [Fact] public async Task FileWithWrongTagsFromHelperIsRejected()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1)));
        h.Song("aaaaaaaaaa", "Música errada", "Z");
        var preview = await h.Service.PreviewUrlAsync(Url);
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Rejeitada", null, default);
        Assert.Equal(0, result.Imported); Assert.Equal(1, result.Unavailable); Assert.Null(result.PlaylistId);
        Assert.Empty(Directory.GetFiles(h.F.Music, "*.mp3"));
    }

    [Fact] public async Task CancellationKeepsFinishedDownloadsIndexedWithoutPlaylist()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1), Entry("bbbbbbbbbb", "Segunda", "B", 2)), history: true);
        h.Song("aaaaaaaaaa", "Primeira", "A"); h.Song("bbbbbbbbbb", "Segunda", "B"); h.Mode("slow");
        var preview = await h.Service.PreviewUrlAsync(Url);
        using var cts = new CancellationTokenSource();
        var progress = new Sync<ImportProgress>(p => { if (p.Completed >= 1 && p.Message.StartsWith("Baixando")) cts.Cancel(); });
        var started = DateTime.UtcNow;
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Cancelada", progress, cts.Token);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(25), "O processo auxiliar deve ser encerrado no cancelamento.");
        Assert.True(result.Cancelled); Assert.Null(result.PlaylistId);
        Assert.Equal(1, result.Imported); Assert.Equal("Download cancelado.", result.Items[1].Error);
        Assert.Empty(await h.Playlists.ListAsync());
        Assert.Single(Directory.GetFiles(h.F.Music, "*.mp3"));
        Assert.Single((await h.F.Service.QueryAsync(new(), default)).Tracks);
        await h.History!.FlushAsync();
        Assert.Single((await h.History.QueryAsync(new(Type: OperationalEventType.ImportCancelled))).Events);
    }

    [Fact] public async Task TimeoutStopsHelperAndReportsTracksAsUnavailable()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1)), perTrack: TimeSpan.FromSeconds(2));
        h.Song("aaaaaaaaaa", "Primeira", "A"); h.Mode("hang");
        var preview = await h.Service.PreviewUrlAsync(Url);
        var started = DateTime.UtcNow;
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Tempo", null, default);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(25));
        Assert.False(result.Cancelled); Assert.Equal(0, result.Imported); Assert.Equal(1, result.Unavailable); Assert.Null(result.PlaylistId);
    }

    [Fact] public async Task DownloadDoesNotTouchActivePlayerOrQueue()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1)));
        h.Song("aaaaaaaaaa", "Primeira", "A");
        var audio = new PlayingAudio(); using var player = new PlaybackService(audio);
        var current = new Track(Path.Combine(h.F.Base, "tocando.mp3"), "Tocando", "Operador", TimeSpan.FromMinutes(3));
        player.PlayLibrary(current, [current]);
        var preview = await h.Service.PreviewUrlAsync(Url);
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Simultânea", null, default);
        Assert.Equal(1, result.Imported);
        Assert.Equal(PlayerState.Playing, player.State); Assert.Same(current, player.CurrentTrack);
        Assert.Equal(1, audio.Loads);
    }

    [Fact] public async Task DownloadRequestRewritesPositionsAndRejectsEntriesWithoutSourceData()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 7)));
        var preview = await h.Service.PreviewUrlAsync(Url);
        var staging = Path.Combine(h.F.Base, "staging with spaces");
        await h.Adapter.DownloadAsync(preview.Tracks, staging, null, default);
        using var request = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(h.Requests)));
        Assert.Equal(1, request.RootElement[0].GetProperty("list_position").GetInt32());
        Assert.Equal(1, request.RootElement[0].GetProperty("list_length").GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(() => h.Adapter.DownloadAsync([preview.Tracks[0] with { SourceData = null }], Path.Combine(h.F.Base, "other"), null, default));
    }

    // Regression for the first real run (spotDL 4.5.2): YouTube "Sign in to confirm you're not a bot" plus the Deno warning.
    private const string RealRunOutput = """
        WARNING: Some YouTube downloads require Deno. Run spotdl --download-deno or install Deno system-wide.
        ERROR: [youtube] AbCdEfGhIjK: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.
        ERROR: [youtube] NnChallenge: n challenge solving failed: Some formats may be missing. Ensure you have a supported JavaScript runtime
        ERROR: [youtube] NetFailure1: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>
        https://open.spotify.com/track/aaaaaaaaaa - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=AbCdEfGhIjK
        https://open.spotify.com/track/bbbbbbbbbb - LookupError: No results found for song: B - Segunda
        https://open.spotify.com/track/cccccccccc - FFmpegError: Failed to convert, preview: Conversion failed
        https://open.spotify.com/track/dddddddddd - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=NnChallenge
        https://open.spotify.com/track/eeeeeeeeee - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=NetFailure1
        """;
    private static ImportTrack Online(string id) => new(Guid.NewGuid(), 0, id, "A", TimeSpan.FromSeconds(1), SourceId: id,
        SourceData: $$"""{"name":"{{id}}","url":"https://open.spotify.com/track/{{id}}","song_id":"{{id}}"}""");

    [Fact] public void ClassifierSeparatesVerificationRuntimeNotFoundConversionAndNetwork()
    {
        var tracks = new[] { "aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc", "dddddddddd", "eeeeeeeeee", "ffffffffff" }.Select(Online).ToArray();
        var kinds = SpotDlOutputClassifier.Classify(RealRunOutput, tracks, denoAvailable: false);
        Assert.Equal(ImportFailureKind.SourceVerification, kinds[tracks[0].Id]);
        Assert.Equal(ImportFailureKind.NotFound, kinds[tracks[1].Id]);
        Assert.Equal(ImportFailureKind.Conversion, kinds[tracks[2].Id]);
        Assert.Equal(ImportFailureKind.RuntimeMissing, kinds[tracks[3].Id]);
        Assert.Equal(ImportFailureKind.Network, kinds[tracks[4].Id]);
        Assert.Equal(ImportFailureKind.SourceVerification, kinds[tracks[5].Id]); // no own line: the run-wide reason applies
        var withDeno = SpotDlOutputClassifier.Classify(RealRunOutput, tracks, denoAvailable: true);
        Assert.Equal(ImportFailureKind.SourceVerification, withDeno[tracks[3].Id]); // installed runtime is never blamed; the run-wide reason applies
        Assert.Equal(ImportFailureKind.SourceVerification, withDeno[tracks[0].Id]);
        Assert.All(SpotDlOutputClassifier.Classify("saída sem padrão conhecido", tracks, false).Values, k => Assert.Equal(ImportFailureKind.Unknown, k));
    }

    // Second real run: spotDL's YTDLLogger turns the yt-dlp message into the *cause* of "YT-DLP download error - url".
    // At WARNING the reason never reaches the console (track shown as unknown); at DEBUG it appears in the debug line and traceback.
    private const string RealDebugOutput = """
        DEBUG:spotdl.providers.audio.base:ERROR: [youtube] AbCdEfGhIjK: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.
        ERROR:spotdl.download.progress_handler:Traceback (most recent call last):
          File "spotdl\providers\audio\base.py", line 401, in get_download_metadata
        spotdl.providers.audio.base.AudioProviderError: ERROR: [youtube] AbCdEfGhIjK: Sign in to confirm you’re not a bot.
        The above exception was the direct cause of the following exception:
        spotdl.providers.audio.base.AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=AbCdEfGhIjK
        ERROR:spotdl:https://open.spotify.com/track/aaaaaaaaaa - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=AbCdEfGhIjK
        """;

    [Fact] public void ReasonOnlyInDebugTracebackIsStillClassifiedAndSummaryAloneIsUnknown()
    {
        var track = Online("aaaaaaaaaa");
        Assert.Equal(ImportFailureKind.SourceVerification, SpotDlOutputClassifier.Classify(RealDebugOutput, [track], true)[track.Id]);
        var summaryOnly = "https://open.spotify.com/track/aaaaaaaaaa - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=AbCdEfGhIjK";
        Assert.Equal(ImportFailureKind.Unknown, SpotDlOutputClassifier.Classify(summaryOnly, [track], true)[track.Id]);
    }

    // Third real run (10 tracks): rich-formatted summary lines carry the video id, so they used to count as "detail"
    // and hid the run-wide reason; 4 of 10 tracks whose own reason lines were not captured ended up "unknown".
    [Fact] public void RichSummaryLinesDoNotHideTheRunWideReason()
    {
        var a = Online("0S6CXA5LpRDX6b4akgrOot"); var b = Online("4u7EnebtmKWzUH433cf5Qv");
        var output = string.Join('\n',
            "                    ExtractorError: [youtube] BSTsnWoslP4: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication." + new string(' ', 300),
            "                    AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=BSTsnWoslP4",
            "[21:54:32] ERROR    MainThread - https://open.spotify.com/track/0S6CXA5LpRDX6b4akgrOot - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=XzwF8urswkk" + new string(' ', 300),
            "[21:54:32] ERROR    MainThread - https://open.spotify.com/track/4u7EnebtmKWzUH433cf5Qv - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=BSTsnWoslP4");
        var kinds = SpotDlOutputClassifier.Classify(output, [a, b], true);
        Assert.Equal(ImportFailureKind.SourceVerification, kinds[a.Id]);
        Assert.Equal(ImportFailureKind.SourceVerification, kinds[b.Id]);
    }

    [Fact] public async Task CaptureTrimsConsolePaddingSoLongRunsKeepTheirReasons()
    {
        var work = Path.Combine(Path.GetTempPath(), "AHNSF-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "padded.ps1");
            await File.WriteAllTextAsync(script, "Write-Output ('primeira linha' + (' ' * 3000)); for ($i = 0; $i -lt 900; $i++) { Write-Output (('linha ' + $i) + (' ' * 3000)) }");
            var result = await ImportProcessRunner.RunAsync(PowerShell(), ["-NoProfile", "-File", script], work, TimeSpan.FromSeconds(60), null, default);
            Assert.StartsWith("primeira linha\n", result.Stdout); // ~2.7 MB padded, ~12 KB trimmed: nothing evicted
            Assert.DoesNotContain("   ", result.Stdout);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact] public async Task DownloadRunsSpotDlAtDebugSoTheSourceReasonIsVisible()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1)));
        h.Song("aaaaaaaaaa", "Primeira", "A");
        var preview = await h.Service.PreviewUrlAsync(Url);
        await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Depuração", null, default);
        var args = h.Calls.Split("----")[1].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("DEBUG", args[Array.IndexOf(args, "--log-level") + 1]);
    }

    [Fact] public void SanitizedLogDropsPathsQueryStringsAndSecretsButKeepsVideoId()
    {
        var text = "Saved to C:\\Temp\\AHNSF Music\\Import\\abc\\1.mp3\nERROR https://music.youtube.com/watch?v=AbCdEfGhIjK&si=segredo\n"
            + "https://open.spotify.com/playlist/x?si=privado token=abc123 cookie: sessao";
        var clean = SpotDlOutputClassifier.Sanitize(text, ["C:\\Temp\\AHNSF Music\\Import\\abc"]);
        Assert.DoesNotContain("AHNSF Music\\Import", clean); Assert.DoesNotContain("segredo", clean); Assert.DoesNotContain("privado", clean);
        Assert.DoesNotContain("abc123", clean); Assert.DoesNotContain("sessao", clean);
        Assert.Contains("watch?v=AbCdEfGhIjK", clean);
    }

    [Fact] public async Task YouTubeBotCheckIsReportedPerTrackInSummaryAndHistoryWithoutCookiesOrUrls()
    {
        await using var h = new Harness();
        await h.StartAsync(Playlist(Entry("aaaaaaaaaa", "Primeira", "A", 1), Entry("bbbbbbbbbb", "Segunda", "B", 2)), history: true);
        h.Song("aaaaaaaaaa", "Primeira", "A"); h.Mode("bot");
        var preview = await h.Service.PreviewUrlAsync(Url);
        var result = await h.Service.ImportAsync(preview, All(preview), h.F.Music, "Bloqueada", null, default);
        Assert.Equal(0, result.Imported); Assert.Equal(2, result.Unavailable); Assert.Null(result.PlaylistId);
        Assert.All(result.Items, i => Assert.Equal(ImportFailureKind.SourceVerification, i.Failure));
        Assert.All(result.Items, i => Assert.Equal(ImportFailures.Describe(ImportFailureKind.SourceVerification), i.Error));
        Assert.Equal("2 por verificação do YouTube", result.FailureBreakdown);
        Assert.DoesNotContain("--cookies", h.Calls.Split("----")[1]);
        await h.History!.FlushAsync();
        var failed = Assert.Single((await h.History.QueryAsync(new(Type: OperationalEventType.ImportFailed))).Events);
        Assert.Contains("verificação do YouTube", failed.Description);
        Assert.DoesNotContain("http", failed.Description); Assert.DoesNotContain("segredo", failed.Description);
    }

    [Fact] public async Task DenoInPackageMustMatchPinnedHashBeforeAnyProcessRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "AHNSF import deno " + Guid.NewGuid().ToString("N"));
        try
        {
            string Write(string relative, string content)
            { var path = Path.Combine(root, "Tools", relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
            string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var spot = Write(Path.Combine("spotdl", "spotdl.exe"), "fake spotdl"); var ffmpeg = Write(Path.Combine("ffmpeg", "ffmpeg.exe"), "fake ffmpeg");
            var deno = Write(Path.Combine("deno", "deno.exe"), "fake deno");
            Write("manifest.json", $$"""{"SpotDlVersion":"4.5.2","SpotDlSha256":"{{Hash(spot)}}","FFmpegVersion":"9.0.2","FFmpegSha256":"{{Hash(ffmpeg)}}","DenoVersion":"2.9.7","DenoSha256":"{{new string('0', 64)}}"}""");
            var status = await new SpotDlProcessAdapter(root).DiagnoseAsync();
            Assert.False(status.Ready); Assert.Contains("Deno", status.Message);
            Write("manifest.json", $$"""{"SpotDlVersion":"4.5.2","SpotDlSha256":"{{Hash(spot)}}","FFmpegVersion":"9.0.2","FFmpegSha256":"{{Hash(ffmpeg)}}","DenoVersion":"1.0.0","DenoSha256":"{{Hash(deno)}}"}""");
            Assert.Contains("Deno", (await new SpotDlProcessAdapter(root).DiagnoseAsync()).Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class PlayingAudio : IAudioEngine
    {
        public int Loads;
        public event EventHandler<AudioEndedEventArgs>? Ended { add { } remove { } }
        public PlayerState State { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public float Volume { get; set; } = 0.5f;
        public void Load(string path) => Loads++;
        public void Play() => State = PlayerState.Playing;
        public void Pause() => State = PlayerState.Paused;
        public void Stop() => State = PlayerState.Stopped;
        public void Dispose() => Stop();
    }
    private sealed class Sync<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
