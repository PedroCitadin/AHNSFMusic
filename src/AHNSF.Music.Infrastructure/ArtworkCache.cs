using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AHNSF.Music.Core;
using Serilog;
using SkiaSharp;

namespace AHNSF.Music.Infrastructure;

public sealed partial class ArtworkCache(string directory, ILogger logger) : IArtworkCache
{
    public const int ThumbnailSize = 256;
    public const int MaxEncodedBytes = 8 * 1024 * 1024;
    public const long MaxPixels = 16_000_000;
    public long MaxCacheBytes { get; init; } = 256 * 1024 * 1024;
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
    public string? GetPath(string? key)
    {
        if (key is null || !KeyPattern().IsMatch(key)) return null;
        var path = Path.Combine(directory, key + ".png");
        return File.Exists(path) ? path : null;
    }
    public string? Store(byte[] image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (image.Length == 0 || image.Length > MaxEncodedBytes) { logger.Warning("Capa excede limite de {Limit} bytes", MaxEncodedBytes); return null; }
        var key = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        if (GetPath(key) is not null) return key;
        string? temporary = null;
        try
        {
            using var data = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(data);
            if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                throw new InvalidDataException("Imagem inválida ou com dimensões excessivas.");
            using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Não foi possível decodificar a capa.");
            var ratio = Math.Min(1d, (double)ThumbnailSize / Math.Max(decoded.Width, decoded.Height));
            using var thumbnail = decoded.Resize(new SKImageInfo(Math.Max(1, (int)(decoded.Width * ratio)), Math.Max(1, (int)(decoded.Height * ratio))), SKFilterQuality.Medium)
                ?? throw new InvalidDataException("Falha ao redimensionar capa.");
            using var encoded = thumbnail.Encode(SKEncodedImageFormat.Png, 90);
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache não pode ser um link de diretório.");
            temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = File.Create(temporary)) encoded.SaveTo(stream);
            File.Move(temporary, Path.Combine(directory, key + ".png"), true);
            return key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.Warning(ex, "Falha ao gerar miniatura {Key}", key); return null; }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clean(IReadOnlySet<string> retainedKeys, CancellationToken token)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        var files = new DirectoryInfo(directory).GetFiles("*.png").Where(f => KeyPattern().IsMatch(Path.GetFileNameWithoutExtension(f.Name)) && (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderBy(f => f.LastWriteTimeUtc).ToArray();
        long total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var obsolete = !retainedKeys.Contains(Path.GetFileNameWithoutExtension(file.Name)) && DateTime.UtcNow - file.LastWriteTimeUtc >= Retention;
            if (!obsolete && total <= MaxCacheBytes) continue;
            try { var size = file.Length; file.Delete(); total -= size; }
            catch (IOException ex) { logger.Warning(ex, "Cache em uso: {File}", file.Name); }
            catch (UnauthorizedAccessException ex) { logger.Warning(ex, "Cache sem permissão: {File}", file.Name); }
        }
    }
}
