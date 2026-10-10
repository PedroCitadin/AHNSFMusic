using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class AudioDeviceRecoveryTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public LibraryFixture Fixture { get; } = new();
        public AudioDeviceTests.Discovery Discovery { get; } = new();
        public AudioDeviceTests.RoutedAudio Audio { get; } = new();
        public SqlitePlaylistService Playlists { get; }
        public SqliteScheduleService Store { get; }
        public PlaybackService Player { get; }
        public OperationsTests.Clock Clock { get; } = new();
        public AudioDeviceService Devices { get; }
        public ScheduleCoordinator Coordinator { get; }
        public Playlist Playlist { get; private set; } = null!;
        public Harness(Func<IPlaybackService, IPlaybackService>? decorate = null)
        {
            Playlists = new(Fixture.Database, Fixture.Repository); Store = new(Fixture.Database, Playlists); Player = new(Audio, Playlists);
            Devices = new(Discovery, Audio); Coordinator = new(Store, Playlists, decorate?.Invoke(Player) ?? Player, Clock, new ScheduleEvaluator(), devices: Devices);
        }
        public async Task Setup(bool automatic = true)
        {
            Playlist = await Playlists.CreateAsync("Hospital"); Fixture.WriteTrack("a.mp3", "A"); Fixture.WriteTrack("b.mp3", "B");
            await Playlists.AddAsync(Playlist.Id, (await Fixture.StartAsync()).Tracks.Select(t => t.Id).ToArray());
            await Store.SaveAsync(new(Guid.Empty, "Expediente", Playlist.Id, ScheduleDays.All, new(8, 0), new(18, 0), 62, 0, true, default, default));
            if (automatic) await Store.SetModeAsync(OperationMode.Automatic);
            await Devices.StartAsync(AudioDeviceTests.Specific(), false); await Coordinator.StartAsync(false);
        }
        public async Task Disconnect() { Discovery.Devices = [AudioDeviceTests.Speakers(true)]; await Devices.RefreshAsync(); await Coordinator.EvaluateAsync(); }
        public async Task Reconnect(string id = "usb-stable-id") { Discovery.Devices = [AudioDeviceTests.Usb(id: id), AudioDeviceTests.Speakers()]; await Devices.RefreshAsync(); await Coordinator.EvaluateAsync(); }
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); await Devices.DisposeAsync(); Player.Dispose(); await Fixture.DisposeAsync(); }
    }
    [Fact] public async Task AutomaticWaitsWhileMissingAndRecoversSameIdInsideWindow()
    {
        await using var h = new Harness(); await h.Setup(); Assert.Equal(PlayerState.Playing, h.Player.State); await h.Disconnect(); var loads = h.Audio.Loads;
        for (var i = 0; i < 20; i++) { h.Clock.UtcNow += TimeSpan.FromSeconds(5); await h.Coordinator.EvaluateAsync(); }
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.False(h.Coordinator.Recovery.IsRecovering);
        await h.Reconnect(); Assert.Equal(PlayerState.Playing, h.Player.State); Assert.Equal("usb-stable-id", h.Audio.Id); Assert.Equal(.62f, h.Audio.Volume);
        Assert.Equal(h.Playlist.Id, h.Player.Queue.Origin.PlaylistId);
    }
    [Fact] public async Task ManualReconnectionNeverStarts()
    {
        await using var h = new Harness(); await h.Setup(false); await h.Player.LoadPlaylistAsync(h.Playlist.Id, true); await h.Disconnect(); var loads = h.Audio.Loads;
        await h.Reconnect(); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.Equal(loads, h.Audio.Loads); h.Player.Play(); Assert.Equal(PlayerState.Playing, h.Player.State);
    }
    [Fact] public async Task ReconnectionAfterWindowNeverStarts()
    {
        await using var h = new Harness(); await h.Setup(); await h.Disconnect(); var loads = h.Audio.Loads; h.Clock.UtcNow = DateTimeOffset.Parse("2026-10-05T18:00:00+00:00"); await h.Reconnect();
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.Null(h.Coordinator.Status.Evaluation.Winner);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ReconnectionPreservesPauseAndStopInterventions(bool pause)
    {
        await using var h = new Harness(); await h.Setup(); h.Coordinator.NotifyManualIntervention(); if (pause) h.Player.Pause(); else h.Player.Stop();
        await h.Disconnect(); var loads = h.Audio.Loads; await h.Reconnect(); Assert.True(h.Coordinator.Status.IsSuspended); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.Equal(loads, h.Audio.Loads);
    }
    [Fact] public async Task DifferentIdNeverStartsDespiteSameName()
    {
        await using var h = new Harness(); await h.Setup(); await h.Disconnect(); var loads = h.Audio.Loads; await h.Reconnect("new-id");
        Assert.Equal(loads, h.Audio.Loads); Assert.False(h.Devices.Status.CanPlay); Assert.Equal(PlayerState.Stopped, h.Player.State);
    }
    [Fact] public async Task ConcurrentReconnectEvaluationsStartOnlyOneSession()
    {
        await using var h = new Harness(); await h.Setup(); await h.Disconnect(); var loads = h.Audio.Loads;
        h.Discovery.Devices = [AudioDeviceTests.Usb(), AudioDeviceTests.Speakers()]; await h.Devices.RefreshAsync();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => h.Coordinator.EvaluateAsync())); Assert.Equal(loads + 1, h.Audio.Loads);
    }
    [Fact] public async Task ReconnectedDriverFailureUsesExistingBoundedRecovery()
    {
        await using var h = new Harness(); await h.Setup(); await h.Disconnect(); h.Audio.FailLoad = true; await h.Reconnect(); var start = h.Clock.UtcNow; var loads = h.Audio.Loads;
        for (var i = 1; i <= 120; i++) { h.Clock.UtcNow = start.AddSeconds(i); await h.Coordinator.EvaluateAsync(); }
        Assert.Equal(loads + 3, h.Audio.Loads); Assert.True(h.Coordinator.Recovery.Exhausted); Assert.Equal(PlayerState.Stopped, h.Player.State);
    }
    [Fact] public async Task OperatorCanCancelRecoveryAfterReconnect()
    {
        await using var h = new Harness(); await h.Setup(); await h.Disconnect(); h.Audio.FailLoad = true; await h.Reconnect(); var loads = h.Audio.Loads;
        h.Coordinator.NotifyManualIntervention(); h.Audio.FailLoad = false; h.Clock.UtcNow += TimeSpan.FromSeconds(5); await h.Coordinator.EvaluateAsync();
        Assert.Equal(loads, h.Audio.Loads); Assert.True(h.Coordinator.Status.IsSuspended); Assert.False(h.Coordinator.Recovery.IsRecovering);
    }
    [Fact] public async Task SwitchingOutputIsInterventionAndPreservesPlaybackQueueAndVolume()
    {
        await using var h = new Harness(); await h.Setup(); h.Audio.Position = TimeSpan.FromSeconds(23); var ids = h.Player.Queue.Items.Select(i => i.Id).ToArray(); var current = h.Player.CurrentTrack;
        h.Coordinator.NotifyManualIntervention(); var volume = h.Audio.Volume; await h.Devices.SelectAsync(new(AudioDeviceSelectionMode.Specific, "internal-id")); await h.Coordinator.EvaluateAsync();
        Assert.Equal("internal-id", h.Audio.Id); Assert.Equal(PlayerState.Playing, h.Player.State); Assert.Same(current, h.Player.CurrentTrack); Assert.Equal(ids, h.Player.Queue.Items.Select(i => i.Id));
        Assert.Equal(23, h.Audio.Position.TotalSeconds); Assert.Equal(volume, h.Audio.Volume); Assert.True(h.Coordinator.Status.IsSuspended);
    }
    [Fact] public async Task ShutdownCancelsDeviceDrivenRecovery()
    {
        await using var h = new Harness(); await h.Setup(); await h.Disconnect(); var loads = h.Audio.Loads; h.Coordinator.StopScheduling();
        h.Discovery.Devices = [AudioDeviceTests.Usb()]; await h.Devices.RefreshAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Coordinator.EvaluateAsync());
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State);
    }
    private sealed class DelayedPlayer(IPlaybackService inner) : IPlaybackService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public PlaybackQueue Queue => inner.Queue;
        public Track? CurrentTrack => inner.CurrentTrack;
        public PlayerState State => inner.State;
        public string Message => inner.Message;
        public Exception? LastError => inner.LastError;
        public float Volume { get => inner.Volume; set => inner.Volume = value; }
        public void SetScheduledRepeat(bool enabled) => inner.SetScheduledRepeat(enabled);
        public async Task LoadPlaylistAsync(Guid id, bool start = false, Guid? itemId = null, CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); await inner.LoadPlaylistAsync(id, start, itemId, token); }
        public void Play() => inner.Play(); public void PlayOccurrence(Guid id) => inner.PlayOccurrence(id);
        public void PlayLibrary(Track track, IReadOnlyList<Track> library) => inner.PlayLibrary(track, library);
        public void Pause() => inner.Pause(); public void Stop() => inner.Stop(); public void Next() => inner.Next(); public void Previous() => inner.Previous();
        public void Add(Track track, bool next = false) => inner.Add(track, next); public void Remove(IReadOnlyList<Guid> ids) => inner.Remove(ids); public void Clear() => inner.Clear();
        public void SetModes(bool shuffle, RepeatMode repeat) => inner.SetModes(shuffle, repeat);
        public Task RefreshMetadataAsync(CancellationToken token = default) => inner.RefreshMetadataAsync(token);
    }
    [Fact] public async Task DisconnectCancelsPendingPlaylistLoadBeforeItCanStart()
    {
        DelayedPlayer delayed = null!; await using var h = new Harness(player => delayed = new(player)); await h.Setup(false);
        var starting = h.Coordinator.SetModeAsync(OperationMode.Automatic); await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        h.Discovery.Devices = [AudioDeviceTests.Speakers(true)]; await h.Devices.RefreshAsync(); await starting.WaitAsync(TimeSpan.FromSeconds(3));
        delayed.Release.TrySetResult(); await h.Coordinator.EvaluateAsync(); Assert.Equal(0, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State);
    }
}
