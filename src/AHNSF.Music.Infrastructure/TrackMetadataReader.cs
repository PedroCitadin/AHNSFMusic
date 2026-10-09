using AHNSF.Music.Core;
using Serilog;

namespace AHNSF.Music.Infrastructure;

public sealed class TrackMetadataReader(ITrackDurationReader durationReader, IArtworkCache artwork, ILogger logger) : ITrackMetadataReader
{
    public Track Read(string path, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var track = new Track(path, Path.GetFileNameWithoutExtension(path), "Artista desconhecido", TimeSpan.Zero)
        {
            Id = Guid.NewGuid(), RootPath = root, RelativePath = Path.GetRelativePath(root, path),
            FileSize = info.Length, LastModifiedUtc = info.LastWriteTimeUtc, IndexedAtUtc = DateTime.UtcNow
        };
        try
        {
            // Bound ID3 payloads before TagLib materializes embedded images/frames.
            using (var stream = File.OpenRead(path))
            {
                Span<byte> header = stackalloc byte[10];
                if (stream.Read(header) == 10 && header[..3].SequenceEqual("ID3"u8))
                {
                    var tagBytes = ((header[6] & 127) << 21) | ((header[7] & 127) << 14) | ((header[8] & 127) << 7) | (header[9] & 127);
                    if (tagBytes > 16 * 1024 * 1024) throw new InvalidDataException("Tags ID3 excedem limite de 16 MiB.");
                }
            }
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            track = track with
            {
                Title = string.IsNullOrWhiteSpace(tag.Title) ? track.Title : tag.Title,
                Artist = tag.Performers.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray() is { Length: > 0 } performers ? string.Join(", ", performers) : track.Artist,
                Album = string.IsNullOrWhiteSpace(tag.Album) ? track.Album : tag.Album,
                AlbumArtist = string.Join(", ", tag.AlbumArtists), Genre = string.Join(", ", tag.Genres), Year = tag.Year
            };
            var picture = tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures.FirstOrDefault();
            if (picture is not null)
            {
                if (picture.Data.Count <= ArtworkCache.MaxEncodedBytes) track = track with { ArtworkCacheKey = artwork.Store(picture.Data.Data, token) };
                else logger.Warning("Capa incorporada excessiva: {Path}", path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.Warning(ex, "Metadados inválidos: {Path}", path); }
        token.ThrowIfCancellationRequested();
        // Failed tags/artwork never suppress a playable MP3. Duration has its own error boundary.
        try { track = track with { Duration = durationReader.Read(path) }; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.Warning(ex, "MP3 inválido: {Path}", path); track = track with { Availability = AvailabilityStatus.Invalid }; }
        return track;
    }
}
