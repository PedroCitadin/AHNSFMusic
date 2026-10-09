using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using AHNSF.Music.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AHNSF.Music.Tests;
public sealed class ScheduleTests
{
    private static readonly ScheduleDays Weekdays = ScheduleDays.Monday | ScheduleDays.Tuesday | ScheduleDays.Wednesday | ScheduleDays.Thursday | ScheduleDays.Friday;
    private static DateTimeOffset At(string date) => DateTimeOffset.Parse(date + "+00:00");
    private static ScheduleRule Rule(ScheduleDays days = ScheduleDays.All, int start = 8, int end = 18, int priority = 0) =>
        new(Guid.NewGuid(), "Expediente", Guid.NewGuid(), days, new(start, 0), new(end, 0), 62, priority, true, default, default);
    private static ScheduleEvaluation Evaluate(DateTimeOffset now, params ScheduleRule[] rules) => new ScheduleEvaluator().Evaluate(rules, now, TimeZoneInfo.Utc);
    [Theory]
    [InlineData("2026-10-05T08:00", true)] [InlineData("2026-10-06T12:00", true)]
    [InlineData("2026-10-07T12:00", true)] [InlineData("2026-10-08T12:00", true)]
    [InlineData("2026-10-09T17:59", true)] [InlineData("2026-10-10T12:00", false)]
    [InlineData("2026-10-11T12:00", false)] [InlineData("2026-10-05T07:59", false)]
    [InlineData("2026-10-05T18:00", false)] [InlineData("2026-10-05T19:00", false)]
    public void WeekdaysAndInclusiveStartExclusiveEnd(string time, bool active) => Assert.Equal(active, Evaluate(At(time), Rule(Weekdays)).Winner is not null);
    [Fact] public void SaturdayIsIndependent() => Assert.NotNull(Evaluate(At("2026-10-10T12:00"), Rule(ScheduleDays.Saturday)).Winner);
    [Fact] public void DisabledRulesDoNotRunOrCreateTransitions()
    { var e = Evaluate(At("2026-10-05T12:00"), Rule() with { IsEnabled = false }); Assert.Null(e.Winner); Assert.Null(e.NextTransitionUtc); }
    [Theory] [InlineData("2026-10-09T22:00", true)] [InlineData("2026-10-10T01:59", true)] [InlineData("2026-10-10T02:00", false)] [InlineData("2026-10-09T01:00", false)]
    public void OvernightUsesStartDay(string time, bool active) => Assert.Equal(active, Evaluate(At(time), Rule(ScheduleDays.Friday, 22, 2)).Winner is not null);
    [Fact] public void HighestPriorityWinsAndReportsConflict()
    { var low = Rule(priority: 1); var high = Rule(priority: 2); var e = Evaluate(At("2026-10-05T12:00"), low, high); Assert.Equal(high.Id, e.Winner!.Rule.Id); Assert.Equal(2, e.Active.Count); }
    [Fact] public void LatestEffectiveStartBreaksTie()
    { var earlier = Rule(start: 8); var later = Rule(start: 10); Assert.Equal(later.Id, Evaluate(At("2026-10-05T12:00"), earlier, later).Winner!.Rule.Id); }
    [Fact] public void SmallestCanonicalIdBreaksFinalTie()
    { var a = Rule() with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") }; var b = a with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") }; Assert.Equal(a.Id, Evaluate(At("2026-10-05T12:00"), b, a).Winner!.Rule.Id); }
    [Fact] public void NextTransitionIgnoresLosingBoundariesAndResumesLowerRule()
    {
        var a = Rule(priority: 10, end: 16); var b = Rule(start: 10, end: 12); var c = Rule(end: 18);
        var e = Evaluate(At("2026-10-05T09:00"), a, b, c); Assert.Equal(At("2026-10-05T16:00"), e.NextTransitionUtc); Assert.Equal(c.Id, e.NextWinner!.Rule.Id);
    }
    [Fact] public void NextWeekTransitionAfterLastEnabledDay()
    { Assert.Equal(At("2026-10-12T08:00"), Evaluate(At("2026-10-09T19:00"), Rule(ScheduleDays.Monday)).NextTransitionUtc); }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void InvalidRuleIsRejected(int invalid)
    {
        var r = Rule(); r = invalid switch { 0 => r with { Name = " " }, 1 => r with { PlaylistId = null }, 2 => r with { Days = ScheduleDays.None }, 3 => r with { Days = (ScheduleDays)128 }, 4 => r with { EndTime = r.StartTime }, 5 => r with { Volume = -1 }, 6 => r with { Volume = double.NaN }, _ => r with { Priority = 1001 } };
        Assert.Throws<ArgumentException>(() => ScheduleRule.Validate(r));
    }
    private static TimeZoneInfo DstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        return TimeZoneInfo.CreateCustomTimeZone("TestDST", TimeSpan.FromHours(-5), "Test", "Test", "DST", [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2026, 1, 1), new(2026, 12, 31), TimeSpan.FromHours(1), start, end)]);
    }
    [Fact] public void SpringGapAdvancesToFirstValidMinute()
    { Assert.Equal(At("2026-03-08T07:00"), ScheduleEvaluator.Resolve(new(2026, 3, 8, 2, 30, 0), DstZone(), false)); }
    [Fact] public void RepeatedHourStartsFirstAndEndsLastOccurrence()
    { var local = new DateTime(2026, 11, 1, 1, 30, 0); Assert.Equal(At("2026-11-01T05:30"), ScheduleEvaluator.Resolve(local, DstZone(), false)); Assert.Equal(At("2026-11-01T06:30"), ScheduleEvaluator.Resolve(local, DstZone(), true)); }

    private sealed class Clock : IScheduleClock
    { public DateTimeOffset UtcNow { get; set; } = At("2026-10-05T12:00"); public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc; }
    private sealed class Harness : IAsyncDisposable
    {
        public LibraryFixture Fixture { get; } = new();
        public SqlitePlaylistService Playlists { get; }
        public SqliteScheduleService Store { get; }
        public QueueTests.TestAudio Audio { get; } = new() { Volume = .22f };
        public PlaybackService Player { get; }
        public Clock Clock { get; } = new();
        public ScheduleCoordinator Coordinator { get; }
        public Playlist Playlist { get; private set; } = null!;
        public ScheduleRule Saved { get; private set; } = null!;
        public Harness()
        {
            Playlists = new(Fixture.Database, Fixture.Repository); Store = new(Fixture.Database, Playlists); Player = new(Audio, Playlists);
            Coordinator = new(Store, Playlists, Player, Clock, new ScheduleEvaluator());
        }
        public async Task Setup(bool automatic = true, bool empty = false)
        {
            Playlist = await Playlists.CreateAsync("Ambiente");
            if (!empty) { Fixture.WriteTrack("a.mp3", "A"); Fixture.WriteTrack("b.mp3", "B"); var tracks = (await Fixture.StartAsync()).Tracks; await Playlists.AddAsync(Playlist.Id, tracks.Select(t => t.Id).ToArray()); }
            Saved = await Store.SaveAsync(Rule() with { PlaylistId = Playlist.Id });
            if (automatic) await Store.SetModeAsync(OperationMode.Automatic);
        }
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); Player.Dispose(); await Fixture.DisposeAsync(); }
    }
    [Fact] public async Task SqliteCreateEditReopenPreservesIdentityAndManualDefault()
    {
        await using var h = new Harness(); await h.Setup(false); Assert.Equal(OperationMode.Manual, (await h.Store.LoadAsync()).Mode);
        var changed = await h.Store.SaveAsync(h.Saved with { Name = "Novo", Volume = 90 }); Assert.Equal(h.Saved.CreatedAtUtc, changed.CreatedAtUtc);
        var reopened = new SqliteScheduleService(h.Fixture.Database, h.Playlists); Assert.Equal(changed, Assert.Single((await reopened.LoadAsync()).Rules));
        await h.Store.SetModeAsync(OperationMode.Automatic); Assert.Equal(OperationMode.Automatic, (await reopened.LoadAsync()).Mode);
    }
    [Fact] public async Task SqliteEnableDisableDeleteDoesNotModifyPlaylist()
    {
        await using var h = new Harness(); await h.Setup(); var items = (await h.Playlists.GetAsync(h.Playlist.Id))!.Items;
        await h.Store.SetEnabledAsync(h.Saved.Id, false); Assert.False(Assert.Single((await h.Store.LoadAsync()).Rules).IsEnabled);
        await h.Store.SetEnabledAsync(h.Saved.Id, true); Assert.True(Assert.Single((await h.Store.LoadAsync()).Rules).IsEnabled);
        await h.Store.DeleteAsync(h.Saved.Id); Assert.Empty((await h.Store.LoadAsync()).Rules); Assert.Equal(items, (await h.Playlists.GetAsync(h.Playlist.Id))!.Items);
    }
    [Fact] public async Task MigrationThreePreservesSettingsTracksAndPlaylistItems()
    {
        await using var h = new Harness(); var settings = new SqliteSettingsStore(h.Fixture.Database); await settings.SaveAsync(new() { Volume = .44f });
        await h.Setup(false); Assert.Equal(.44f, (await settings.LoadAsync()).Volume);
        using var db = new SqliteConnection($"Data Source={h.Fixture.Database}"); await db.OpenAsync(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM LibraryMigrations WHERE Version IN(1,2,3)"; Assert.Equal(3L, cmd.ExecuteScalar());
        Assert.Equal(2, (await h.Playlists.GetAsync(h.Playlist.Id))!.Items.Count);
    }
    [Fact] public async Task UnknownPlaylistRollsBackSave()
    { await using var h = new Harness(); await h.Setup(); await Assert.ThrowsAsync<SqliteException>(() => h.Store.SaveAsync(h.Saved with { PlaylistId = Guid.NewGuid() })); Assert.Equal(h.Saved, Assert.Single((await h.Store.LoadAsync()).Rules)); }
    [Fact] public async Task DeletedPlaylistKeepsBrokenRuleAndShowsWarning()
    {
        await using var h = new Harness(); await h.Setup(); await h.Playlists.DeleteAsync(h.Playlist.Id); Assert.Null(Assert.Single((await h.Store.LoadAsync()).Rules).PlaylistId);
        await h.Coordinator.StartAsync(false); Assert.NotEmpty(h.Coordinator.Status.Warning); Assert.Equal(PlayerState.Stopped, h.Player.State);
    }
    [Fact] public async Task StartupDuringIntervalAppliesVolumeAndStarts()
    { await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); Assert.Equal(PlayerState.Playing, h.Player.State); Assert.Equal(.62f, h.Audio.Volume); Assert.Equal(h.Saved.Id, h.Coordinator.Status.Evaluation.Winner!.Rule.Id); }
    [Fact] public async Task OutsideIntervalStopsExistingManualPlayback()
    { await using var h = new Harness(); await h.Setup(); await h.Player.LoadPlaylistAsync(h.Playlist.Id, true); h.Clock.UtcNow = At("2026-10-05T07:00"); await h.Coordinator.StartAsync(false); Assert.Equal(PlayerState.Stopped, h.Player.State); }
    [Fact] public async Task ManualDoesNotStartStopOrChangeVolume()
    { await using var h = new Harness(); await h.Setup(false); await h.Coordinator.StartAsync(false); Assert.Equal(0, h.Audio.Loads); await h.Player.LoadPlaylistAsync(h.Playlist.Id, true); h.Clock.UtcNow = At("2026-10-05T20:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Playing, h.Player.State); Assert.Equal(.22f, h.Audio.Volume); }
    [Fact] public async Task ModeTransitionsReevaluateWithoutInterruptingManualEntry()
    { await using var h = new Harness(); await h.Setup(false); await h.Coordinator.StartAsync(false); await h.Coordinator.SetModeAsync(OperationMode.Automatic); Assert.Equal(PlayerState.Playing, h.Player.State); await h.Coordinator.SetModeAsync(OperationMode.Manual); Assert.Equal(PlayerState.Playing, h.Player.State); Assert.Equal(.22f, h.Audio.Volume); }
    [Fact] public async Task PauseStaysSuspendedUntilNextWinnerTransition()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); h.Coordinator.NotifyManualIntervention(); h.Player.Pause();
        await h.Coordinator.EvaluateAsync(); await h.Coordinator.EvaluateAsync(); Assert.True(h.Coordinator.Status.IsSuspended); Assert.Equal(PlayerState.Paused, h.Player.State);
        h.Clock.UtcNow = At("2026-10-05T18:00"); await h.Coordinator.EvaluateAsync(); Assert.False(h.Coordinator.Status.IsSuspended); Assert.Equal(PlayerState.Stopped, h.Player.State);
        h.Clock.UtcNow = At("2026-10-06T08:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Playing, h.Player.State);
    }
    [Fact] public async Task RepeatedEvaluationsPreserveTrackQueueIdsAndPosition()
    { await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); h.Audio.Position = TimeSpan.FromSeconds(23); var ids = h.Player.Queue.Items.Select(i => i.Id).ToArray(); var loads = h.Audio.Loads; for (int i = 0; i < 10; i++) await h.Coordinator.EvaluateAsync(); Assert.Equal(loads, h.Audio.Loads); Assert.Equal(ids, h.Player.Queue.Items.Select(i => i.Id)); Assert.Equal(23, h.Audio.Position.TotalSeconds); }
    [Theory] [InlineData(RepeatMode.Off)] [InlineData(RepeatMode.Track)] [InlineData(RepeatMode.Queue)]
    public async Task ScheduledQueueRepeatsWithoutChangingUserPreference(RepeatMode mode)
    { await using var h = new Harness(); await h.Setup(); h.Player.SetModes(false, mode); await h.Coordinator.StartAsync(false); Assert.Equal("A", h.Player.CurrentTrack!.Title); h.Audio.End(); Assert.Equal("B", h.Player.CurrentTrack!.Title); h.Audio.End(); Assert.Equal("A", h.Player.CurrentTrack!.Title); Assert.Equal(mode, h.Player.Queue.Repeat); }
    [Fact] public async Task ClockJumpsAndResumeReconcileDesiredState()
    { await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); h.Clock.UtcNow = At("2026-10-05T19:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Stopped, h.Player.State); h.Clock.UtcNow = At("2026-10-05T09:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Playing, h.Player.State); h.Clock.UtcNow = At("2026-10-07T20:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Stopped, h.Player.State); }
    [Fact] public async Task TimeZoneChangeReevaluatesLocalWindow()
    { await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); h.Clock.TimeZone = TimeZoneInfo.CreateCustomTimeZone("Changed", TimeSpan.FromHours(8), "Changed", "Changed"); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Stopped, h.Player.State); }
    [Fact] public async Task EmptyPlaylistWarnsWithoutAggressiveRetry()
    { await using var h = new Harness(); await h.Setup(empty: true); await h.Coordinator.StartAsync(false); for (int i = 0; i < 10; i++) await h.Coordinator.EvaluateAsync(); Assert.NotEmpty(h.Coordinator.Status.Warning); Assert.Equal(0, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); }
    [Fact] public async Task MissingTracksWarnWithoutStarting()
    { await using var h = new Harness(); await h.Setup(); foreach (var item in (await h.Playlists.GetAsync(h.Playlist.Id))!.Items) File.Delete(item.Track.Path); await h.Coordinator.StartAsync(false); Assert.NotEmpty(h.Coordinator.Status.Warning); Assert.Equal(0, h.Audio.Loads); }
    [Fact] public async Task ConcurrentEvaluationsStartOnlyOnce()
    { await using var h = new Harness(); await h.Setup(); await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => h.Coordinator.EvaluateAsync())); Assert.Equal(1, h.Audio.Loads); Assert.Equal(PlayerState.Playing, h.Player.State); }
    [Fact] public async Task HigherPrioritySwitchAndEndResumeLowerRule()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false);
        var high = await h.Store.SaveAsync(Rule(start: 13, end: 14, priority: 10) with { PlaylistId = h.Playlist.Id, Name = "Prioritária", Volume = 80 });
        h.Clock.UtcNow = At("2026-10-05T13:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(high.Id, h.Coordinator.Status.Evaluation.Winner!.Rule.Id); Assert.Equal(.8f, h.Audio.Volume);
        h.Clock.UtcNow = At("2026-10-05T14:00"); await h.Coordinator.EvaluateAsync(); Assert.Equal(h.Saved.Id, h.Coordinator.Status.Evaluation.Winner!.Rule.Id); Assert.Equal(.62f, h.Audio.Volume);
    }
    [Fact] public async Task ActiveVolumeEditDoesNotReloadQueue()
    { await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); var loads = h.Audio.Loads; await h.Store.SaveAsync(h.Saved with { Volume = 77 }); await h.Coordinator.EvaluateAsync(); Assert.Equal(loads, h.Audio.Loads); Assert.Equal(.77f, h.Audio.Volume); }
    private sealed class Scanner : IMusicScanner
    { public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new ScanResult([], [])); }
    [Fact] public async Task ViewModelScheduledVolumeDoesNotPersistOrSuspendAndManualVolumeDoes()
    {
        await using var h = new Harness(); await h.Setup(); var settings = new SqliteSettingsStore(h.Fixture.Database); await settings.SaveAsync(new(Volume: .22f));
        await using var vm = new MainViewModel(h.Audio, new Scanner(), settings, h.Fixture.Logger, playlists: h.Playlists, playback: h.Player, scheduleStore: h.Store, scheduler: h.Coordinator);
        await vm.InitializeAsync(); Assert.False(h.Coordinator.Status.IsSuspended); Assert.Equal(62, vm.Volume, 3); Assert.Equal(.22f, vm.Settings.Volume);
        vm.Volume = 47; Assert.True(h.Coordinator.Status.IsSuspended); Assert.Equal(.47f, h.Audio.Volume); Assert.Equal(47, vm.Volume, 3);
        await h.Coordinator.EvaluateAsync(); Assert.Equal(.47f, h.Audio.Volume); await vm.SaveWindowAsync(1100, 760, 0, 0); Assert.Equal(.47f, (await settings.LoadAsync()).Volume);
    }
    [Fact] public async Task ViewModelRuleEditorValidatesAndRequiresDeleteConfirmation()
    {
        await using var h = new Harness(); await h.Setup(false);
        await using var vm = new MainViewModel(h.Audio, new Scanner(), new SqliteSettingsStore(h.Fixture.Database), h.Fixture.Logger, playlists: h.Playlists, playback: h.Player, scheduleStore: h.Store, scheduler: h.Coordinator);
        await vm.InitializeAsync(); vm.NewRuleCommand.Execute(null); await vm.SaveRuleCommand.ExecuteAsync(null); Assert.NotEmpty(vm.ScheduleWarning); Assert.Single(vm.Rules);
        vm.RuleName = "Plantão"; vm.RuleStart = "22:00"; vm.RuleEnd = "02:00"; await vm.SaveRuleCommand.ExecuteAsync(null); Assert.Equal(2, vm.Rules.Count);
        vm.SelectedRule = vm.Rules.Single(r => r.Name == "Plantão"); await vm.ToggleRuleCommand.ExecuteAsync(null); Assert.False(vm.SelectedRule!.IsEnabled);
        await vm.ConfirmDeleteRuleCommand.ExecuteAsync(null); Assert.Equal(2, vm.Rules.Count);
        vm.RequestDeleteRuleCommand.Execute(null); vm.CancelDeleteRuleCommand.Execute(null); await vm.ConfirmDeleteRuleCommand.ExecuteAsync(null); Assert.Equal(2, vm.Rules.Count);
        vm.RequestDeleteRuleCommand.Execute(null); await vm.ConfirmDeleteRuleCommand.ExecuteAsync(null); Assert.Single(vm.Rules);
        Assert.Contains("Sem programação", vm.WeeklySummary);
    }
    [Fact] public async Task UnexpectedAudioFailureWarnsAndWaitsBeforeRetry()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); var loads = h.Audio.Loads;
        h.Audio.End(new IOException("Saída indisponível")); await h.Coordinator.EvaluateAsync(); Assert.NotEmpty(h.Coordinator.Status.Warning);
        await h.Coordinator.EvaluateAsync(); Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State);
        h.Clock.UtcNow += TimeSpan.FromMinutes(1); await h.Coordinator.EvaluateAsync(); Assert.Equal(PlayerState.Playing, h.Player.State);
    }
    private sealed class DelayedPlayback(IPlaybackService inner) : IPlaybackService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public PlaybackQueue Queue => inner.Queue;
        public Track? CurrentTrack => inner.CurrentTrack;
        public PlayerState State => inner.State;
        public string Message => inner.Message;
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
    [Fact] public async Task ManualInterventionCancelsPendingLoadWithoutReplacingManualQueue()
    {
        await using var h = new Harness(); await h.Setup(); var delayed = new DelayedPlayback(h.Player);
        await using var coordinator = new ScheduleCoordinator(h.Store, h.Playlists, delayed, h.Clock, new ScheduleEvaluator());
        var evaluating = coordinator.EvaluateAsync(); await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.NotifyManualIntervention(); await h.Player.LoadPlaylistAsync(h.Playlist.Id, true); var ids = h.Player.Queue.Items.Select(i => i.Id).ToArray(); delayed.Release.TrySetResult(); await evaluating;
        Assert.Equal(ids, h.Player.Queue.Items.Select(i => i.Id)); Assert.Equal(PlayerState.Playing, h.Player.State); Assert.True(coordinator.Status.IsSuspended);
    }
    [Fact] public async Task ShutdownCancelsPendingScheduleAndDoesNotStartAudio()
    {
        await using var h = new Harness(); await h.Setup(); var delayed = new DelayedPlayback(h.Player);
        await using var coordinator = new ScheduleCoordinator(h.Store, h.Playlists, delayed, h.Clock, new ScheduleEvaluator());
        var evaluating = coordinator.EvaluateAsync(); await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); coordinator.StopScheduling(); await evaluating;
        Assert.Equal(0, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State);
    }
}
