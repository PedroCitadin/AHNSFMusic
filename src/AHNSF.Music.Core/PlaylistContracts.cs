namespace AHNSF.Music.Core;

public sealed record Playlist(Guid Id, string Name, string Description, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool IsDefault)
{
    public string Label => IsDefault ? $"★ {Name}" : Name;
}
public sealed record PlaylistItem(Guid Id, Guid PlaylistId, Guid TrackId, int Position, DateTime AddedAtUtc, Track Track);
public sealed record PlaylistDetail(Playlist Playlist, IReadOnlyList<PlaylistItem> Items);
public interface IPlaylistService
{
    Task<IReadOnlyList<Playlist>> ListAsync(CancellationToken token = default);
    Task<PlaylistDetail?> GetAsync(Guid id, CancellationToken token = default);
    Task<Playlist?> GetDefaultAsync(CancellationToken token = default);
    Task<Playlist> CreateAsync(string name, string description = "", CancellationToken token = default);
    Task UpdateAsync(Guid id, string name, string description, bool isDefault, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
    Task AddAsync(Guid id, IReadOnlyList<Guid> tracks, CancellationToken token = default);
    Task RemoveAsync(Guid id, IReadOnlyList<Guid> items, CancellationToken token = default);
    Task ReorderAsync(Guid id, IReadOnlyList<Guid> items, CancellationToken token = default);
    Task<IReadOnlyDictionary<Guid, Track>> ResolveAsync(IReadOnlyList<Guid> ids, CancellationToken token = default);
}
public enum RepeatMode { Off, Queue, Track }
public sealed record QueueItem(Guid Id, Track Track, Guid? PlaylistItemId = null);
public sealed record PlaybackOrigin(Guid? PlaylistId, string Name);
public interface IPlaybackService
{
    void SetOperationalContext(OperationalActionOrigin origin, Guid? ruleId = null, string? deviceId = null) { }
    event EventHandler? Changed;
    PlaybackQueue Queue { get; }
    Track? CurrentTrack { get; }
    PlayerState State { get; }
    string Message { get; }
    /// <summary>Last playback error, used by scheduled recovery to classify failures.</summary>
    Exception? LastError => null;
    float Volume { get; set; }
    void SetScheduledRepeat(bool enabled);
    Task LoadPlaylistAsync(Guid id, bool start = false, Guid? itemId = null, CancellationToken token = default);
    void Play();
    void PlayOccurrence(Guid id);
    void PlayLibrary(Track track, IReadOnlyList<Track> library);
    void Pause();
    void Stop();
    void Next();
    void Previous();
    void Add(Track track, bool next = false);
    void Remove(IReadOnlyList<Guid> ids);
    void Clear();
    void SetModes(bool shuffle, RepeatMode repeat);
    Task RefreshMetadataAsync(CancellationToken token = default);
}
