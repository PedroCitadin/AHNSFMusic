namespace AHNSF.Music.Core;

// Saved playlist order and active occurrence order are separate. All navigation uses occurrence IDs.
public sealed class PlaybackQueue(Random? random = null)
{
    private readonly Random random = random ?? Random.Shared;
    private readonly List<QueueItem> items = [];
    private readonly List<Guid> route = [];
    private readonly List<Guid> history = [];
    private int historyIndex = -1;
    public IReadOnlyList<QueueItem> Items => items.ToArray();
    public QueueItem? CurrentItem => items.FirstOrDefault(i => i.Id == CurrentId);
    public Guid? CurrentId { get; private set; }
    public Track? Current => CurrentItem?.Track;
    public int Position => items.FindIndex(i => i.Id == CurrentId);
    public bool UsesLibraryOrder { get; private set; } = true;
    public PlaybackOrigin Origin { get; private set; } = new(null, "Biblioteca");
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; set; }
    public static bool IsPlayable(Track track) => track.Availability == AvailabilityStatus.Available;

    public void Load(IEnumerable<QueueItem> entries, PlaybackOrigin origin)
    {
        var incoming = entries.ToArray();
        if (incoming.Select(i => i.Id).Distinct().Count() != incoming.Length) throw new ArgumentException("Ocorrências duplicadas na fila.");
        items.Clear(); items.AddRange(incoming); Origin = origin; CurrentId = null; UsesLibraryOrder = false;
        history.Clear(); historyIndex = -1; BuildRoute();
    }
    public void Replace(IReadOnlyList<Track> tracks)
    {
        var old = items.ToArray(); var currentPath = Current?.Path;
        var identities = old.GroupBy(i => i.Track.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        items.Clear();
        items.AddRange(tracks.Select(t => new QueueItem(identities.GetValueOrDefault(t.Path, Guid.NewGuid()), t)));
        CurrentId = items.FirstOrDefault(i => string.Equals(i.Track.Path, currentPath, StringComparison.OrdinalIgnoreCase))?.Id;
        Origin = new(null, "Biblioteca"); UsesLibraryOrder = true; PruneHistory(); BuildRoute();
    }
    private static bool SamePath(Track a, Track b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
    public Track? Select(Track track) => SelectOccurrence(items.FirstOrDefault(i => SamePath(i.Track, track))?.Id ?? Guid.Empty)?.Track;
    public QueueItem? SelectOccurrence(Guid id)
    {
        var item = items.FirstOrDefault(i => i.Id == id);
        if (item is null) return null;
        CurrentId = id; Record(id); if (Shuffle) BuildRoute(); return item;
    }
    public Track? Move(int direction) => Navigate(direction, false)?.Track;
    public QueueItem? Navigate(int direction, bool automatic = false)
    {
        if (automatic && Repeat == RepeatMode.Track && CurrentItem is { } current && IsPlayable(current.Track)) return current;
        if (Shuffle && historyIndex >= 0)
        {
            var target = historyIndex + Math.Sign(direction);
            while (target >= 0 && target < history.Count)
            {
                var historic = items.FirstOrDefault(i => i.Id == history[target] && IsPlayable(i.Track));
                if (historic is not null) { historyIndex = target; CurrentId = historic.Id; return historic; }
                target += Math.Sign(direction);
            }
            if (direction < 0) return null;
        }
        var index = route.IndexOf(CurrentId ?? Guid.Empty);
        var step = direction < 0 ? -1 : 1;
        var next = index < 0 ? 0 : index + step;
        for (var count = 0; count < route.Count; count++, next += step)
        {
            if (next < 0 || next >= route.Count)
            {
                if (Repeat != RepeatMode.Queue) return null;
                next = step > 0 ? 0 : route.Count - 1;
            }
            var item = items.First(i => i.Id == route[next]);
            if (!IsPlayable(item.Track)) continue;
            CurrentId = item.Id; Record(item.Id); return item;
        }
        return null;
    }
    private void Record(Guid id)
    {
        if (historyIndex >= 0 && history[historyIndex] == id) return;
        if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(id); historyIndex = history.Count - 1;
    }
    public void SetShuffle(bool enabled)
    {
        if (Shuffle == enabled) return;
        Shuffle = enabled; BuildRoute();
    }
    private void BuildRoute()
    {
        route.Clear(); route.AddRange(items.Select(i => i.Id));
        if (!Shuffle) return;
        // Visited occurrences remain ahead of the current occurrence; only future occurrences are shuffled.
        var routeSet = route.ToHashSet();
        var visited = history.Take(historyIndex + 1).Distinct().Where(routeSet.Contains).ToList();
        if (CurrentId is Guid current) { visited.Remove(current); visited.Add(current); }
        var visitedSet = visited.ToHashSet();
        var future = route.Where(id => !visitedSet.Contains(id)).ToList();
        for (int n = future.Count - 1; n > 0; n--) { int j = random.Next(n + 1); (future[n], future[j]) = (future[j], future[n]); }
        route.Clear(); route.AddRange(visited); route.AddRange(future);
    }
    public QueueItem Add(Track track, bool next = false)
    {
        UsesLibraryOrder = false;
        var entry = new QueueItem(Guid.NewGuid(), track);
        if (next && historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        var position = next && Position >= 0 ? Position + 1 : next ? 0 : items.Count;
        items.Insert(position, entry);
        var routePosition = next && CurrentId is Guid id ? route.IndexOf(id) + 1 : next ? 0 : route.Count;
        route.Insert(routePosition, entry.Id); return entry;
    }
    public bool Remove(IReadOnlyList<Guid> ids)
    {
        UsesLibraryOrder = false;
        var set = ids.ToHashSet(); var removedCurrent = CurrentId is Guid current && set.Contains(current);
        items.RemoveAll(i => set.Contains(i.Id)); route.RemoveAll(set.Contains);
        if (removedCurrent) CurrentId = null;
        PruneHistory(); return removedCurrent;
    }
    private void PruneHistory()
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var retained = history.Take(historyIndex + 1).Where(ids.Contains).ToArray();
        history.RemoveAll(id => !ids.Contains(id)); historyIndex = retained.Length - 1;
    }
    public void UpdateMetadata(IReadOnlyDictionary<Guid, Track> tracks)
    {
        for (int n = 0; n < items.Count; n++)
            if (tracks.TryGetValue(items[n].Track.Id, out var track)) items[n] = items[n] with { Track = track };
    }
    public void Clear() => Load([], new(null, "Fila vazia"));
}
