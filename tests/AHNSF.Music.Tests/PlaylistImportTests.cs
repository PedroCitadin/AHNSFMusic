using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class PlaylistImportTests
{
    private sealed class OfflineMetadata : IImportMetadataProvider
    {
        public Task<ImportToolStatus> DiagnoseAsync(CancellationToken token = default) => Task.FromResult(new ImportToolStatus(false, "Offline"));
        public Task<ImportPreview> PreviewAsync(ImportAddress address, IProgress<ImportProgress>? progress, CancellationToken token) =>
            Task.FromResult(new ImportPreview("Online", address.Platform, [new(Guid.NewGuid(), 0, "Canção", "Artista", TimeSpan.FromSeconds(2))], "Metadados"));
    }

    private static PlaylistImportService Service(LibraryFixture f, SqlitePlaylistService? playlists = null) =>
        new(new OfflineMetadata(), f.Service, playlists ?? new(f.Database, f.Repository), f.Metadata);

    [Theory]
    [InlineData("https://open.spotify.com/playlist/1234567890abcdef123456?si=secret", ImportPlatform.Spotify, "playlist")]
    [InlineData("https://open.spotify.com/track/1234567890abcdef123456", ImportPlatform.Spotify, "track")]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefgh123&feature=share", ImportPlatform.YouTube, "playlist")]
    public void AddressAcceptsOnlyCanonicalProviderIds(string input, ImportPlatform platform, string kind)
    {
        var parsed = ImportAddress.Parse(input);
        Assert.Equal(platform, parsed.Platform); Assert.Equal(kind, parsed.Kind);
        Assert.DoesNotContain("secret", parsed.Uri.AbsoluteUri);
        Assert.DoesNotContain("feature", parsed.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://open.spotify.com/playlist/1234567890abcdef123456")]
    [InlineData("https://user:secret@open.spotify.com/playlist/1234567890abcdef123456")]
    [InlineData("https://spotify.example.com/playlist/1234567890abcdef123456")]
    [InlineData("https://open.spotify.com/playlist/1234567890abcdef123456/extra")]
    [InlineData("https://www.youtube.com/playlist?list=bad;rm")]
    public void AddressRejectsUntrustedOrMalformedUrls(string input) => Assert.Throws<ArgumentException>(() => ImportAddress.Parse(input));

    [Fact] public void SavedMetadataKeepsProviderOrderAndNeverGrantsAudioAvailability()
    {
        const string json = "[{\"name\":\"Segundo\",\"artist\":\"B\",\"duration\":2,\"list_position\":1,\"list_name\":\"Sala\"},{\"name\":\"Primeiro\",\"artist\":\"A\",\"duration\":3,\"list_position\":0}]";
        var preview = SpotDlProcessAdapter.ParseSavedMetadata(json, ImportPlatform.Spotify);
        Assert.Equal("Sala", preview.Name);
        Assert.Equal(new[] { "Primeiro", "Segundo" }, preview.Tracks.Select(t => t.Title));
        Assert.All(preview.Tracks, track => Assert.False(track.Available));
    }

    [Fact] public async Task MissingToolsAreDiagnosedWithoutNetworkOrGlobalPython()
    {
        var root = Path.Combine(Path.GetTempPath(), "AHNSF-import-" + Guid.NewGuid().ToString("N"));
        var adapter = new SpotDlProcessAdapter(root);
        var status = await adapter.DiagnoseAsync();
        Assert.False(status.Ready); Assert.Contains("ausentes", status.Message);
    }

    [Fact] public async Task IncompatibleManifestAndHashesAreRejectedBeforeExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "AHNSF-import-" + Guid.NewGuid().ToString("N"));
        var spot = Path.Combine(root, "Tools", "spotdl"); var ffmpeg = Path.Combine(root, "Tools", "ffmpeg");
        Directory.CreateDirectory(spot); Directory.CreateDirectory(ffmpeg);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(spot, "spotdl.exe"), "fake");
            await File.WriteAllTextAsync(Path.Combine(ffmpeg, "ffmpeg.exe"), "fake");
            var manifest = Path.Combine(root, "Tools", "manifest.json");
            await File.WriteAllTextAsync(manifest, "{\"SpotDlVersion\":\"1\",\"SpotDlSha256\":\"\",\"FFmpegVersion\":\"9.0.2\",\"FFmpegSha256\":\"\"}");
            var adapter = new SpotDlProcessAdapter(root);
            Assert.Contains("Versão", (await adapter.DiagnoseAsync()).Message);
            await File.WriteAllTextAsync(manifest, "{\"SpotDlVersion\":\"4.5.2\",\"SpotDlSha256\":\"\",\"FFmpegVersion\":\"9.0.2\",\"FFmpegSha256\":\"\"}");
            Assert.Contains("SHA-256", (await adapter.DiagnoseAsync()).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public async Task LocalImportCopiesIndexesAndPersistsOrderedPlaylistWithoutChangingSource()
    {
        await using var f = new LibraryFixture();
        var a = f.WriteTrack("../source/a.mp3", "Primeira", "A");
        var b = f.WriteTrack("../source/b.mp3", "Segunda", "B");
        var bytesA = File.ReadAllBytes(a); var bytesB = File.ReadAllBytes(b);
        await f.StartAsync();
        var playlists = new SqlitePlaylistService(f.Database, f.Repository);
        var service = Service(f, playlists);
        var preview = await service.PreviewLocalAsync([b, a]);
        Assert.Equal(2, preview.Tracks.Count); Assert.All(preview.Tracks, t => Assert.True(t.Available));
        var result = await service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Sala", null, default);
        Assert.Equal(2, result.Imported); Assert.Equal(0, result.Unavailable);
        var saved = (await playlists.GetAsync(result.PlaylistId!.Value))!;
        Assert.Equal(new[] { "Segunda", "Primeira" }, saved.Items.Select(i => i.Track!.Title));
        Assert.Equal(bytesA, File.ReadAllBytes(a)); Assert.Equal(bytesB, File.ReadAllBytes(b));
        Assert.Equal(2, (await f.Service.QueryAsync(new(), default)).Count);
        Assert.Empty(Directory.GetFiles(f.Music, "*.partial"));
    }

    [Fact] public async Task ExistingIdenticalFileIsReusedAndNotCopied()
    {
        await using var f = new LibraryFixture();
        var original = f.WriteTrack("already.mp3");
        var source = Path.Combine(f.Base, "copy.mp3"); File.Copy(original, source);
        var existing = Assert.Single((await f.StartAsync()).Tracks);
        var playlists = new SqlitePlaylistService(f.Database, f.Repository);
        var service = Service(f, playlists);
        var preview = await service.PreviewLocalAsync([source]);
        Assert.Equal(existing.Id, Assert.Single(preview.Tracks).ExistingTrackId);
        var result = await service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Reuso", null, default);
        Assert.Equal(0, result.Imported); Assert.Equal(1, result.Duplicates);
        Assert.Single(Directory.GetFiles(f.Music, "*.mp3"));
        Assert.Equal(existing.Id, Assert.Single((await playlists.GetAsync(result.PlaylistId!.Value))!.Items).TrackId);
    }

    [Fact] public async Task RepeatedFilesInOneSelectionCreateOneCopyAndTwoPlaylistOccurrences()
    {
        await using var f = new LibraryFixture();
        var a = f.WriteTrack("../source/a.mp3");
        var b = Path.Combine(f.Base, "b.mp3"); File.Copy(a, b);
        await f.StartAsync(); var playlists = new SqlitePlaylistService(f.Database, f.Repository);
        var service = Service(f, playlists); var preview = await service.PreviewLocalAsync([a, b]);
        var result = await service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Dupla", null, default);
        Assert.Equal(1, result.Imported); Assert.Equal(1, result.Duplicates);
        Assert.Single(Directory.GetFiles(f.Music, "*.mp3"));
        var items = (await playlists.GetAsync(result.PlaylistId!.Value))!.Items;
        Assert.Equal(2, items.Count); Assert.Equal(items[0].TrackId, items[1].TrackId);
    }

    [Fact] public async Task InvalidSourceIsReportedAndValidSelectedFileStillImports()
    {
        await using var f = new LibraryFixture();
        var good = f.WriteTrack("../source/good.mp3");
        var bad = Path.Combine(f.Base, "bad.mp3"); File.WriteAllText(bad, "invalid");
        await f.StartAsync(); var playlists = new SqlitePlaylistService(f.Database, f.Repository);
        var service = Service(f, playlists);
        var preview = await service.PreviewLocalAsync([bad, good]);
        Assert.False(preview.Tracks[0].Available);
        var result = await service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Parcial", null, default);
        Assert.Equal(1, result.Imported); Assert.Equal(1, result.Unavailable);
        Assert.Single((await playlists.GetAsync(result.PlaylistId!.Value))!.Items);
        Assert.True(File.Exists(bad));
    }

    [Fact] public async Task ImportWritesSummaryEventsWithoutUrlsOrFilePaths()
    {
        await using var f = new LibraryFixture();
        var source = f.WriteTrack("../source/history.mp3"); await f.StartAsync();
        var repository = new SqliteOperationalHistoryRepository(f.Database, f.Repository);
        await using var history = new OperationalHistoryService(repository);
        await history.StartAsync(false);
        var service = new PlaylistImportService(new OfflineMetadata(), f.Service, new SqlitePlaylistService(f.Database, f.Repository), f.Metadata, history);
        var preview = await service.PreviewLocalAsync([source]);
        var result = await service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Histórico", null, default);
        await history.FlushAsync();
        var events = (await history.QueryAsync(new())).Events;
        Assert.Contains(events, e => e.Type == OperationalEventType.ImportStarted);
        var complete = Assert.Single(events, e => e.Type == OperationalEventType.ImportCompleted);
        Assert.Equal(result.PlaylistId, complete.PlaylistId);
        Assert.Contains("1 arquivo(s)", complete.Description);
        Assert.DoesNotContain(source, complete.Description);
        Assert.DoesNotContain("https:", complete.Description);
    }

    [Fact] public async Task OnlineMetadataMatchesLocalCatalogButDoesNotFetchAudio()
    {
        await using var f = new LibraryFixture();
        f.WriteTrack("present.mp3", "Canção", "Artista");
        var catalog = (await f.StartAsync()).Tracks;
        var preview = await Service(f).PreviewUrlAsync("https://open.spotify.com/playlist/1234567890abcdef123456");
        Assert.Equal(catalog[0].Id, Assert.Single(preview.Tracks).ExistingTrackId);
    }

    [Fact] public async Task CancellationBeforeJobDoesNotCreatePlaylistOrFiles()
    {
        await using var f = new LibraryFixture();
        var source = f.WriteTrack("../source/song.mp3"); await f.StartAsync();
        var playlists = new SqlitePlaylistService(f.Database, f.Repository); var service = Service(f, playlists);
        var preview = await service.PreviewLocalAsync([source]);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Cancelada", null, cts.Token));
        Assert.Empty(await playlists.ListAsync()); Assert.Empty(Directory.GetFiles(f.Music, "*.mp3"));
    }

    [Fact] public async Task ProcessRunnerPassesSpaceContainingPathAsOneArgumentAndReportsProgress()
    {
        var work = Path.Combine(Path.GetTempPath(), "AHNSF import with spaces " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "fake helper.ps1");
            await File.WriteAllTextAsync(script, "Write-Output 'helper ready'");
            var progress = new List<ImportProgress>();
            var result = await ImportProcessRunner.RunAsync(PowerShellExe(), ["-NoProfile", "-File", script], work,
                TimeSpan.FromSeconds(15), new SynchronousProgress<ImportProgress>(progress.Add), default);
            Assert.Equal(0, result.ExitCode); Assert.Contains("helper ready", result.Stdout); Assert.NotEmpty(progress);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact] public async Task ProcessRunnerReturnsUnexpectedExitCode()
    {
        var work = Path.Combine(Path.GetTempPath(), "AHNSF-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "failure.ps1"); await File.WriteAllTextAsync(script, "exit 7");
            var result = await ImportProcessRunner.RunAsync(PowerShellExe(), ["-NoProfile", "-File", script], work, TimeSpan.FromSeconds(15), null, default);
            Assert.Equal(7, result.ExitCode);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact] public async Task ProcessRunnerTimesOutAndTerminatesChild()
    {
        var work = Path.Combine(Path.GetTempPath(), "AHNSF-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "slow.ps1"); await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 8");
            await Assert.ThrowsAsync<TimeoutException>(() => ImportProcessRunner.RunAsync(PowerShellExe(), ["-NoProfile", "-File", script], work,
                TimeSpan.FromSeconds(1), null, default));
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact] public async Task ProcessRunnerCancellationTerminatesChild()
    {
        var work = Path.Combine(Path.GetTempPath(), "AHNSF-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "slow.ps1"); await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 8");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportProcessRunner.RunAsync(PowerShellExe(), ["-NoProfile", "-File", script], work,
                TimeSpan.FromSeconds(15), null, cts.Token));
        }
        finally { Directory.Delete(work, true); }
    }

    private static string PowerShellExe() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
