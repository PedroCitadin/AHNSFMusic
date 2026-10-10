using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class AudioDeviceTests
{
    [Fact] public void MissingFriendlyNameDoesNotHideTheEndpoint()
    {
        var device = AHNSF.Music.Audio.WindowsAudioDeviceDiscovery.ReadEndpoint("stable", () => throw new System.Runtime.InteropServices.COMException("Property store removed", unchecked((int)0xE000020B)), false, false);
        Assert.Equal("stable", device.Id); Assert.False(device.IsAvailable); Assert.Contains("sem nome", device.Name);
    }
    [Fact] public async Task InvalidPersistedModeCannotRouteToAnAlternative()
    {
        var o = new RoutedAudio(); await using var s = new AudioDeviceService(new Discovery(), o); await s.StartAsync(new((AudioDeviceSelectionMode)99, "usb-stable-id"), false);
        Assert.True(s.Status.RequiresIntervention); Assert.Equal(0, o.Switches);
        await Assert.ThrowsAsync<ArgumentException>(() => s.SelectAsync(new((AudioDeviceSelectionMode)99, "internal-id")));
    }
    internal static AudioOutputDevice Usb(bool available = true, bool isDefault = true, string id = "usb-stable-id") => new(id, "Mesa USB", available, isDefault);
    internal static AudioOutputDevice Speakers(bool isDefault = false) => new("internal-id", "Alto-falantes", true, isDefault);
    internal static AudioDeviceSelection Specific(string id = "usb-stable-id") => new(AudioDeviceSelectionMode.Specific, id, "Mesa USB");
    internal sealed class Discovery : IAudioDeviceDiscovery
    {
        public IReadOnlyList<AudioOutputDevice> Devices { get; set; } = [Usb(), Speakers()];
        public event EventHandler? Changed;
        public bool Fail { get; set; }
        public bool Disposed { get; private set; }
        public int Reads;
        public IReadOnlyList<AudioOutputDevice> Enumerate() { Interlocked.Increment(ref Reads); if (Fail) throw new IOException("Descoberta falhou"); return Devices.ToArray(); }
        public void Signal() => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Disposed = true;
    }
    internal sealed class RoutedAudio : IAudioEngine, IAudioOutputController
    {
        public event EventHandler<AudioEndedEventArgs>? Ended;
        public PlayerState State { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(1);
        public float Volume { get; set; } = .22f;
        public string? EffectiveDeviceId => State == PlayerState.Stopped ? null : Id;
        public string? Id { get; private set; }
        public bool Blocked { get; private set; }
        public bool FailSwitch { get; set; }
        public bool FailLoad { get; set; }
        public int Switches { get; private set; }
        public int Loads { get; private set; }
        public void SwitchOutput(string id) { Switches++; if (FailSwitch) throw new IOException("Falha do driver"); Id = id; Blocked = false; }
        public void BlockOutput(string reason) { var had = State != PlayerState.Stopped; Blocked = true; State = PlayerState.Stopped; if (had) Ended?.Invoke(this, new(new AudioDeviceUnavailableException(reason))); }
        public void Load(string path) { Loads++; if (Blocked || FailLoad) throw new AudioDeviceUnavailableException("USB indisponível"); Position = TimeSpan.Zero; }
        public void Play() { if (Blocked) throw new AudioDeviceUnavailableException("USB indisponível"); State = PlayerState.Playing; }
        public void Pause() => State = PlayerState.Paused;
        public void Stop() => State = PlayerState.Stopped;
        public void Dispose() => Stop();
    }
    [Fact] public async Task DiscoveryListsStableIdsAvailabilityAndDefault()
    {
        var discovery = new Discovery { Devices = [Speakers(), Usb(), new("off-id", "Desabilitado", false, false)] };
        var output = new RoutedAudio(); await using var service = new AudioDeviceService(discovery, output);
        await service.StartAsync(new(), false);
        Assert.Equal(3, service.Status.Devices.Count); Assert.Equal("usb-stable-id", service.Status.DefaultDevice!.Id);
        Assert.Contains(service.Status.Devices, d => !d.IsAvailable && d.Label.Contains("indisponível"));
        Assert.Contains("padrão", service.Status.DefaultDevice.Label); Assert.True(service.Status.CanPlay);
    }
    [Theory] [InlineData(AudioDeviceSelectionMode.Default, "usb-stable-id")] [InlineData(AudioDeviceSelectionMode.Specific, "internal-id")]
    public async Task SelectionUsesChosenEndpoint(AudioDeviceSelectionMode mode, string id)
    {
        var output = new RoutedAudio(); await using var service = new AudioDeviceService(new Discovery(), output);
        await service.StartAsync(new(mode, id), false); Assert.Equal(id, output.Id); Assert.True(service.Status.CanPlay);
    }
    [Fact] public async Task ReorderedEnumerationNeverChangesSpecificIdentity()
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false);
        d.Devices = d.Devices.Reverse().ToArray(); await s.RefreshAsync(); Assert.Equal("usb-stable-id", o.Id); Assert.Equal(1, o.Switches);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task MissingOrDisabledSelectionDoesNotFallBack(bool removed)
    {
        var d = new Discovery { Devices = removed ? [Speakers(true)] : [Usb(false), Speakers(true)] }; var o = new RoutedAudio();
        await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false);
        Assert.False(s.Status.CanPlay); Assert.Equal(0, o.Switches); Assert.True(o.Blocked); Assert.Equal("usb-stable-id", s.Status.Selection.DeviceId);
    }
    [Fact] public async Task DisconnectDuringPlaybackStopsAndKeepsSelection()
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false); o.Load("a"); o.Play();
        d.Devices = [Speakers(true)]; await s.RefreshAsync(); Assert.Equal(PlayerState.Stopped, o.State); Assert.True(o.Blocked);
        Assert.NotNull(s.Status.LastDisconnectedUtc); Assert.Equal("usb-stable-id", s.Status.Selection.DeviceId); Assert.Equal(1, o.Switches);
    }
    [Fact] public async Task SameIdReconnectRestoresAvailabilityWithoutPlaying()
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false);
        d.Devices = [Speakers(true)]; await s.RefreshAsync(); d.Devices = [Usb(), Speakers()]; await s.RefreshAsync();
        Assert.True(s.Status.CanPlay); Assert.NotNull(s.Status.LastReconnectedUtc); Assert.Equal("usb-stable-id", o.Id); Assert.Equal(PlayerState.Stopped, o.State);
    }
    [Fact] public async Task SameNameDifferentIdNeverRecovers()
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false);
        d.Devices = [Speakers(true)]; await s.RefreshAsync(); d.Devices = [Usb(id: "different-id"), Speakers()]; await s.RefreshAsync();
        Assert.False(s.Status.CanPlay); Assert.Null(s.Status.LastReconnectedUtc); Assert.Equal(1, o.Switches);
    }
    [Theory] [InlineData(AudioDeviceSelectionMode.Default)] [InlineData(AudioDeviceSelectionMode.Specific)]
    public async Task WindowsDefaultChangeHasConservativePolicy(AudioDeviceSelectionMode mode)
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(new(mode, "usb-stable-id"), false); o.Play();
        d.Devices = [Usb(isDefault: false), Speakers(true)]; await s.RefreshAsync();
        Assert.Equal("usb-stable-id", o.Id);
        if (mode == AudioDeviceSelectionMode.Default) { Assert.True(s.Status.RequiresIntervention); Assert.Equal(PlayerState.Stopped, o.State); await s.SelectAsync(new()); Assert.Equal("internal-id", o.Id); Assert.True(s.Status.CanPlay); }
        else { Assert.False(s.Status.RequiresIntervention); Assert.Equal(PlayerState.Playing, o.State); }
    }
    [Fact] public async Task InvalidManualSelectionPreservesPreviousOutput()
    {
        var o = new RoutedAudio(); await using var s = new AudioDeviceService(new Discovery(), o); await s.StartAsync(Specific(), false); o.Play();
        await Assert.ThrowsAsync<AudioDeviceUnavailableException>(() => s.SelectAsync(Specific("absent")));
        Assert.Equal("usb-stable-id", o.Id); Assert.Equal(PlayerState.Playing, o.State); Assert.Equal(Specific(), s.Status.Selection);
    }
    [Fact] public async Task SwitchFailureBlocksAndRetainsOperatorIntent()
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false); o.Play(); o.FailSwitch = true;
        Assert.False(await s.SelectAsync(new(AudioDeviceSelectionMode.Specific, "internal-id")));
        Assert.Equal("internal-id", s.Status.Selection.DeviceId); Assert.True(s.Status.RequiresIntervention); Assert.Equal(PlayerState.Stopped, o.State); Assert.NotNull(s.Status.LastFailureUtc);
        var attempts = o.Switches; await s.RefreshAsync(); Assert.Equal(attempts, o.Switches);
    }
    [Fact] public async Task DiscoveryFailureBlocksAndReportsError()
    {
        var d = new Discovery { Fail = true }; var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false);
        Assert.True(s.Status.RequiresIntervention); Assert.True(o.Blocked); Assert.NotNull(s.Status.LastFailure);
    }
    [Fact] public async Task DuplicateSnapshotsDoNotSwitchOrPublish()
    {
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o); await s.StartAsync(Specific(), false);
        var changes = 0; s.Changed += (_, _) => changes++;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => s.RefreshAsync())); Assert.Equal(0, changes); Assert.Equal(1, o.Switches);
    }
    [Fact] public async Task CancelledSelectionDoesNotTouchResources()
    {
        var o = new RoutedAudio(); await using var s = new AudioDeviceService(new Discovery(), o); await s.StartAsync(Specific(), false);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.SelectAsync(new(), cancel.Token)); Assert.Equal(1, o.Switches);
    }
    [Fact] public async Task MonitorHandlesNotificationsAndPeriodicReconciliationAndDisposes()
    {
        var d = new Discovery(); var o = new RoutedAudio(); var s = new AudioDeviceService(d, o, reconcileInterval: TimeSpan.FromMilliseconds(30)); await s.StartAsync(Specific());
        d.Devices = [Speakers(true)]; for (var i = 0; i < 100; i++) d.Signal();
        await Wait(() => !s.Status.CanPlay); var switches = o.Switches;
        d.Devices = [Usb(), Speakers()]; await Wait(() => s.Status.CanPlay); Assert.Equal(switches + 1, o.Switches);
        await s.DisposeAsync(); await s.DisposeAsync(); Assert.True(d.Disposed); Assert.True(o.Blocked); var reads = d.Reads; d.Signal(); await Task.Delay(60); Assert.Equal(reads, d.Reads);
    }
    [Fact] public async Task ConcurrentDisposeWaitsForTheSameCleanup()
    {
        var d = new Discovery(); var o = new RoutedAudio(); var s = new AudioDeviceService(d, o); await s.StartAsync(Specific());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.DisposeAsync().AsTask())); Assert.True(d.Disposed); Assert.True(o.Blocked);
    }
    private static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!condition()) await Task.Delay(10, timeout.Token); }
    [Fact] public async Task SqlitePersistsSelectionAndAllExistingPreferences()
    {
        await using var f = new LibraryFixture(); var store = new SqliteSettingsStore(f.Database);
        var expected = new AppSettings(f.Music, .22f, 1366, 728, 1, 2, true, RepeatMode.Track, true, true, CloseBehavior.Exit, true, Specific());
        await store.SaveAsync(expected); Assert.Equal(expected, await new SqliteSettingsStore(f.Database).LoadAsync());
        await store.SaveAsync(expected with { AudioDevice = new() }); Assert.Equal(expected with { AudioDevice = new() }, await store.LoadAsync());
    }
    [Fact] public async Task OldSettingsJsonDefaultsToWindowsDefaultWithoutLosingFields()
    {
        await using var f = new LibraryFixture(); var store = new SqliteSettingsStore(f.Database); await store.SaveAsync(new(Volume: .44f));
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.Database}"); await db.OpenAsync(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Settings SET Json='{" + "\"Volume\":0.22,\"AutoStartEnabled\":true,\"CloseNoticeShown\":true}" + "'"; await cmd.ExecuteNonQueryAsync();
        var settings = await store.LoadAsync(); Assert.Null(settings.AudioDevice); Assert.Equal(.22f, settings.Volume); Assert.True(settings.AutoStartEnabled); Assert.True(settings.CloseNoticeShown);
    }
    private sealed class Scanner : IMusicScanner { public Task<ScanResult> ScanAsync(string directory, CancellationToken token) => Task.FromResult(new ScanResult([], [])); }
    [Fact] public async Task ViewModelShowsMissingDeviceAndPersistsExplicitApply()
    {
        await using var f = new LibraryFixture(); var store = new SqliteSettingsStore(f.Database); await store.SaveAsync(new(AudioDevice: Specific("absent")));
        var d = new Discovery(); var o = new RoutedAudio(); await using var s = new AudioDeviceService(d, o);
        using var player = new PlaybackService(o); await using var vm = new MainViewModel(o, new Scanner(), store, f.Logger, playback: player, audioDevices: s);
        await vm.InitializeAsync(); Assert.True(vm.AudioDeviceNeedsAttention); Assert.Contains(vm.AudioOutputs, device => device.Id == "absent" && !device.IsAvailable); Assert.True(vm.HasOperationalAlert);
        vm.SelectedAudioOutput = vm.AudioOutputs.First(device => device.Id == "usb-stable-id"); vm.UseDefaultAudioOutput = false; await vm.ApplyAudioOutputCommand.ExecuteAsync(null);
        Assert.Equal(Specific(), (await store.LoadAsync()).AudioDevice); Assert.False(vm.AudioDeviceNeedsAttention);
        Assert.Contains(vm.Diagnostics, row => row.Label == "Dispositivo configurado" && row.Value == "Mesa USB"); Assert.Equal(PlayerState.Stopped, o.State);
    }
}
