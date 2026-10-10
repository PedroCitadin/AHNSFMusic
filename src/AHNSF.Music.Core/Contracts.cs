namespace AHNSF.Music.Core;

public enum PlayerState { Stopped, Playing, Paused }
public sealed record Track(string Path, string Title, string Artist, TimeSpan Duration)
{
    public string DurationText => Duration.ToString(@"mm\:ss");
    public Guid Id { get; init; }
    public string RootPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Album { get; init; } = "Álbum desconhecido";
    public string AlbumArtist { get; init; } = "";
    public string Genre { get; init; } = "";
    public uint Year { get; init; }
    public long FileSize { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public DateTime IndexedAtUtc { get; init; }
    public string? ArtworkCacheKey { get; init; }
    public AvailabilityStatus Availability { get; init; } = AvailabilityStatus.Available;
}
public sealed record AppSettings(string? MusicDirectory = null, float Volume = 0.35f,
    double Width = 1100, double Height = 760, int? X = null, int? Y = null,
    bool Shuffle = false, RepeatMode Repeat = RepeatMode.Off,
    bool AutoStartEnabled = false, bool StartMinimized = false,
    CloseBehavior CloseBehavior = CloseBehavior.MinimizeToTray, bool CloseNoticeShown = false,
    AudioDeviceSelection? AudioDevice = null, int HistoryRetentionDays = 90);
public enum CloseBehavior { MinimizeToTray, Exit }
public sealed record ScanResult(IReadOnlyList<Track> Tracks, IReadOnlyList<string> Warnings);
public interface IMusicScanner { Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken); }
public interface ITrackDurationReader { TimeSpan Read(string path); }
public interface ISettingsStore { Task<AppSettings> LoadAsync(); Task SaveAsync(AppSettings settings); }
public sealed class AudioEndedEventArgs(Exception? error = null) : EventArgs
{ public Exception? Error { get; } = error; }
public interface IAudioEngine : IDisposable
{
    event EventHandler<AudioEndedEventArgs>? Ended;
    PlayerState State { get; }
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    void Load(string path);
    void Play();
    void Pause();
    void Stop();
}
