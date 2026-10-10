using System.IO.Pipes;
using System.Text;
using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Desktop.Platform;
using AHNSF.Music.Desktop.Shell;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

// Fase 0.6: auto-start, tray/close policy, single instance, bounded recovery, suspend/resume, preferences, shutdown and modal focus.
// The Windows registry, tray and audio device are replaced by fakes; nothing here touches the real HKCU Run key.
public sealed class OperationsTests
{
    private const string Exe = @"C:\Program Files\AHNSF Music\AHNSF.Music.Desktop.exe";
    private static DateTimeOffset At(string date) => DateTimeOffset.Parse(date + "+00:00");

    internal sealed class FakeRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase) { ["Outro aplicativo"] = @"""C:\Outro\outro.exe"" --tray" };
        public Dictionary<string, bool> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Fail { get; set; }
        public int Writes { get; private set; }
        public IReadOnlyDictionary<string, string> ReadEntries() { Throw(); return new Dictionary<string, string>(Entries, StringComparer.OrdinalIgnoreCase); }
        public void Write(string name, string command) { Throw(); Writes++; Entries[name] = command; }
        public void Delete(string name) { Throw(); Entries.Remove(name); }
        public bool? IsApprovedByWindows(string name) => Approved.TryGetValue(name, out var approved) ? approved : null;
        private void Throw() { if (Fail) throw new UnauthorizedAccessException("Acesso negado ao registro (simulado)"); }
    }

    // 1, 3, 5 — registration with a quoted path containing spaces, a single entry and other apps untouched.
    [Fact] public void EnableWritesSingleQuotedEntryWithoutTouchingOtherApplications()
    {
        var registry = new FakeRegistry(); var service = new AutoStartService(registry, Exe);
        Assert.Equal(AutoStartState.Disabled, service.Inspect(false).State);
        var status = service.Apply(true); service.Apply(true);
        Assert.Equal(AutoStartState.Enabled, status.State);
        Assert.Equal($"\"{Exe}\" --autostart", registry.Entries[AutoStartService.EntryName]);
        Assert.Equal(2, registry.Entries.Count); Assert.Equal(@"""C:\Outro\outro.exe"" --tray", registry.Entries["Outro aplicativo"]);
    }
    // 4 — removal deletes only our entry (and stray duplicates of our executable).
    [Fact] public void DisableRemovesOwnEntryAndDuplicatesOnly()
    {
        var registry = new FakeRegistry(); var service = new AutoStartService(registry, Exe);
        service.Apply(true); registry.Entries["AHNSF antigo"] = $"\"{Exe}\"";
        Assert.Equal(AutoStartState.Duplicated, service.Inspect(true).State);
        Assert.Equal(AutoStartState.Enabled, service.Apply(true).State); Assert.False(registry.Entries.ContainsKey("AHNSF antigo"));
        Assert.Equal(AutoStartState.Disabled, service.Apply(false).State);
        Assert.Equal(["Outro aplicativo"], registry.Entries.Keys);
    }
    // 5 — commands with spaces are recognised quoted and unquoted; other paths are not.
    [Theory]
    [InlineData(@"""C:\Program Files\AHNSF Music\AHNSF.Music.Desktop.exe"" --autostart", true)]
    [InlineData(@"C:\Program Files\AHNSF Music\AHNSF.Music.Desktop.exe --autostart", true)]
    [InlineData(@"""c:\program files\ahnsf music\ahnsf.music.desktop.exe""", true)]
    [InlineData(@"""C:\Program Files\AHNSF Music 2\AHNSF.Music.Desktop.exe"" --autostart", false)]
    [InlineData(@"C:\Program", false)]
    public void PathsWithSpacesAreParsed(string command, bool expected) => Assert.Equal(expected, AutoStartService.PointsTo(command, Exe));
    // 6 — external removal, relocation and Task Manager disabling are detected; reapplying repairs what is ours to repair.
    [Fact] public void ExternalChangesAreDetectedAndRepairable()
    {
        var registry = new FakeRegistry(); var service = new AutoStartService(registry, Exe);
        service.Apply(true); registry.Entries.Remove(AutoStartService.EntryName);
        Assert.Equal(AutoStartState.Missing, service.Inspect(true).State);
        registry.Entries[AutoStartService.EntryName] = @"""D:\Antigo\AHNSF.Music.Desktop.exe"" --autostart";
        var moved = service.Inspect(true); Assert.Equal(AutoStartState.PointsElsewhere, moved.State); Assert.True(moved.NeedsAttention);
        Assert.Equal(AutoStartState.Enabled, service.Apply(true).State);
        registry.Approved[AutoStartService.EntryName] = false;
        Assert.Equal(AutoStartState.DisabledByWindows, service.Inspect(true).State);
        Assert.Equal(AutoStartState.PresentWhileDisabled, service.Inspect(false).State);
    }
    [Theory] [InlineData(null)] [InlineData(@"AHNSF.Music.Desktop.exe")] [InlineData(@"C:\Program Files\dotnet\dotnet.exe")] [InlineData(@"C:\App\AHNSF.Music.Desktop.dll")]
    public void UnsupportedExecutableIsNeverRegistered(string? path)
    {
        var registry = new FakeRegistry(); var service = new AutoStartService(registry, path);
        Assert.Equal(AutoStartState.Unsupported, service.Apply(true).State); Assert.Equal(0, registry.Writes);
    }
    [Fact] public void RegistryFailureIsReportedNotThrown()
    {
        var registry = new FakeRegistry { Fail = true }; var service = new AutoStartService(registry, Exe);
        var status = service.Apply(true); Assert.Equal(AutoStartState.Error, status.State); Assert.IsType<UnauthorizedAccessException>(status.Error);
        Assert.Equal(AutoStartState.Error, service.Inspect(true).State);
    }
    [Fact] public void AutoStartArgumentIsRecognised()
    { Assert.True(AutoStartService.IsAutoStartLaunch(["--AUTOSTART"])); Assert.False(AutoStartService.IsAutoStartLaunch([])); Assert.False(AutoStartService.IsAutoStartLaunch(["--autostart-x"])); }

    // 7, 12 — start hidden only for Windows auto-start with the option on and a working tray.
    [Theory]
    [InlineData(true, true, true, true)] [InlineData(false, true, true, false)] [InlineData(true, false, true, false)] [InlineData(true, true, false, false)]
    public void StartMinimizedOnlyForAutoStartWithTray(bool autoLaunch, bool minimized, bool tray, bool hidden) =>
        Assert.Equal(hidden, ShellPolicy.StartHidden(autoLaunch, new AppSettings(StartMinimized: minimized), tray));
    // 11, 12 — closing never hides the window without a tray; first close explains the tray; exit with music asks first.
    [Theory]
    [InlineData(CloseBehavior.MinimizeToTray, true, true, false, false, CloseDecision.HideToTray)]
    [InlineData(CloseBehavior.MinimizeToTray, false, true, false, false, CloseDecision.ShowFirstCloseNotice)]
    [InlineData(CloseBehavior.MinimizeToTray, true, false, false, true, CloseDecision.ConfirmExit)]
    [InlineData(CloseBehavior.MinimizeToTray, true, false, false, false, CloseDecision.Exit)]
    [InlineData(CloseBehavior.Exit, true, true, false, true, CloseDecision.ConfirmExit)]
    [InlineData(CloseBehavior.Exit, true, true, false, false, CloseDecision.Exit)]
    [InlineData(CloseBehavior.MinimizeToTray, true, true, true, true, CloseDecision.Exit)]
    public void ClosePolicy(CloseBehavior behavior, bool noticeShown, bool tray, bool shutdown, bool atRisk, CloseDecision expected) =>
        Assert.Equal(expected, ShellPolicy.OnClose(new AppSettings(CloseBehavior: behavior, CloseNoticeShown: noticeShown), tray, shutdown, atRisk));

    // 13, 14 — single instance, activation request, invalid payloads ignored, abandoned mutex recovered.
    private static (string Mutex, string Pipe) Names() { var id = Guid.NewGuid().ToString("N"); return ($@"Local\AHNSF.Test.{id}", $"AHNSF.Test.{id}"); }
    private static SingleInstance? AcquireOnOtherThread(string mutex, string pipe)
    { SingleInstance? result = null; var thread = new Thread(() => result = SingleInstance.TryAcquire(mutex, pipe)); thread.Start(); thread.Join(); return result; }
    [Fact] public void SecondInstanceIsRefused()
    {
        var (mutex, pipe) = Names();
        using var first = SingleInstance.TryAcquire(mutex, pipe); Assert.NotNull(first); Assert.False(first!.RecoveredAbandoned);
        Assert.Null(AcquireOnOtherThread(mutex, pipe));
    }
    [Fact] public async Task SecondLaunchRestoresFirstAndIgnoresOtherCommands()
    {
        var (mutex, pipe) = Names();
        using var first = SingleInstance.TryAcquire(mutex, pipe)!;
        var activations = 0; var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Listen(() => { Interlocked.Increment(ref activations); activated.TrySetResult(); });
        await using (var rogue = new NamedPipeClientStream(".", pipe, PipeDirection.Out))
        { await rogue.ConnectAsync(3000); await rogue.WriteAsync(Encoding.ASCII.GetBytes("PLAY")); }
        await Task.Delay(300); Assert.Equal(0, Volatile.Read(ref activations));
        Assert.True(await Task.Run(() => SingleInstance.SignalExisting(pipe, TimeSpan.FromSeconds(3))));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, Volatile.Read(ref activations));
    }
    [Fact] public void SignalWithoutRunningInstanceReturnsFalse() => Assert.False(SingleInstance.SignalExisting(Names().Pipe, TimeSpan.FromMilliseconds(200)));
    [Fact] public void AbandonedMutexIsRecovered()
    {
        var (mutex, pipe) = Names(); Mutex? crashed = null;
        var thread = new Thread(() => { crashed = new Mutex(false, mutex); crashed.WaitOne(); }); thread.Start(); thread.Join(); // owner thread ends without releasing
        try
        {
            using var recovered = AcquireOnOtherThread(mutex, pipe);
            Assert.NotNull(recovered); Assert.True(recovered!.RecoveredAbandoned);
        }
        finally { crashed?.Dispose(); }
    }

    // Recovery and power events, driven through the real ScheduleCoordinator with a controllable clock and audio device.
    internal sealed class Clock : IScheduleClock
    { public DateTimeOffset UtcNow { get; set; } = At("2026-10-05T12:00"); public TimeZoneInfo TimeZone => TimeZoneInfo.Utc; }
    internal sealed class FlakyAudio : IAudioEngine
    {
        public event EventHandler<AudioEndedEventArgs>? Ended;
        public PlayerState State { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(1);
        public float Volume { get; set; } = .22f;
        public int Loads { get; private set; }
        public Func<Exception>? LoadFailure { get; set; }
        public void Load(string path) { Loads++; if (LoadFailure is { } failure) throw failure(); Position = TimeSpan.Zero; }
        public void Play() => State = PlayerState.Playing;
        public void Pause() => State = PlayerState.Paused;
        public void Stop() => State = PlayerState.Stopped;
        public void End(Exception? error = null) { State = PlayerState.Stopped; Ended?.Invoke(this, new(error)); }
        public void Dispose() { }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public LibraryFixture Fixture { get; } = new();
        public SqlitePlaylistService Playlists { get; }
        public SqliteScheduleService Store { get; }
        public FlakyAudio Audio { get; } = new();
        public PlaybackService Player { get; }
        public Clock Clock { get; } = new();
        public ScheduleCoordinator Coordinator { get; }
        public Playlist Playlist { get; private set; } = null!;
        public List<string> Log { get; } = [];
        public Harness()
        {
            Playlists = new(Fixture.Database, Fixture.Repository); Store = new(Fixture.Database, Playlists); Player = new(Audio, Playlists);
            Coordinator = new(Store, Playlists, Player, Clock, new ScheduleEvaluator(), (message, _) => { lock (Log) Log.Add(message); });
        }
        public async Task Setup(bool automatic = true)
        {
            Playlist = await Playlists.CreateAsync("Ambiente");
            Fixture.WriteTrack("a.mp3", "A"); Fixture.WriteTrack("b.mp3", "B");
            await Playlists.AddAsync(Playlist.Id, (await Fixture.StartAsync()).Tracks.Select(t => t.Id).ToArray());
            await Store.SaveAsync(new(Guid.Empty, "Expediente", Playlist.Id, ScheduleDays.All, new(8, 0), new(18, 0), 62, 0, true, default, default));
            if (automatic) await Store.SetModeAsync(OperationMode.Automatic);
        }
        /// <summary>Moves the clock in 5 s ticks, evaluating each tick like the periodic worker does.</summary>
        public async Task Advance(int seconds) { for (var s = 0; s < seconds; s += 5) { Clock.UtcNow += TimeSpan.FromSeconds(Math.Min(5, seconds - s)); await Coordinator.EvaluateAsync(); } }
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); Player.Dispose(); await Fixture.DisposeAsync(); }
    }
    // 15 — transient failure recovers after the first delay and is logged.
    [Fact] public async Task TransientFailureRecoversAfterFirstDelay()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); var loads = h.Audio.Loads;
        h.Audio.End(new IOException("Dispositivo temporariamente indisponível")); await h.Coordinator.EvaluateAsync();
        Assert.Equal(1, h.Coordinator.Recovery.ConsecutiveFailures); Assert.Equal(h.Clock.UtcNow.AddSeconds(5), h.Coordinator.Recovery.NextAttemptUtc);
        Assert.Contains("Tentativa de recuperação 1 de 3", h.Coordinator.Status.Warning);
        h.Clock.UtcNow += TimeSpan.FromSeconds(4); await h.Coordinator.EvaluateAsync(); Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State);
        h.Clock.UtcNow += TimeSpan.FromSeconds(1); await h.Coordinator.EvaluateAsync();
        Assert.Equal(PlayerState.Playing, h.Player.State); Assert.Equal(0, h.Coordinator.Recovery.ConsecutiveFailures); Assert.NotNull(h.Coordinator.Recovery.LastRecoveredUtc);
        Assert.Equal(1, h.Coordinator.Recovery.LastEpisodeAttempts); Assert.Empty(h.Coordinator.Status.Warning);
        Assert.Contains(h.Log, l => l.StartsWith("Falha de áudio")); Assert.Contains(h.Log, l => l.StartsWith("Recuperação bem-sucedida"));
    }
    // 16 — media/file failures are permanent: no audio retries until a new transition or intervention.
    [Fact] public async Task PermanentFailureDoesNotRetry()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); var loads = h.Audio.Loads;
        h.Audio.End(new InvalidDataException("MP3 corrompido")); await h.Coordinator.EvaluateAsync();
        Assert.True(h.Coordinator.Recovery.Exhausted); await h.Advance(120);
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.NotEmpty(h.Coordinator.Status.Warning);
        Assert.Equal(FailureKind.Permanent, RecoveryPolicy.Classify(new FileNotFoundException())); Assert.Equal(FailureKind.Transient, RecoveryPolicy.Classify(new InvalidOperationException()));
    }
    // 17, 18 — progressive 5/15/30 s backoff, then exhaustion with no further attempts.
    [Fact] public async Task BackoffIsProgressiveAndLimited()
    {
        await using var h = new Harness(); await h.Setup(); h.Audio.LoadFailure = () => new IOException("Falha ao abrir a saída de áudio");
        var start = h.Clock.UtcNow; var attempts = new List<double>();
        await h.Coordinator.StartAsync(false); attempts.Add(0);
        for (var second = 1; second <= 120; second++)
        {
            var before = h.Audio.Loads; h.Clock.UtcNow = start.AddSeconds(second); await h.Coordinator.EvaluateAsync();
            if (h.Audio.Loads > before) attempts.Add(second);
        }
        Assert.Equal([0, 5, 20, 50], attempts);
        Assert.True(h.Coordinator.Recovery.Exhausted); Assert.Contains("Recuperação esgotada", h.Coordinator.Status.Warning);
        Assert.Contains(h.Log, l => l.StartsWith("Recuperação esgotada"));
        // A valid new action from the operator (activating Automatic again) restarts the policy.
        h.Audio.LoadFailure = null; await h.Coordinator.SetModeAsync(OperationMode.Automatic);
        Assert.Equal(PlayerState.Playing, h.Player.State); Assert.False(h.Coordinator.Recovery.Exhausted);
    }
    // 19, 20 — manual intervention cancels pending recovery and audio never restarts on its own.
    [Fact] public async Task ManualInterventionCancelsRecovery()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false); var loads = h.Audio.Loads;
        h.Audio.End(new IOException("Dispositivo indisponível")); await h.Coordinator.EvaluateAsync(); Assert.True(h.Coordinator.Recovery.IsRecovering);
        h.Coordinator.NotifyManualIntervention(); h.Player.Stop();
        await h.Advance(300);
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.True(h.Coordinator.Status.IsSuspended); Assert.False(h.Coordinator.Recovery.IsRecovering);
        Assert.Contains(h.Log, l => l == "Recuperação cancelada por intervenção manual");
    }
    [Fact] public async Task ManualModeNeverRecoversAutomatically()
    {
        await using var h = new Harness(); await h.Setup(automatic: false); await h.Coordinator.StartAsync(false);
        await h.Player.LoadPlaylistAsync(h.Playlist.Id, true); var loads = h.Audio.Loads;
        h.Audio.End(new IOException("Dispositivo indisponível")); await h.Advance(120);
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.Equal(0, h.Coordinator.Recovery.ConsecutiveFailures);
    }
    // 21 — a recovery due after the window ends never starts music.
    [Fact] public async Task RecoveryNeverRestartsAfterWindowEnds()
    {
        await using var h = new Harness(); await h.Setup(); h.Clock.UtcNow = At("2026-10-05T17:59:55"); await h.Coordinator.StartAsync(false); var loads = h.Audio.Loads;
        h.Audio.End(new IOException("Dispositivo indisponível")); await h.Coordinator.EvaluateAsync();
        await h.Advance(60);
        Assert.Equal(loads, h.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.Null(h.Coordinator.Status.Evaluation.Winner); Assert.Equal(0, h.Coordinator.Recovery.ConsecutiveFailures);
    }
    // 22 — suspended at 12:00, resumed at 13:00 inside the window: playback is recovered immediately.
    [Fact] public async Task ResumeInsideWindowRecoversPlayback()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false);
        h.Audio.End(new IOException("Saída perdida durante a suspensão")); h.Clock.UtcNow = At("2026-10-05T13:00");
        h.Coordinator.NotifyResumed(); await h.Coordinator.EvaluateAsync(); // first evaluation sees the failure
        Assert.Equal(1, h.Coordinator.Recovery.ConsecutiveFailures);
        h.Coordinator.NotifyResumed(); await h.Coordinator.EvaluateAsync(); // resume also clears pending delays
        Assert.Equal(PlayerState.Playing, h.Player.State);
        Assert.Contains(h.Log, l => l.StartsWith("Retomada detectada"));
    }
    // 23 — resumed at 18:00, after closing time: no music.
    [Fact] public async Task ResumeAfterHoursDoesNotStart()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false);
        h.Audio.End(new IOException("Saída perdida durante a suspensão")); h.Clock.UtcNow = At("2026-10-05T18:00");
        h.Coordinator.NotifyResumed(); await h.Coordinator.EvaluateAsync(); await h.Advance(60);
        Assert.Equal(PlayerState.Stopped, h.Player.State); Assert.Null(h.Coordinator.Status.Evaluation.Winner);
    }
    [Fact] public async Task ResumeWithoutPowerEventIsDetectedByTimeGap()
    {
        await using var h = new Harness(); await h.Setup(); await h.Coordinator.StartAsync(false);
        h.Audio.LoadFailure = () => new IOException("Saída indisponível"); h.Audio.End(new IOException("Saída indisponível"));
        await h.Coordinator.EvaluateAsync(); await h.Advance(55); Assert.True(h.Coordinator.Recovery.Exhausted);
        h.Audio.LoadFailure = null; h.Clock.UtcNow += TimeSpan.FromMinutes(40); await h.Coordinator.EvaluateAsync(); await h.Advance(5);
        Assert.Equal(PlayerState.Playing, h.Player.State);
    }

    // View model: tray, preferences, diagnostics, shutdown and modal behaviour.
    private sealed class Scanner : IMusicScanner
    { public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new ScanResult([], [])); }
    private sealed class VmHarness : IAsyncDisposable
    {
        public Harness Inner { get; } = new();
        public SqliteSettingsStore Settings { get; }
        public FakeRegistry Registry { get; } = new();
        public MainViewModel ViewModel { get; private set; } = null!;
        public VmHarness() => Settings = new(Inner.Fixture.Database);
        public async Task<MainViewModel> Start(bool automatic = false, AppSettings? settings = null)
        {
            await Inner.Setup(automatic);
            await Settings.SaveAsync(settings ?? new AppSettings(Inner.Fixture.Music, .44f, Shuffle: true, Repeat: RepeatMode.Track));
            ViewModel = new MainViewModel(Inner.Audio, new Scanner(), Settings, Inner.Fixture.Logger, Inner.Fixture.Service, Inner.Playlists, Inner.Player, Inner.Store, Inner.Coordinator, new AutoStartService(Registry, Exe));
            await ViewModel.InitializeAsync();
            return ViewModel;
        }
        public async ValueTask DisposeAsync() { if (ViewModel is not null) await ViewModel.DisposeAsync(); await Inner.DisposeAsync(); }
    }
    // 2, 24 — preferences persist without overwriting volume, shuffle, repeat, folder, playlists or mode.
    [Fact] public async Task PreferencesPersistAndPreserveExistingSettings()
    {
        await using var h = new VmHarness(); var vm = await h.Start();
        Assert.False(vm.AutoStartEnabled); Assert.False(vm.StartMinimized); Assert.True(vm.CloseToTray);
        vm.AutoStartEnabled = true; vm.StartMinimized = true; vm.CloseExits = true;
        Assert.True(h.Registry.Entries.ContainsKey(AutoStartService.EntryName)); Assert.False(vm.CloseToTray);
        await vm.DisposeAsync();
        var saved = await h.Settings.LoadAsync();
        Assert.True(saved.AutoStartEnabled); Assert.True(saved.StartMinimized); Assert.Equal(CloseBehavior.Exit, saved.CloseBehavior);
        Assert.Equal(.44f, saved.Volume); Assert.True(saved.Shuffle); Assert.Equal(RepeatMode.Track, saved.Repeat); Assert.Equal(h.Inner.Fixture.Music, saved.MusicDirectory);
        Assert.Equal(OperationMode.Manual, (await h.Inner.Store.LoadAsync()).Mode); Assert.Single(await h.Inner.Playlists.ListAsync());
    }
    [Fact] public async Task AutoStartFailureRevertsOptionAndExternalRemovalIsShown()
    {
        await using var h = new VmHarness(); h.Registry.Fail = true; var vm = await h.Start();
        vm.AutoStartEnabled = true; Assert.False(vm.AutoStartEnabled); Assert.Equal(AutoStartState.Error, vm.AutoStartStatus.State);
        await using var again = new VmHarness();
        var reopened = await again.Start(settings: new AppSettings(again.Inner.Fixture.Music, AutoStartEnabled: true));
        Assert.True(reopened.AutoStartNeedsAttention); Assert.Equal(AutoStartState.Missing, reopened.AutoStartStatus.State);
        reopened.ReapplyAutoStartCommand.Execute(null); Assert.Equal(AutoStartState.Enabled, reopened.AutoStartStatus.State);
    }
    // 8, 9, 10 — tray actions reuse the single view model/player and never create another one.
    [Fact] public async Task TrayOpensTogglesPlaybackAndRequestsExit()
    {
        await using var h = new VmHarness(); var vm = await h.Start();
        var opened = 0; var exits = 0; var tray = new TrayActions(vm, () => opened++, () => exits++);
        tray.Open(); Assert.Equal(1, opened);
        await h.Inner.Player.LoadPlaylistAsync(h.Inner.Playlist.Id); var queue = h.Inner.Player.Queue.Items.Select(i => i.Id).ToArray();
        tray.TogglePlayback(); Assert.Equal(PlayerState.Playing, h.Inner.Player.State); Assert.Equal(queue, h.Inner.Player.Queue.Items.Select(i => i.Id));
        Assert.Equal("Pausar", vm.TrayPlayPauseLabel); Assert.StartsWith("Reproduzindo", vm.TrayStatus); Assert.True(vm.MusicAtRisk);
        tray.TogglePlayback(); Assert.Equal(PlayerState.Paused, h.Inner.Player.State);
        var before = h.Inner.Player.CurrentTrack; tray.Next(); Assert.NotEqual(before, h.Inner.Player.CurrentTrack);
        tray.Exit(); Assert.Equal(1, exits); Assert.Equal(2, h.Inner.Audio.Loads); // one engine, one track per action
    }
    // 10, 25 — explicit exit: scheduler stopped, audio stopped, geometry saved, idempotent and nothing restarts afterwards.
    [Fact] public async Task ShutdownStopsSchedulerAudioAndSavesOnce()
    {
        await using var h = new VmHarness(); var vm = await h.Start(automatic: true);
        Assert.Equal(PlayerState.Playing, h.Inner.Player.State);
        var first = vm.ShutdownAsync(s => s with { Width = 1234, Height = 700 }); var second = vm.ShutdownAsync();
        Assert.Same(first, second); await first;
        Assert.Equal(PlayerState.Stopped, h.Inner.Audio.State); var loads = h.Inner.Audio.Loads;
        try { await h.Inner.Coordinator.EvaluateAsync(); } catch (OperationCanceledException) { }
        h.Inner.Clock.UtcNow += TimeSpan.FromMinutes(1);
        try { await h.Inner.Coordinator.EvaluateAsync(); } catch (OperationCanceledException) { }
        Assert.Equal(loads, h.Inner.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Inner.Audio.State);
        var saved = await h.Settings.LoadAsync(); Assert.Equal(1234, saved.Width); Assert.Equal(.44f, saved.Volume);
        Assert.Equal(OperationMode.Automatic, (await h.Inner.Store.LoadAsync()).Mode);
    }
    // 26, 27 — modal disables the background (Tab cannot leave it), blocks global commands and Esc/cancel restores everything.
    [Fact] public async Task ModalTrapsInteractionAndCancelRestores()
    {
        await using var h = new VmHarness(); var vm = await h.Start();
        await h.Inner.Player.LoadPlaylistAsync(h.Inner.Playlist.Id); var count = h.Inner.Player.Queue.Items.Count;
        Assert.True(vm.IsBackgroundInteractive);
        vm.RequestClearQueueCommand.Execute(null);
        Assert.True(vm.IsModalOpen); Assert.False(vm.IsBackgroundInteractive);
        vm.NavigateCommand.Execute("Biblioteca"); Assert.Equal("Início", vm.Section);
        vm.TrayPlayPauseCommand.Execute(null); Assert.Equal(PlayerState.Stopped, h.Inner.Player.State);
        vm.CancelDestructiveCommand.Execute(null); // Esc
        Assert.False(vm.IsModalOpen); Assert.True(vm.IsBackgroundInteractive); Assert.Equal(count, h.Inner.Player.Queue.Items.Count);
        vm.NavigateCommand.Execute("Biblioteca"); Assert.Equal("Biblioteca", vm.Section);
    }
    [Fact] public async Task DialogSecondaryActionAndReplacementCancelPrevious()
    {
        await using var h = new VmHarness(); var vm = await h.Start();
        var log = new List<string>();
        vm.RequestDialog("Primeiro", "?", "Ok", () => { log.Add("primeiro"); return Task.CompletedTask; }, cancelled: () => log.Add("primeiro cancelado"));
        vm.RequestDialog("Segundo", "?", "Minimizar", () => { log.Add("confirmar"); return Task.CompletedTask; }, secondaryLabel: "Encerrar", secondary: () => { log.Add("secundária"); return Task.CompletedTask; });
        Assert.Equal("Segundo", vm.DialogTitle); Assert.True(vm.HasDialogSecondary); Assert.False(vm.DialogIsDanger);
        await vm.DialogSecondaryCommand.ExecuteAsync(null); await vm.ConfirmDestructiveCommand.ExecuteAsync(null);
        Assert.Equal(["primeiro cancelado", "secundária"], log); Assert.False(vm.IsModalOpen);
    }
    // Diagnostics expose every requested field and the recovery state.
    [Fact] public async Task DiagnosticsShowOperationalStateAndRecovery()
    {
        await using var h = new VmHarness(); var vm = await h.Start(automatic: true);
        var labels = vm.Diagnostics.Select(d => d.Label).ToArray();
        foreach (var label in new[] { "Player", "Modo operacional", "Agendador", "Playlist ativa", "Próxima transição", "Pasta musical", "Biblioteca", "Mecanismo de áudio", "Último erro relevante", "Última recuperação", "Tentativas recentes de recuperação" })
            Assert.Contains(label, labels);
        h.Inner.Audio.End(new IOException("Dispositivo indisponível")); await h.Inner.Coordinator.EvaluateAsync();
        Assert.StartsWith("Em recuperação", vm.Diagnostics.Single(d => d.Label == "Mecanismo de áudio").Value);
        Assert.Contains("Dispositivo indisponível", vm.Diagnostics.Single(d => d.Label == "Último erro relevante").Value);
        h.Inner.Clock.UtcNow += TimeSpan.FromSeconds(5); await h.Inner.Coordinator.EvaluateAsync();
        Assert.Contains("bem-sucedida", vm.Diagnostics.Single(d => d.Label == "Última recuperação").Value);
    }
    // 22 via the view model: resume reconciles and re-evaluates; Manual never starts audio.
    [Fact] public async Task ViewModelResumeInManualDoesNotStartAudio()
    {
        await using var h = new VmHarness(); var vm = await h.Start();
        vm.OnSystemSuspending(); h.Inner.Clock.UtcNow = At("2026-10-05T13:00"); await vm.OnSystemResumedAsync();
        Assert.Equal(0, h.Inner.Audio.Loads); Assert.Equal(PlayerState.Stopped, h.Inner.Player.State);
        Assert.Contains("retomado", vm.Diagnostics.Single(d => d.Label == "Suspensão/retomada").Value);
    }
}
