using System.Buffers.Binary;
using System.Text;
using AHNSF.Music.Audio;
using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Microsoft.Data.Sqlite;
using Serilog;
using SkiaSharp;

namespace AHNSF.Music.Tests;

internal sealed class LibraryFixture : IAsyncDisposable
{
    public string Base { get; } = Path.Combine(Path.GetTempPath(), "AHNSF-library-" + Guid.NewGuid().ToString("N"));
    public string Music => Path.Combine(Base, "music");
    public string Database => Path.Combine(Base, "settings.db");
    public string CacheDirectory => Path.Combine(Base, "artwork");
    public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();
    public SqliteLibraryRepository Repository { get; }
    public ArtworkCache Artwork { get; }
    public CountingMetadata Metadata { get; }
    public LibraryService Service { get; }
    public LibraryFixture(bool automatic = false, bool watcher = true, TimeSpan? stability = null, TimeSpan? periodic = null)
    {
        Directory.CreateDirectory(Music);
        Repository = new(Database); Artwork = new(CacheDirectory, Logger);
        Metadata = new(new TrackMetadataReader(new Mp3DurationReader(), Artwork, Logger));
        Service = new(Repository, Metadata, Artwork, Logger, new LibraryOptions
        {
            EnableAutomaticSync = automatic, UseFileSystemWatcher = watcher,
            Debounce = TimeSpan.FromMilliseconds(60), StabilityDelay = stability ?? TimeSpan.FromMilliseconds(30),
            RetryDelay = TimeSpan.FromMilliseconds(80), ReconcileInterval = periodic ?? TimeSpan.FromMinutes(5)
        });
    }
    public string WriteTrack(string file, string title = "Canção", string artist = "Artista", string album = "Álbum", byte[]? picture = null)
    {
        var path = Path.Combine(Music, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.mp3"));
        var offset = original.AsSpan(0, 3).SequenceEqual("ID3"u8) ? 10 + ((original[6] << 21) | (original[7] << 14) | (original[8] << 7) | original[9]) : 0;
        using var frames = new MemoryStream();
        TextFrame("TIT2", title); TextFrame("TPE1", artist); TextFrame("TALB", album);
        TextFrame("TPE2", "Artista do álbum"); TextFrame("TCON", "Ambiente"); TextFrame("TYER", "2026");
        if (picture is not null)
        {
            using var image = new MemoryStream(); image.WriteByte(0); image.Write(Encoding.ASCII.GetBytes("image/png\0")); image.WriteByte(3); image.WriteByte(0); image.Write(picture);
            Frame("APIC", image.ToArray());
        }
        using var output = File.Create(path);
        output.Write("ID3"u8); output.Write([3, 0, 0]);
        var length = (int)frames.Length; output.Write([(byte)((length >> 21) & 127), (byte)((length >> 14) & 127), (byte)((length >> 7) & 127), (byte)(length & 127)]);
        frames.Position = 0; frames.CopyTo(output); output.Write(original.AsSpan(offset));
        return path;
        void TextFrame(string id, string text)
        {
            using var data = new MemoryStream(); data.WriteByte(1); data.Write([0xff, 0xfe]); data.Write(Encoding.Unicode.GetBytes(text)); data.Write([0, 0]); Frame(id, data.ToArray());
        }
        void Frame(string id, byte[] data)
        {
            frames.Write(Encoding.ASCII.GetBytes(id)); Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); frames.Write(size); frames.Write([0, 0]); frames.Write(data);
        }
    }
    public static byte[] Picture(int width = 700, int height = 500)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(SKColors.Teal);
        using var image = bitmap.Encode(SKEncodedImageFormat.Png, 90); return image.ToArray();
    }
    public async Task<LibraryResult> StartAsync()
    { await Service.SetDirectoryAsync(Music, CancellationToken.None); await Service.ReconcileAsync(false, CancellationToken.None); return await Service.QueryAsync(new(), CancellationToken.None); }
    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync(); SqliteConnection.ClearAllPools(); (Logger as IDisposable)?.Dispose(); Directory.Delete(Base, true);
    }
    internal sealed class CountingMetadata(ITrackMetadataReader reader) : ITrackMetadataReader
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public Track Read(string path, string root, CancellationToken token) { Interlocked.Increment(ref count); return reader.Read(path, root, token); }
    }
}
