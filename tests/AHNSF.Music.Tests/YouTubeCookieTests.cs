using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

/// <summary>Only fictitious cookie files: no real session, no network, no real download.</summary>
public sealed class YouTubeCookieTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AHNSF cookies " + Guid.NewGuid().ToString("N"), "pasta com espaços");
    public YouTubeCookieTests() => Directory.CreateDirectory(root);
    public void Dispose() { var top = Path.GetDirectoryName(root)!; if (Directory.Exists(top)) Directory.Delete(top, true); }

    private const string Fake = "# Netscape HTTP Cookie File\n# Arquivo fictício para testes\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tFAKE_SESSION\tvalor-ficticio-000\n";
    private string Write(string name, string content) { var path = Path.Combine(root, name); File.WriteAllText(path, content); return path; }

    [Fact] public void NotConfiguredWhenPathIsEmpty() => Assert.Equal(CookieFileState.NotConfigured, YouTubeCookieSetting.Inspect(null).State);
    [Fact] public void MissingFileIsReported() => Assert.Equal(CookieFileState.FileMissing, YouTubeCookieSetting.Inspect(Path.Combine(root, "nao-existe.txt")).State);
    [Fact] public void EmptyFileIsInvalid() => Assert.Equal(CookieFileState.Invalid, YouTubeCookieSetting.Inspect(Write("vazio.txt", "")).State);
    [Fact] public void FileWithoutNetscapeHeaderIsInvalid() => Assert.Equal(CookieFileState.Invalid, YouTubeCookieSetting.Inspect(Write("json.txt", "{\"cookies\":[]}")).State);
    [Fact] public void FictitiousNetscapeFileInFolderWithSpacesIsConfigured()
    {
        var (state, message) = YouTubeCookieSetting.Inspect(Write("cookies.txt", Fake));
        Assert.Equal(CookieFileState.Configured, state);
        Assert.DoesNotContain("valor-ficticio", message);
    }

    private static string Playlist() =>
        """[{"name":"Primeira","artist":"A","duration":1,"song_id":"aaaaaaaaaa","url":"https://open.spotify.com/track/aaaaaaaaaa","list_name":"Sala","list_position":1},{"name":"Segunda","artist":"B","duration":1,"song_id":"bbbbbbbbbb","url":"https://open.spotify.com/track/bbbbbbbbbb","list_name":"Sala","list_position":2}]""";
    private static async Task<(string[] Args, ImportResult Result, LibraryFixture Fixture)> RunAsync(YouTubeCookieSetting cookies, bool secondAvailable = true)
    {
        var f = new LibraryFixture();
        var tool = Path.Combine(f.Base, "fake spot dl"); Directory.CreateDirectory(tool);
        var script = Path.Combine(tool, "spotdl.ps1");
        await File.WriteAllTextAsync(script, """
            Set-Content -LiteralPath (Join-Path $PSScriptRoot 'args.txt') -Value ($args -join "`n") -Encoding UTF8
            if ($args[0] -eq 'save') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'playlist.json') -Destination $args[[array]::IndexOf($args, '--save-file') + 1]; exit 0 }
            $request = [IO.File]::ReadAllText($args[1], [Text.Encoding]::UTF8) | ConvertFrom-Json
            $output = $args[[array]::IndexOf($args, '--output') + 1]
            foreach ($entry in $request) {
                $source = Join-Path $PSScriptRoot ($entry.song_id + '.mp3')
                if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $output.Replace('{list-position}', [string]$entry.list_position).Replace('{output-ext}', 'mp3') }
                else { Write-Output ($entry.url + ' - AudioProviderError: YT-DLP download error - https://music.youtube.com/watch?v=AbCdEfGhIjK'); [Console]::Error.WriteLine('ERROR: [youtube] AbCdEfGhIjK: Sign in to confirm you are not a bot.') }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(tool, "playlist.json"), Playlist());
        f.WriteTrack("../fake spot dl/aaaaaaaaaa.mp3", "Primeira", "A");
        if (secondAvailable) f.WriteTrack("../fake spot dl/bbbbbbbbbb.mp3", "Segunda", "B");
        await f.StartAsync();
        var adapter = new SpotDlProcessAdapter(f.Base, new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script], Path.Combine(tool, "ffmpeg.exe")), null, cookies: cookies);
        var service = new PlaylistImportService(adapter, f.Service, new SqlitePlaylistService(f.Database, f.Repository), f.Metadata);
        var preview = await service.PreviewUrlAsync("https://open.spotify.com/playlist/1234567890abcdef123456");
        var result = await service.ImportAsync(preview, preview.Tracks.Select(t => t.Id).ToHashSet(), f.Music, "Sala", null, default);
        return (File.ReadAllLines(Path.Combine(tool, "args.txt")).Select(l => l.Trim('﻿')).ToArray(), result, f);
    }

    [Fact] public async Task DownloadWithoutCookiesHasNoCookieArgument()
    {
        var (args, result, f) = await RunAsync(new YouTubeCookieSetting());
        await using var _ = f;
        Assert.Equal("download", args[0]); Assert.DoesNotContain("--cookie-file", args);
        Assert.Equal("2", args[Array.IndexOf(args, "--threads") + 1]);
        Assert.Equal(2, result.Imported);
    }

    [Fact] public async Task DownloadWithCookiesPassesPathWithSpacesAsOneArgumentAndLeavesFileUntouched()
    {
        var cookie = Write("cookies.txt", Fake); var before = File.ReadAllBytes(cookie);
        var (args, result, f) = await RunAsync(new YouTubeCookieSetting { FilePath = cookie });
        await using var _ = f;
        Assert.Equal(cookie, args[Array.IndexOf(args, "--cookie-file") + 1]);
        Assert.Single(args, a => a == "--cookie-file");
        Assert.Equal("1", args[Array.IndexOf(args, "--threads") + 1]); // one request at a time with an authenticated session
        Assert.Contains("--output", args); Assert.Contains("--ffmpeg", args); // existing arguments preserved
        Assert.Equal(2, result.Imported);
        Assert.Equal(before, File.ReadAllBytes(cookie));
    }

    [Fact] public async Task RemovingTheSettingStopsPassingTheArgument()
    {
        var setting = new YouTubeCookieSetting { FilePath = Write("cookies.txt", Fake) };
        setting.FilePath = null;
        var (args, _, f) = await RunAsync(setting);
        await using var __ = f;
        Assert.DoesNotContain("--cookie-file", args);
    }

    [Fact] public async Task ConfiguredButMissingFileStopsBeforeRunningSpotDl()
    {
        var setting = new YouTubeCookieSetting { FilePath = Path.Combine(root, "removido.txt") };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(setting));
        Assert.Contains("não encontrado", ex.Message); Assert.Contains("Configurações", ex.Message);
    }

    [Fact] public async Task RejectedSessionFailsOnlyThatTrackWithAuthenticationMessage()
    {
        var (args, result, f) = await RunAsync(new YouTubeCookieSetting { FilePath = Write("cookies.txt", Fake) }, secondAvailable: false);
        await using var _ = f;
        Assert.Contains("--cookie-file", args);
        Assert.Equal(1, result.Imported); Assert.Equal(1, result.Unavailable); Assert.NotNull(result.PlaylistId);
        var failed = Assert.Single(result.Items, i => i.LibraryTrackId is null);
        Assert.Equal(ImportFailureKind.SourceVerification, failed.Failure);
        Assert.Equal("O YouTube solicitou uma verificação de autenticação. Configure ou atualize seu arquivo de cookies nas configurações de downloads.", failed.Error);
    }
}
