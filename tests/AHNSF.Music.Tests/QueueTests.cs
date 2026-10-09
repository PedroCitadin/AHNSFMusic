using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class QueueTests
{
    private static Track Track(string name, AvailabilityStatus availability = AvailabilityStatus.Available) => new(name + ".mp3", name, "Artista", TimeSpan.FromMinutes(1)) { Id = Guid.NewGuid(), Availability = availability };
    private static QueueItem Item(Track track) => new(Guid.NewGuid(), track);
    private static PlaybackQueue Queue(params Track[] tracks)
    { var queue = new PlaybackQueue(new Random(7)); queue.Load(tracks.Select(Item), new(Guid.NewGuid(), "Teste")); return queue; }
    [Fact] public void DuplicateOccurrencesAreIndependentlySelectable()
    {
        var a = Track("A"); var q = Queue(a, a); Assert.NotEqual(q.Items[0].Id, q.Items[1].Id);
        q.SelectOccurrence(q.Items[1].Id); Assert.Equal(1, q.Position); Assert.Equal(q.Items[0].Id, q.Navigate(-1)!.Id);
    }
    [Fact] public void NextAndEndInsertionPreserveCurrentIdentity()
    {
        var q = Queue(Track("A"), Track("B")); q.Navigate(1); var current = q.CurrentId;
        var next = q.Add(Track("C"), true); var end = q.Add(Track("D")); Assert.Equal(current, q.CurrentId);
        Assert.Equal(next.Id, q.Navigate(1)!.Id); Assert.Equal("B", q.Navigate(1)!.Track.Title); Assert.Equal(end.Id, q.Navigate(1)!.Id);
    }
    [Fact] public void RemoveOnlyOneDuplicateOccurrence()
    {
        var a = Track("A"); var q = Queue(a, a); q.Navigate(1); var current = q.CurrentId;
        Assert.False(q.Remove([q.Items[1].Id])); Assert.Single(q.Items); Assert.Equal(current, q.CurrentId);
        Assert.True(q.Remove([current!.Value])); Assert.Null(q.Current); Assert.Empty(q.Items);
    }
    [Fact] public void ClearResetsCurrentOriginAndHistory()
    { var q = Queue(Track("A")); q.Navigate(1); q.Clear(); Assert.Empty(q.Items); Assert.Null(q.Current); Assert.Equal(-1, q.Position); Assert.Null(q.Navigate(1)); }
    [Fact] public void SequentialNavigationHasPredictableBoundaries()
    { var q = Queue(Track("A"), Track("B")); Assert.Equal("A", q.Navigate(1)!.Track.Title); Assert.Null(q.Navigate(-1)); Assert.Equal("B", q.Navigate(1)!.Track.Title); Assert.Null(q.Navigate(1)); Assert.Equal("A", q.Navigate(-1)!.Track.Title); }
    [Fact] public void ShuffleDoesNotModifyStoredOrderOrCurrentAndVisitsEveryOccurrenceOnce()
    {
        var a = Track("A"); var q = Queue(a, a, Track("B"), Track("C"), Track("D")); q.Navigate(1);
        var order = q.Items.Select(i => i.Id).ToArray(); var current = q.CurrentId; q.SetShuffle(true); Assert.Equal(current, q.CurrentId);
        var visited = new List<Guid> { current!.Value }; while (q.Navigate(1) is { } next) visited.Add(next.Id);
        Assert.Equal(5, visited.Count); Assert.Equal(5, visited.Distinct().Count()); Assert.Equal(order, q.Items.Select(i => i.Id));
    }
    [Fact] public void ShufflePreviousAndNextRetraceHistory()
    {
        var q = Queue(Track("A"), Track("B"), Track("C")); q.SetShuffle(true); var first = q.Navigate(1)!; var second = q.Navigate(1)!;
        Assert.Equal(first.Id, q.Navigate(-1)!.Id); Assert.Equal(second.Id, q.Navigate(1)!.Id);
    }
    [Fact] public void DisableShuffleReturnsToManualSequenceAtCurrentOccurrence()
    {
        var q = Queue(Track("A"), Track("B"), Track("C")); q.SetShuffle(true); q.SelectOccurrence(q.Items[1].Id);
        q.SetShuffle(false); Assert.Equal("B", q.Current!.Title); Assert.Equal("C", q.Navigate(1)!.Track.Title);
    }
    [Fact] public void ShuffleHistorySkipsOccurrenceThatBecameUnavailable()
    {
        var a = Track("A"); var b = Track("B"); var c = Track("C"); var q = Queue(a, b, c); q.SetShuffle(true);
        var first = q.Navigate(1)!; var second = q.Navigate(1)!; q.Navigate(1);
        q.UpdateMetadata(new Dictionary<Guid, Track> { [second.Track.Id] = second.Track with { Availability = AvailabilityStatus.Missing } });
        Assert.Equal(first.Id, q.Navigate(-1)!.Id);
    }
    [Theory] [InlineData(RepeatMode.Off, false)] [InlineData(RepeatMode.Queue, true)] [InlineData(RepeatMode.Track, true)]
    public void AutomaticEndRespectsRepeatMode(RepeatMode mode, bool repeats)
    { var q = Queue(Track("A")); q.Repeat = mode; q.Navigate(1); Assert.Equal(repeats, q.Navigate(1, true) is not null); }
    [Fact] public void RepeatTrackAffectsAutomaticEndButManualNextAdvances()
    { var q = Queue(Track("A"), Track("B")); q.Repeat = RepeatMode.Track; var a = q.Navigate(1)!; Assert.Equal(a.Id, q.Navigate(1, true)!.Id); Assert.Equal("B", q.Navigate(1, false)!.Track.Title); Assert.Null(q.Navigate(1, false)); }
    [Fact] public void RepeatQueueWithShuffleVisitsAllOccurrencesBeforeWrapping()
    {
        var a = Track("A"); var q = Queue(a, a, Track("B")); q.Repeat = RepeatMode.Queue; q.SetShuffle(true);
        var visits = Enumerable.Range(0, 3).Select(_ => q.Navigate(1, true)!.Id).ToArray(); Assert.Equal(3, visits.Distinct().Count()); Assert.Equal(visits[0], q.Navigate(1, true)!.Id);
    }
    [Fact] public void UnavailableItemsAreSkippedAndAllUnavailableQueueDoesNotLoop()
    {
        var q = Queue(Track("Ausente", AvailabilityStatus.Missing), Track("B"), Track("Inválida", AvailabilityStatus.Invalid)); Assert.Equal("B", q.Navigate(1)!.Track.Title); Assert.Null(q.Navigate(1));
        q.Remove([q.CurrentId!.Value]); q.Repeat = RepeatMode.Queue; Assert.Null(q.Navigate(1, true));
    }
    [Fact] public void AddNextAfterGoingBackInShuffleOverridesForwardHistory()
    { var q = Queue(Track("A"), Track("B"), Track("C")); q.SetShuffle(true); q.Navigate(1); q.Navigate(1); q.Navigate(-1); var next = q.Add(Track("Nova"), true); Assert.Equal(next.Id, q.Navigate(1)!.Id); }
    [Fact] public void RepeatedItemManualSelectionDoesNotLoseUnvisitedShuffleOccurrences()
    {
        var q = Queue(Track("A"), Track("B"), Track("C")); q.SetShuffle(true); q.SelectOccurrence(q.Items[2].Id);
        var visited = new HashSet<Guid> { q.CurrentId!.Value }; while (q.Navigate(1) is { } item) Assert.True(visited.Add(item.Id)); Assert.Equal(3, visited.Count);
    }
    [Fact] public void ActiveQueueEditsDoNotReloadAudioOrResetPosition()
    {
        var audio = new TestAudio(); using var service = new PlaybackService(audio); var a = Track("A"); var b = Track("B"); service.PlayLibrary(a, [a, b]); audio.Position = TimeSpan.FromSeconds(25);
        var current = service.CurrentTrack; service.Add(Track("C"), true); service.Add(Track("D")); service.Remove([service.Queue.Items[^1].Id]); service.SetModes(true, RepeatMode.Queue);
        Assert.Same(current, service.CurrentTrack); Assert.Equal(1, audio.Loads); Assert.Equal(TimeSpan.FromSeconds(25), audio.Position);
        service.ReconcileLibrary([a, b]); Assert.Equal(3, service.Queue.Items.Count); Assert.Equal(1, audio.Loads);
    }
    [Fact] public void RemovingCurrentOrClearingQueueStopsWithoutChangingSavedCollection()
    {
        var audio = new TestAudio(); using var service = new PlaybackService(audio); var a = Track("A"); service.PlayLibrary(a, [a]); service.Add(a);
        service.Remove([service.Queue.CurrentId!.Value]); Assert.Null(service.CurrentTrack); Assert.Single(service.Queue.Items); Assert.Equal(PlayerState.Stopped, audio.State);
        service.Play(); service.Clear(); Assert.Empty(service.Queue.Items); Assert.Null(service.CurrentTrack);
    }
    [Theory] [InlineData(RepeatMode.Off, 1, false)] [InlineData(RepeatMode.Queue, 2, true)] [InlineData(RepeatMode.Track, 2, true)]
    public void AudioEndedUsesDomainNavigation(RepeatMode mode, int loads, bool playing)
    {
        var audio = new TestAudio(); using var service = new PlaybackService(audio); var a = Track("A"); service.PlayLibrary(a, [a]); service.SetModes(false, mode); audio.End();
        Assert.Equal(loads, audio.Loads); Assert.Equal(playing, service.State == PlayerState.Playing);
    }
    [Fact] public void ManualNextWhileRepeatTrackIsOnStartsNextOccurrence()
    { var audio = new TestAudio(); using var service = new PlaybackService(audio); var a = Track("A"); service.PlayLibrary(a, [a, Track("B")]); service.SetModes(true, RepeatMode.Track); service.Next(); Assert.Equal("B", service.CurrentTrack!.Title); }
    [Fact] public void OldEndedCallbackCannotAdvanceNewSession()
    {
        var callbacks = new List<Action>(); var audio = new TestAudio(); using var service = new PlaybackService(audio, dispatch: callbacks.Add);
        var a = Track("A"); service.PlayLibrary(a, [a, Track("B")]); audio.End(); service.PlayLibrary(Track("C"), []); callbacks[0](); Assert.Equal("C", service.CurrentTrack!.Title);
    }
    [Fact] public void AudioErrorStopsEvenWithRepeatAndReportsPortugueseMessage()
    { var audio = new TestAudio(); using var service = new PlaybackService(audio); var a = Track("A"); service.PlayLibrary(a, [a]); service.SetModes(true, RepeatMode.Queue); audio.End(new IOException()); Assert.Null(service.CurrentTrack); Assert.Contains("Não foi possível reproduzir", service.Message); Assert.Equal(1, audio.Loads); }
    [Fact] public async Task LoadPlaylistByDomainServiceAndPlaySpecificDuplicate()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "A"); var track = Assert.Single((await f.StartAsync()).Tracks); var playlists = new SqlitePlaylistService(f.Database, f.Repository);
        var p = await playlists.CreateAsync("P"); await playlists.AddAsync(p.Id, [track.Id, track.Id]); var saved = (await playlists.GetAsync(p.Id))!;
        var audio = new TestAudio(); using var service = new PlaybackService(audio, playlists); await service.LoadPlaylistAsync(p.Id, true, saved.Items[1].Id);
        Assert.Equal(p.Id, service.Queue.Origin.PlaylistId); Assert.Equal(1, service.Queue.Position); Assert.Equal(saved.Items[1].Id, service.Queue.CurrentItem!.PlaylistItemId);
        Assert.NotEqual(saved.Items[1].Id, service.Queue.CurrentItem.Id); Assert.Equal(1, audio.Loads);
    }
    [Fact] public async Task DeletingPlaylistDuringPlaybackKeepsActiveQueueAndCurrentAudio()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3"); var t = Assert.Single((await f.StartAsync()).Tracks); var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t.Id, t.Id]);
        var audio = new TestAudio(); using var playback = new PlaybackService(audio, s); await playback.LoadPlaylistAsync(p.Id, true); var current = playback.CurrentTrack;
        await s.DeleteAsync(p.Id); await playback.RefreshMetadataAsync(); Assert.Same(current, playback.CurrentTrack); Assert.Equal(1, audio.Loads); Assert.Equal(2, playback.Queue.Items.Count); playback.Next(); Assert.Equal(2, audio.Loads);
    }
    [Fact] public async Task MetadataRefreshUpdatesRowsWithoutReloadingCurrentAndMissingStops()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "Antes"); var t = Assert.Single((await f.StartAsync()).Tracks); var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t.Id, t.Id]);
        var audio = new TestAudio(); using var playback = new PlaybackService(audio, s); await playback.LoadPlaylistAsync(p.Id, true); var current = playback.CurrentTrack; audio.Position = TimeSpan.FromSeconds(.5);
        f.WriteTrack("a.mp3", "Depois"); await f.Service.ReconcileAsync(true, default); await playback.RefreshMetadataAsync();
        Assert.All(playback.Queue.Items, i => Assert.Equal("Depois", i.Track.Title)); Assert.Same(current, playback.CurrentTrack); Assert.Equal(1, audio.Loads); Assert.Equal(TimeSpan.FromSeconds(.5), audio.Position);
        File.Delete(t.Path); await f.Service.ReconcileAsync(false, default); await playback.RefreshMetadataAsync(); Assert.Null(playback.CurrentTrack); Assert.Equal(2, playback.Queue.Items.Count); Assert.All(playback.Queue.Items, i => Assert.Equal(AvailabilityStatus.Missing, i.Track.Availability));
    }
    [Fact] public async Task EmptyAndEntirelyMissingPlaylistsDoNotOpenAudio()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3"); var t = Assert.Single((await f.StartAsync()).Tracks); var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P");
        var audio = new TestAudio(); using var playback = new PlaybackService(audio, s); await playback.LoadPlaylistAsync(p.Id, true); Assert.Equal(0, audio.Loads);
        await s.AddAsync(p.Id, [t.Id]); File.Delete(t.Path); await playback.LoadPlaylistAsync(p.Id, true); Assert.Equal(0, audio.Loads); Assert.Single(playback.Queue.Items);
    }
    [Fact] public async Task MixedPlaylistStartsFirstAvailableOccurrenceAndPreservesMissing()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B"); var t = (await f.StartAsync()).Tracks; var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t[0].Id, t[1].Id]); File.Delete(t[0].Path);
        var audio = new TestAudio(); using var playback = new PlaybackService(audio, s); await playback.LoadPlaylistAsync(p.Id, true); Assert.Equal("B", playback.CurrentTrack!.Title); Assert.Equal(2, playback.Queue.Items.Count);
    }
    [Fact] public async Task ShuffleAndQueueEditsNeverMutatePersistedPlaylist()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3"); var t = Assert.Single((await f.StartAsync()).Tracks); var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t.Id, t.Id, t.Id]); var before = (await s.GetAsync(p.Id))!.Items;
        var audio = new TestAudio(); using var playback = new PlaybackService(audio, s); await playback.LoadPlaylistAsync(p.Id, true); playback.SetModes(true, RepeatMode.Queue); playback.Add(t, true); playback.Remove([playback.Queue.Items[^1].Id]); playback.Next(); playback.Clear();
        Assert.Equal(before.Select(i => i.Id), (await s.GetAsync(p.Id))!.Items.Select(i => i.Id));
    }
    [Fact] public async Task RestartKeepsPreferencesAndDefaultButDoesNotResumePlayback()
    {
        await using var f = new LibraryFixture(); var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await s.UpdateAsync(p.Id, p.Name, "", true);
        var settings = new SqliteSettingsStore(f.Database); await settings.SaveAsync(new(Volume: .4f, Shuffle: true, Repeat: RepeatMode.Track));
        var loaded = await new SqliteSettingsStore(f.Database).LoadAsync(); Assert.True(loaded.Shuffle); Assert.Equal(RepeatMode.Track, loaded.Repeat);
        var audio = new TestAudio(); using var playback = new PlaybackService(audio, new SqlitePlaylistService(f.Database, new(f.Database))); playback.SetModes(loaded.Shuffle, loaded.Repeat);
        Assert.Equal(PlayerState.Stopped, audio.State); Assert.Empty(playback.Queue.Items); Assert.Equal(p.Id, (await s.GetDefaultAsync())!.Id);
    }
    internal sealed class TestAudio : IAudioEngine
    {
        public event EventHandler<AudioEndedEventArgs>? Ended;
        public PlayerState State { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(1);
        public float Volume { get; set; }
        public int Loads { get; private set; }
        public void Load(string path) { Loads++; Position = TimeSpan.Zero; }
        public void Play() => State = PlayerState.Playing;
        public void Pause() => State = PlayerState.Paused;
        public void Stop() { State = PlayerState.Stopped; Position = TimeSpan.Zero; }
        public void End(Exception? error = null) { State = PlayerState.Stopped; Ended?.Invoke(this, new(error)); }
        public void Dispose() { }
    }
}
