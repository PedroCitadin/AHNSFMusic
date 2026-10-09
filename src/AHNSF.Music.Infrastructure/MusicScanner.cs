using AHNSF.Music.Core;
using Serilog;

namespace AHNSF.Music.Infrastructure;

public sealed class MusicScanner(ILogger logger, ITrackDurationReader durationReader) : IMusicScanner
{
    public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("A pasta de músicas não está disponível.");
        var tracks = new List<Track>();
        var warnings = new List<string>();
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var child in Directory.GetDirectories(folder))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                foreach (var path in Directory.GetFiles(folder).Where(p => System.IO.Path.GetExtension(p).Equals(".mp3", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var file = TagLib.File.Create(path);
                        tracks.Add(new Track(path, string.IsNullOrWhiteSpace(file.Tag.Title) ? System.IO.Path.GetFileNameWithoutExtension(path) : file.Tag.Title,
                            string.Join(", ", file.Tag.Performers) is { Length: > 0 } artist ? artist : "Artista desconhecido", durationReader.Read(path)));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.Warning(ex, "Falha nos metadados: {Path}", path);
                        warnings.Add($"Não foi possível ler os metadados de {System.IO.Path.GetFileName(path)}.");
                        tracks.Add(new Track(path, System.IO.Path.GetFileNameWithoutExtension(path), "Metadados indisponíveis", TimeSpan.Zero));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Pasta inacessível: {Path}", folder);
                if (folder == directory) throw;
                warnings.Add($"Não foi possível acessar a subpasta {folder}.");
            }
        }
        return new ScanResult(tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(), warnings);
    }, cancellationToken);
}
