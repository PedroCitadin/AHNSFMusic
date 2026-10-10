namespace AHNSF.Music.Core;

public enum ImportPlatform { Spotify, YouTube }
public sealed record ImportAddress(ImportPlatform Platform, string Kind, string Id, Uri Uri)
{
    // Restrict to known hosts and IDs. Query strings, fragments and credentials never reach subprocesses/logs.
    public static ImportAddress Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new ArgumentException("Informe um link HTTPS público de playlist ou faixa do Spotify/YouTube.");
        var host = uri.IdnHost.ToLowerInvariant();
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host == "open.spotify.com" && parts.Length == 2 && parts[0] is "playlist" or "track" &&
            parts[1].Length is >= 10 and <= 64 && parts[1].All(char.IsLetterOrDigit))
            return new(ImportPlatform.Spotify, parts[0], parts[1], new Uri($"https://open.spotify.com/{parts[0]}/{parts[1]}"));
        if (host is "www.youtube.com" or "youtube.com" or "music.youtube.com" && parts.Length == 1 && parts[0] == "playlist")
        {
            var list = ParseQuery(uri.Query, "list");
            if (list is { Length: >= 8 and <= 128 } && list.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                return new(ImportPlatform.YouTube, "playlist", list, new Uri($"https://www.youtube.com/playlist?list={list}"));
        }
        throw new ArgumentException("Link não suportado. Use uma playlist ou faixa pública reconhecida.");
    }
    private static string? ParseQuery(string query, string key) => query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == key)
        .Select(pair => Uri.UnescapeDataString(pair[1])).FirstOrDefault();
}

public sealed record ImportTrack(Guid Id, int Position, string Title, string Artist, TimeSpan Duration,
    string? LocalPath = null, Guid? ExistingTrackId = null, string? SourceId = null, string? Issue = null, string? SourceData = null)
{
    public bool Available => ExistingTrackId is not null || LocalPath is not null;
    /// <summary>Online entry the helper tool can try to obtain; it is not audio until a verified file exists.</summary>
    public bool Downloadable => !Available && SourceData is not null;
}
public sealed record ImportPreview(string Name, ImportPlatform? Platform, IReadOnlyList<ImportTrack> Tracks, string Origin);
public sealed record ImportProgress(int Completed, int Total, string Message);
public sealed record ImportItemResult(Guid ImportTrackId, string Title, Guid? LibraryTrackId, string Status, string? Error = null)
{
    public string Display => $"{Title}: {Status}{(Error is null ? "" : " — " + Error)}";
}
public sealed record ImportResult(Guid? PlaylistId, int Imported, int Duplicates, int Unavailable, bool Cancelled, IReadOnlyList<ImportItemResult> Items);
public sealed record ImportToolStatus(bool Ready, string Message, string? SpotDlVersion = null, string? FFmpegVersion = null);

public interface IImportMetadataProvider
{
    Task<ImportToolStatus> DiagnoseAsync(CancellationToken token = default);
    Task<ImportPreview> PreviewAsync(ImportAddress address, IProgress<ImportProgress>? progress, CancellationToken token);
    /// <summary>Obtains audio for Downloadable entries into <paramref name="staging"/>. Returns only files that exist; missing keys are failures.</summary>
    Task<IReadOnlyDictionary<Guid, string>> DownloadAsync(IReadOnlyList<ImportTrack> tracks, string staging,
        IProgress<ImportProgress>? progress, CancellationToken token) =>
        throw new NotSupportedException("Obtenção de áudio indisponível neste provedor.");
}
public interface IPlaylistImportService
{
    Task<ImportToolStatus> DiagnoseAsync(CancellationToken token = default);
    Task<ImportPreview> PreviewUrlAsync(string url, CancellationToken token = default);
    Task<ImportPreview> PreviewLocalAsync(IReadOnlyList<string> paths, CancellationToken token = default);
    Task<ImportResult> ImportAsync(ImportPreview preview, IReadOnlySet<Guid> selected, string root, string playlistName,
        IProgress<ImportProgress>? progress, CancellationToken token);
}
