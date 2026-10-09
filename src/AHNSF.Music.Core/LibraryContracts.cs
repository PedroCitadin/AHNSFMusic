using System.Globalization;
using System.Text;

namespace AHNSF.Music.Core;

public enum AvailabilityStatus { Available, Invalid, Missing }
public enum LibrarySort { Title, Artist, Album, Duration, Added }
public sealed record LibraryQuery(string Search = "", string? Artist = null, string? Album = null,
    LibrarySort Sort = LibrarySort.Title, bool Descending = false);
public sealed record LibraryResult(IReadOnlyList<Track> Tracks, int Count, IReadOnlyList<string> Artists, IReadOnlyList<string> Albums);
public sealed record LibrarySyncResult(bool DirectoryAvailable, int Processed, int Changed, int Pending, IReadOnlyList<string> Warnings);
public sealed record LibraryNotification(string Message, bool IsBusy = false, bool DirectoryAvailable = true, string? RootPath = null);
public interface ILibraryRepository
{
    Task<IReadOnlyList<Track>> LoadAsync(string root, CancellationToken token, bool includeMissing = true);
    Task<LibraryResult> QueryAsync(string root, LibraryQuery query, CancellationToken token);
    Task ApplyAsync(IReadOnlyList<Track> changed, CancellationToken token);
    Task<IReadOnlySet<string>> ArtworkKeysAsync(CancellationToken token);
}
public interface ITrackMetadataReader { Track Read(string path, string root, CancellationToken token); }
public interface IArtworkCache
{
    string? Store(byte[] image, CancellationToken token);
    string? GetPath(string? key);
    void Clean(IReadOnlySet<string> retainedKeys, CancellationToken token);
}
public interface ILibraryService : IAsyncDisposable
{
    event EventHandler<LibraryNotification>? Changed;
    Task SetDirectoryAsync(string root, CancellationToken token);
    Task<LibrarySyncResult> ReconcileAsync(bool force, CancellationToken token);
    Task<LibraryResult> QueryAsync(LibraryQuery query, CancellationToken token);
}
public static class LibraryText
{
    public static string Fold(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToUpperInvariant(ch));
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
    public static string NormalizePath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (full.Length > System.IO.Path.GetPathRoot(full)!.Length) full = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return full.ToUpperInvariant();
    }
}
