using System.Diagnostics;
using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using AHNSF.Music.Desktop;
using Microsoft.Data.Sqlite;
using Serilog;
using Xunit;
using Xunit.Abstractions;

namespace AHNSF.Music.Tests;
public sealed class OperationalHistoryTests(ITestOutputHelper output)
{
    private sealed class HistoryClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero); private long ticks;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => 1000;
        public void Advance(double seconds) { now += TimeSpan.FromSeconds(seconds); ticks += (long)(seconds * 1000); }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public LibraryFixture Fixture { get; } = new();
        public SqliteOperationalHistoryRepository Repo { get; }
        public OperationalHistoryService History { get; }
        public HistoryClock Clock { get; } = new();
        public Harness() { Repo = new(Fixture.Database, Fixture.Repository); History = new(Repo, clock: Clock); }
        public async Task Start() { await History.StartAsync(false); await History.FlushAsync(); }
        public async Task<IReadOnlyList<OperationalEvent>> Events(OperationalEventType? type = null) { await History.FlushAsync(); return (await History.QueryAsync(new(Type: type, PageSize: 200))).Events; }
        public async ValueTask DisposeAsync() { await History.DisposeAsync(); await Fixture.DisposeAsync(); }
    }
    private static OperationalEvent Event(int index = 0, OperationalEventType type = OperationalEventType.OutputSelected, OperationalEventSeverity severity = OperationalEventSeverity.Information) => new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddSeconds(index), type, severity, "Teste", "Evento " + index, Guid.NewGuid());
    private static Track Track(string name = "Música") => new(name + ".mp3", name, "Artista", TimeSpan.FromMinutes(1)) { Id = Guid.NewGuid() };
    [Fact] public async Task MigrationIsIdempotentAndPreservesExistingTables()
    {
        await using var h = new Harness(); var playlists = new SqlitePlaylistService(h.Fixture.Database, h.Fixture.Repository); var playlist = await playlists.CreateAsync("Existente"); var store = new SqliteSettingsStore(h.Fixture.Database); await store.SaveAsync(new(Volume: .27f));
        await h.Repo.InitializeAsync(default); var reopened = new SqliteOperationalHistoryRepository(h.Fixture.Database, h.Fixture.Repository); await reopened.InitializeAsync(default);
        Assert.Equal(.27f, (await store.LoadAsync()).Volume); Assert.NotNull(await playlists.GetAsync(playlist.Id));
        await using var db = new SqliteConnection("Data Source=" + h.Fixture.Database); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM LibraryMigrations WHERE Version=4"; Assert.Equal(1L, command.ExecuteScalar());
    }
    [Fact] public async Task EventsPersistAcrossReopenAndOrderByUtc()
    {
        await using var h = new Harness(); var a = Event(2); var b = Event(1); await h.Repo.AppendAsync([new(a), new(b), new(a)], default);
        var reopened = new SqliteOperationalHistoryRepository(h.Fixture.Database, h.Fixture.Repository); var page = await reopened.QueryAsync(new(), default); Assert.Equal(2, page.Total); Assert.Equal(a.Id, page.Events[0].Id); Assert.Equal(b.Id, page.Events[1].Id);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task FiltersSelectOnlyMatchingEvents(int kind)
    {
        await using var h = new Harness(); var a = Event(1); var b = Event(2, OperationalEventType.PlaybackError, OperationalEventSeverity.Error);
        await h.Repo.AppendAsync([new(a), new(b)], default);
        var query = kind switch { 0 => new OperationalHistoryQuery(FromUtc: b.TimestampUtc, UntilUtc: b.TimestampUtc.AddSeconds(1)), 1 => new(Type: OperationalEventType.PlaybackError), 2 => new(Severity: OperationalEventSeverity.Error), _ => new(Search: "Evento 2") };
        Assert.Equal(b.Id, Assert.Single((await h.Repo.QueryAsync(query, default)).Events).Id);
    }
    [Fact] public async Task SearchEscapesSqlWildcardsAndInjection()
    {
        await using var h = new Harness(); await h.Repo.AppendAsync([new(Event() with { Description = "100%_ concluído" })], default);
        Assert.Single((await h.Repo.QueryAsync(new(Search: "%_"), default)).Events); Assert.Empty((await h.Repo.QueryAsync(new(Search: "' OR 1=1 --"), default)).Events);
    }
    [Fact] public async Task PaginationHasNoOverlapForEqualTimestamps()
    {
        await using var h = new Harness(); await h.Repo.AppendAsync(Enumerable.Range(0, 125).Select(_ => new OperationalWrite(Event())).ToArray(), default);
        var a = await h.Repo.QueryAsync(new(PageSize: 50), default); var b = await h.Repo.QueryAsync(new(Page: 1), default); var c = await h.Repo.QueryAsync(new(Page: 2), default); Assert.Equal(125, a.Total); Assert.Equal(125, a.Events.Concat(b.Events).Concat(c.Events).Select(e => e.Id).Distinct().Count());
    }
    [Fact] public async Task ConcurrentWritesRemainComplete()
    {
        await using var h = new Harness(); await h.Repo.InitializeAsync(default); await Task.WhenAll(Enumerable.Range(0, 8).Select(i => h.Repo.AppendAsync(Enumerable.Range(0, 40).Select(j => new OperationalWrite(Event(i * 40 + j))).ToArray(), default)));
        Assert.Equal(320, (await h.Repo.QueryAsync(new(), default)).Total);
    }
    [Fact] public async Task CancelledQueryAndWriteDoNotMutateDatabase()
    {
        await using var h = new Harness(); using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Repo.QueryAsync(new(), cts.Token)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Repo.AppendAsync([new(Event())], cts.Token)); Assert.Equal(0, (await h.Repo.QueryAsync(new(), default)).Total);
    }
    [Theory] [InlineData(30)] [InlineData(90)] [InlineData(180)] [InlineData(365)]
    public async Task RetentionDeletesOnlyOlderEventsIncrementally(int days)
    {
        await using var h = new Harness(); var cutoff = h.Clock.GetUtcNow().AddDays(-days); var old = Event() with { TimestampUtc = cutoff.AddDays(-1) }; var keep = Event() with { TimestampUtc = cutoff }; await h.Repo.AppendAsync(Enumerable.Range(0, 510).Select(_ => new OperationalWrite(old with { Id = Guid.NewGuid() })).Append(new(keep)).ToArray(), default);
        Assert.Equal(500, await h.Repo.RetainAsync(cutoff, default)); Assert.Equal(11, (await h.Repo.QueryAsync(new(), default)).Total); Assert.Equal(10, await h.Repo.RetainAsync(cutoff, default)); Assert.Equal(keep.Id, Assert.Single((await h.Repo.QueryAsync(new(), default)).Events).Id);
    }
    [Fact] public async Task NormalSessionIncludesDurationFinalStateAndErrors()
    {
        await using var h = new Harness(); await h.Start(); h.History.Record(OperationalEventType.PlaybackError, "Player", "Falha", OperationalEventSeverity.Error); h.Clock.Advance(42); await h.History.EndAsync("Parado"); var session = Assert.Single(await h.History.SessionsAsync()); Assert.NotNull(session.EndedUtc); Assert.Equal(42, session.DurationSeconds); Assert.Equal("Parado", session.FinalState); Assert.Equal(1, session.RelevantErrors);
    }
    [Fact] public async Task UnconfirmedSessionAndPlaybackNeverInventCrashOrDuration()
    {
        await using var h = new Harness(); var id = Guid.NewGuid(); var p = new PlaybackPeriod(Guid.NewGuid(), id, null, null, null, null, "A", "Biblioteca", OperationalActionOrigin.Manual, h.Clock.GetUtcNow(), null, null, "Em reprodução");
        await h.Repo.AppendAsync([new(Event() with { SessionId = id }, new(id, h.Clock.GetUtcNow(), null, null, "Em execução", 0), p)], default);
        await h.Start(); var session = (await h.History.SessionsAsync()).Single(s => s.Id == id); Assert.Equal("Sem encerramento confirmado", session.FinalState); Assert.Null(session.EndedUtc); Assert.Null(session.DurationSeconds);
        var period = Assert.Single(await h.History.PlaybackAsync(new())); Assert.False(period.Confirmed); Assert.Null(period.EffectiveSeconds);
        Assert.DoesNotContain("crash", Assert.Single(await h.Events(OperationalEventType.PreviousSessionUnconfirmed)).Description, StringComparison.OrdinalIgnoreCase);
        await h.Repo.MarkUnconfirmedAsync(h.History.SessionId, default); Assert.Single(await h.Events(OperationalEventType.PreviousSessionUnconfirmed));
    }
    [Fact] public async Task PlaybackPauseResumeExcludesPausedTimeAndDeduplicatesPause()
    {
        await using var h = new Harness(); await h.Start(); var track = Track(); h.History.Playback(OperationalEventType.PlaybackStarted, track, new(null, "Biblioteca"), OperationalActionOrigin.Manual, "Início"); h.Clock.Advance(10);
        h.History.Playback(OperationalEventType.PlaybackPaused, track, null, OperationalActionOrigin.Manual, "Pausa"); h.History.Playback(OperationalEventType.PlaybackPaused, track, null, OperationalActionOrigin.Manual, "Pausa repetida"); h.Clock.Advance(60);
        h.History.Playback(OperationalEventType.PlaybackResumed, track, null, OperationalActionOrigin.Manual, "Retomada"); h.Clock.Advance(5); h.History.Playback(OperationalEventType.PlaybackStopped, track, null, OperationalActionOrigin.Manual, "Parar"); await h.History.FlushAsync();
        var p = Assert.Single(await h.History.PlaybackAsync(new())); Assert.Equal(15, p.EffectiveSeconds); Assert.Equal(75, (p.EndedUtc - p.StartedUtc)?.TotalSeconds); Assert.Single(await h.Events(OperationalEventType.PlaybackPaused));
    }
    private sealed class EndableAudio : IAudioEngine
    {
        public event EventHandler<AudioEndedEventArgs>? Ended; public PlayerState State { get; private set; } public TimeSpan Position { get; set; } public TimeSpan Duration => TimeSpan.FromMinutes(1); public float Volume { get; set; }
        public void Load(string path) { } public void Play() => State = PlayerState.Playing; public void Pause() => State = PlayerState.Paused; public void Stop() => State = PlayerState.Stopped; public void Dispose() => Stop(); public void End() { State = PlayerState.Stopped; Ended?.Invoke(this, new()); }
    }
    [Fact] public async Task ActualPlayerPublishesAdvanceStopAndIndependentPeriods()
    {
        await using var h = new Harness(); await h.Start(); var audio = new EndableAudio(); using var player = new PlaybackService(audio, history: h.History); var a = Track("A"); var b = Track("B"); player.PlayLibrary(a, [a, b]); h.Clock.Advance(4); audio.End(); h.Clock.Advance(6); audio.End();
        Assert.Equal(PlayerState.Stopped, player.State); Assert.Single(await h.Events(OperationalEventType.AutomaticAdvance)); var periods = await h.History.PlaybackAsync(new()); Assert.Equal(2, periods.Count); Assert.Equal(10, periods.Sum(p => p.EffectiveSeconds)); Assert.Equal(2, (await h.Events(OperationalEventType.PlaybackStarted)).Count);
    }
    [Fact] public async Task CsvHasBomQuotesExcelSafetyFiltersAndObservedDurations()
    {
        await using var h = new Harness(); await h.Start(); var track = Track("=1+1;\"Título\""); h.History.Playback(OperationalEventType.PlaybackStarted, track, null, OperationalActionOrigin.Manual, "=cmd;\n\"texto\""); h.Clock.Advance(2); h.History.Playback(OperationalEventType.PlaybackStopped, track, null, OperationalActionOrigin.Manual, "Parar"); await h.History.FlushAsync(); h.Clock.Advance(1);
        var path = Path.Combine(h.Fixture.Base, "export.csv"); await h.History.ExportAsync(path, new(Type: OperationalEventType.PlaybackStarted)); var bytes = await File.ReadAllBytesAsync(path); Assert.Equal(new byte[] { 239, 187, 191 }, bytes.Take(3).ToArray()); var text = await File.ReadAllTextAsync(path); Assert.Contains("' =".Replace(" ", ""), text); Assert.Contains("Observada", text); Assert.Contains("Segundos reproduzidos", text); Assert.DoesNotContain("Aplicativo iniciado", text); Assert.Contains("\"\"Título\"\"", text); await Assert.ThrowsAsync<IOException>(() => h.History.ExportAsync(path, new()));
    }
    [Fact] public async Task InvalidExportDestinationReportsFailure()
    { await using var h = new Harness(); await h.Start(); await Assert.ThrowsAnyAsync<IOException>(() => h.History.ExportAsync(Path.Combine(h.Fixture.Base, "missing", "x.csv"), new())); }
    private sealed class BlockingRepository(IOperationalHistoryRepository inner) : IOperationalHistoryRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Fail;
        public Task InitializeAsync(CancellationToken t) => inner.InitializeAsync(t); public Task MarkUnconfirmedAsync(Guid id, CancellationToken t) => inner.MarkUnconfirmedAsync(id, t);
        public async Task AppendAsync(IReadOnlyList<OperationalWrite> entries, CancellationToken t) { Entered.TrySetResult(); await Release.Task.WaitAsync(t); if (Fail) throw new IOException("Banco indisponível"); await inner.AppendAsync(entries, t); }
        public Task<OperationalHistoryPage> QueryAsync(OperationalHistoryQuery q, CancellationToken t) => inner.QueryAsync(q,t); public Task<IReadOnlyList<OperationalSession>> SessionsAsync(int p, CancellationToken t) => inner.SessionsAsync(p,t); public Task<IReadOnlyList<PlaybackPeriod>> PlaybackAsync(OperationalHistoryQuery q,CancellationToken t)=>inner.PlaybackAsync(q,t); public Task<IReadOnlyList<PlaybackPeriod>> PlaybackByIdsAsync(IReadOnlyList<Guid> ids,CancellationToken t)=>inner.PlaybackByIdsAsync(ids,t); public Task<OperationalHistorySummary> SummaryAsync(DateTimeOffset d,CancellationToken t)=>inner.SummaryAsync(d,t); public Task<int> RetainAsync(DateTimeOffset d,CancellationToken t)=>inner.RetainAsync(d,t);
    }
    [Fact] public async Task FullQueueReservesCriticalCapacityAndDoesNotBlockPlayback()
    {
        await using var h = new Harness(); var repo = new BlockingRepository(h.Repo); await using var history = new OperationalHistoryService(repo, clock: h.Clock, capacity: 8); await history.StartAsync(false); await repo.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i=0;i<100;i++) history.Record(OperationalEventType.ManualIntervention,"Teste","Info"); history.Record(OperationalEventType.DeviceDisconnected,"Dispositivo","Crítico",OperationalEventSeverity.Error);
        var audio = new EndableAudio(); using var player = new PlaybackService(audio, history:history); player.PlayLibrary(Track(), []); Assert.Equal(PlayerState.Playing, player.State); player.Pause(); player.Stop(); repo.Release.TrySetResult(); await history.FlushAsync(); Assert.True(history.DroppedEvents > 0); Assert.Single((await history.QueryAsync(new(Type:OperationalEventType.DeviceDisconnected))).Events);
    }
    [Fact] public async Task DatabaseFailureDoesNotEscapeToPlayerOrRecurse()
    {
        await using var h = new Harness(); var repo = new BlockingRepository(h.Repo) { Fail=true }; repo.Release.TrySetResult(); int logs=0; await using var history = new OperationalHistoryService(repo, (_,_)=>Interlocked.Increment(ref logs)); await history.StartAsync(false); var audio = new EndableAudio(); using var player = new PlaybackService(audio, history:history); player.PlayLibrary(Track(), []); await history.FlushAsync(); Assert.Equal(PlayerState.Playing, player.State); Assert.NotNull(history.Failure); Assert.InRange(logs,1,4); Assert.True(history.DroppedEvents>0);
    }
    [Fact] public async Task DisposeFlushesPendingEventsAndIsIdempotent()
    {
        await using var h = new Harness(); await h.Start(); for(int i=0;i<100;i++) h.History.Record(OperationalEventType.ManualIntervention,"Teste","Pendente"); await Task.WhenAll(Enumerable.Range(0,4).Select(_=>h.History.DisposeAsync().AsTask())); Assert.Equal(102,(await h.Repo.QueryAsync(new(),default)).Total); Assert.NotNull(Assert.Single(await h.Repo.SessionsAsync(0,default)).EndedUtc);
    }
    [Fact] public async Task ViewModelFiltersPagesDetailsRetentionAndValidation()
    {
        await using var h=new Harness(); await h.Start(); h.History.Record(OperationalEventType.PlaybackError,"Player","Erro",OperationalEventSeverity.Error); await h.History.FlushAsync(); using var log=new LoggerConfiguration().CreateLogger(); var audio=new EndableAudio(); await using var vm=new MainViewModel(audio,new MusicScanner(log, new AHNSF.Music.Audio.Mp3DurationReader()),new SqliteSettingsStore(h.Fixture.Database),log,history:h.History);
        vm.HistoryPeriodIndex=3; vm.HistoryFrom="2026-10-09";vm.HistoryUntil="2026-10-09";vm.HistorySeverity=vm.HistorySeverities.Single(v=>v.Value==OperationalEventSeverity.Error);await vm.LoadHistoryPageAsync(); Assert.Single(vm.HistoryEvents);vm.SelectedHistoryEvent=vm.HistoryEvents[0];Assert.Contains("Sessão:",vm.HistoryEventDetails);vm.HistoryRetentionDays=180;await vm.SaveHistoryRetentionCommand.ExecuteAsync(null);Assert.Equal(180,(await new SqliteSettingsStore(h.Fixture.Database).LoadAsync()).HistoryRetentionDays);vm.HistoryUntil="inválida";await vm.LoadHistoryPageAsync();Assert.Contains("Não foi possível",vm.HistoryFeedback);
    }
    [Fact] public async Task TenThousandEventsQueryExportAndCleanupHaveMeasuredTimes()
    {
        await using var h=new Harness();var timer=Stopwatch.StartNew();await h.Repo.AppendAsync(Enumerable.Range(0,10000).Select(i=>new OperationalWrite(Event(-i))).ToArray(),default);output.WriteLine($"Inserção 10.000: {timer.ElapsedMilliseconds} ms");timer.Restart();var page=await h.Repo.QueryAsync(new(Page:100),default);Assert.Equal(10000,page.Total);Assert.Equal(50,page.Events.Count);output.WriteLine($"Consulta/página 101: {timer.ElapsedMilliseconds} ms");timer.Restart();await h.History.ExportAsync(Path.Combine(h.Fixture.Base,"10000.csv"),new());output.WriteLine($"Exportação 10.000: {timer.ElapsedMilliseconds} ms");timer.Restart();var deleted=await h.Repo.RetainAsync(new DateTimeOffset(2027,1,1,0,0,0,TimeSpan.Zero),default);Assert.Equal(500,deleted);output.WriteLine($"Limpeza 500: {timer.ElapsedMilliseconds} ms");
    }
    [Fact] public async Task AutoStartPowerEventsAndShutdownAreAssociatedWithOneSession()
    {
        await using var h = new Harness(); await h.History.StartAsync(true);
        h.History.Record(OperationalEventType.ComputerSuspended,"Aplicativo","Suspensão observada."); h.History.Record(OperationalEventType.ComputerResumed,"Aplicativo","Retomada observada."); h.History.Record(OperationalEventType.ExitRequested,"Aplicativo","Pedido do usuário",origin:OperationalActionOrigin.Manual); await h.History.EndAsync("Parado");
        var events = await h.Events(); Assert.Equal(6,events.Count); Assert.All(events,e=>Assert.Equal(h.History.SessionId,e.SessionId)); Assert.Single(events,e=>e.Type==OperationalEventType.ApplicationAutoStarted);
    }
    [Fact] public async Task ShutdownFlushTimeoutIsBoundedAndDoesNotConfirmLostSession()
    {
        await using var h = new Harness(); var repo = new BlockingRepository(h.Repo); var history = new OperationalHistoryService(repo); await history.StartAsync(false); await repo.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var timer = Stopwatch.StartNew(); await history.DisposeAsync(); Assert.InRange(timer.ElapsedMilliseconds,2500,6000); Assert.Empty(await h.Repo.SessionsAsync(0,default));
    }
    [Fact] public void StructuredDataAndDescriptionsAreBounded()
    {
        Assert.Equal("{}",OperationalHistoryService.SafeJson(new string('x',3000))); Assert.Equal("{}",OperationalHistoryService.SafeJson("[1,2]")); Assert.Equal("{}",OperationalHistoryService.SafeJson("invalid")); Assert.Equal(512,OperationalHistoryService.Limit(new string('x',1000),512).Length);
    }
    [Fact] public async Task ComputerSuspensionIsExcludedFromEffectivePlaybackTime()
    {
        await using var h = new Harness(); await h.Start(); var track = Track();
        h.History.Playback(OperationalEventType.PlaybackStarted,track,null,OperationalActionOrigin.Manual,"Início"); h.Clock.Advance(5);
        h.History.Record(OperationalEventType.ComputerSuspended,"Aplicativo","Suspensão"); h.Clock.Advance(300); h.History.Record(OperationalEventType.ComputerResumed,"Aplicativo","Retomada"); h.Clock.Advance(5);
        h.History.Playback(OperationalEventType.PlaybackStopped,track,null,OperationalActionOrigin.Manual,"Fim"); await h.History.FlushAsync(); Assert.Equal(10,Assert.Single(await h.History.PlaybackAsync(new())).EffectiveSeconds);
    }
}
