using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;
public sealed class OperationalIntegrationTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public LibraryFixture F { get; } = new(); public AudioDeviceTests.RoutedAudio Audio { get; } = new(); public AudioDeviceTests.Discovery Discovery { get; } = new(); public OperationsTests.Clock Clock { get; } = new();
        public OperationalHistoryService History { get; } public PlaybackService Player { get; } public SqlitePlaylistService Playlists { get; } public SqliteScheduleService Store { get; } public AudioDeviceService Devices { get; } public ScheduleCoordinator Coordinator { get; } public Guid RuleId;
        public Harness()
        {
            History=new(new SqliteOperationalHistoryRepository(F.Database,F.Repository));Playlists=new(F.Database,F.Repository);Store=new(F.Database,Playlists);Player=new(Audio,Playlists,history:History);Devices=new(Discovery,Audio,history:History);Coordinator=new(Store,Playlists,Player,Clock,new ScheduleEvaluator(),devices:Devices,history:History);
        }
        public async Task Setup()
        {
            await History.StartAsync(false);var playlist=await Playlists.CreateAsync("Hospital");F.WriteTrack("a.mp3","A");await Playlists.AddAsync(playlist.Id,(await F.StartAsync()).Tracks.Select(t=>t.Id).ToArray());await Store.SaveAsync(new(Guid.Empty,"Expediente",playlist.Id,ScheduleDays.All,new(8,0),new(18,0),62,0,true,default,default));RuleId=(await Store.LoadAsync()).Rules.Single().Id;await Store.SetModeAsync(OperationMode.Automatic);await Devices.StartAsync(AudioDeviceTests.Specific(),false);
        }
        public async Task<IReadOnlyList<OperationalEvent>> Events(OperationalEventType type) { await History.FlushAsync();return (await History.QueryAsync(new(Type:type,PageSize:200))).Events; }
        public async ValueTask DisposeAsync() {await Coordinator.DisposeAsync();await Devices.DisposeAsync();Player.Dispose();await History.DisposeAsync();await F.DisposeAsync();}
    }
    [Fact] public async Task ScheduleTransitionsInterventionAndModesAreObservedWithoutCycles()
    {
        await using var h=new Harness();await h.Setup();await h.Coordinator.StartAsync(false);for(int i=0;i<10;i++)await h.Coordinator.EvaluateAsync();Assert.Single(await h.Events(OperationalEventType.RuleActivated));Assert.Single(await h.Events(OperationalEventType.PlaybackStarted));
        h.Coordinator.NotifyManualIntervention();h.Coordinator.NotifyManualIntervention();Assert.Single(await h.Events(OperationalEventType.AutomationSuspended));Assert.Single(await h.Events(OperationalEventType.ManualIntervention));await h.Coordinator.SetModeAsync(OperationMode.Manual);Assert.Single(await h.Events(OperationalEventType.ManualMode));Assert.Single(await h.Events(OperationalEventType.AutomaticMode));
    }
    [Fact] public async Task RuleWinnerChangeAndWindowEndAreObserved()
    {
        await using var h=new Harness();await h.Setup();await h.Coordinator.StartAsync(false);var rule=(await h.Store.LoadAsync()).Rules.Single();await h.Store.SaveAsync(rule with {Id=Guid.Empty,Name="Prioritária",Priority=10});await h.Coordinator.EvaluateAsync();Assert.Single(await h.Events(OperationalEventType.WinnerChanged));h.Clock.UtcNow=h.Clock.UtcNow.Date.AddHours(19);await h.Coordinator.EvaluateAsync();Assert.Equal(2,(await h.Events(OperationalEventType.RuleEnded)).Count);Assert.Equal(PlayerState.Stopped,h.Audio.State);
    }
    [Fact] public async Task DisconnectReconnectAndWindowsDuplicatesAreObservedOnce()
    {
        await using var h=new Harness();await h.Setup();await h.Coordinator.StartAsync(false);h.Discovery.Devices=[AudioDeviceTests.Speakers(true)];for(int i=0;i<10;i++)await h.Devices.RefreshAsync();await h.Coordinator.EvaluateAsync();Assert.Single(await h.Events(OperationalEventType.DeviceDisconnected));Assert.Single(await h.Events(OperationalEventType.PlaybackError));
        h.Discovery.Devices=[AudioDeviceTests.Usb(),AudioDeviceTests.Speakers()];for(int i=0;i<10;i++)await h.Devices.RefreshAsync();await h.Coordinator.EvaluateAsync();Assert.Single(await h.Events(OperationalEventType.DeviceReconnected));Assert.Equal(PlayerState.Playing,h.Audio.State);Assert.Equal(2,(await h.Events(OperationalEventType.PlaybackStarted)).Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RecoverySuccessAndExhaustionAreStructured(bool exhaust)
    {
        await using var h=new Harness();await h.Setup();h.Audio.FailLoad=true;await h.Coordinator.StartAsync(false);if(!exhaust)h.Audio.FailLoad=false;
        foreach(int delay in new[]{5,15,30}) { for (int elapsed=0; elapsed<delay; elapsed+=5) {h.Clock.UtcNow+=TimeSpan.FromSeconds(5);await h.Coordinator.EvaluateAsync();} if(!exhaust)break;}
        Assert.Equal(exhaust?3:1,(await h.Events(OperationalEventType.RecoveryAttempt)).Count);Assert.Single(await h.Events(exhaust?OperationalEventType.RecoveryExhausted:OperationalEventType.RecoverySucceeded));if(!exhaust)Assert.Equal(OperationalActionOrigin.Recovery,Assert.Single(await h.Events(OperationalEventType.PlaybackStarted)).Origin);
    }
    [Fact] public async Task DeviceSelectionChangeAndFailuresHaveStableIds()
    {
        await using var h=new Harness();await h.Setup();await h.Devices.SelectAsync(new(AudioDeviceSelectionMode.Specific,"internal-id"));Assert.Single(await h.Events(OperationalEventType.OutputSelected));Assert.Equal("internal-id",Assert.Single(await h.Events(OperationalEventType.OutputChanged)).DeviceId);h.Discovery.Fail=true;for(int i=0;i<5;i++)await h.Devices.RefreshAsync();Assert.Single(await h.Events(OperationalEventType.DeviceOpenFailed));
    }
    [Fact] public async Task LibraryObservesSignificantChangesButNotEveryFileOrNoOpCycle()
    {
        await using var h=new Harness();await h.History.StartAsync(false);h.F.WriteTrack("a.mp3");h.F.WriteTrack("b.mp3");
        using var logger=new Serilog.LoggerConfiguration().CreateLogger();var library=new LibraryService(h.F.Repository,new TrackMetadataReader(new AHNSF.Music.Audio.Mp3DurationReader(),h.F.Artwork,logger),h.F.Artwork,logger,new(){EnableAutomaticSync=false,UseFileSystemWatcher=false,StabilityDelay=TimeSpan.Zero},h.History);
        await using(library) {await library.SetDirectoryAsync(h.F.Music,default);await library.ReconcileAsync(false,default);await library.ReconcileAsync(false,default);Assert.Single(await h.Events(OperationalEventType.IndexingStarted));Assert.Single(await h.Events(OperationalEventType.IndexingCompleted));}
    }
}
