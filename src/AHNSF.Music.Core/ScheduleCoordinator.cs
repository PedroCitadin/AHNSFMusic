namespace AHNSF.Music.Core;

/// <summary>Reconciles desired local schedule with playback; timers never define correctness.</summary>
public sealed class ScheduleCoordinator : IAsyncDisposable
{
    private readonly IScheduleService store;
    private readonly IPlaylistService playlists;
    private readonly IPlaybackService player;
    private readonly IScheduleClock clock;
    private readonly IScheduleEvaluator evaluator;
    private readonly Action<string, Exception?> log;
    private readonly ScheduleCoordinatorOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object control = new();
    private CancellationTokenSource? currentAction;
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private string? applied, suspendedKey, attemptedKey;
    private string? previousWinner, previousWinnerName;
    private OperationMode? previousMode;
    private DateTimeOffset? lastTime;
    private long lastTimestamp;
    private string? lastZone;
    private bool suspended, ownsSession;
    private float originalVolume;
    private long revision;
    private DateTimeOffset retryAt;
    private string warning = "", lastLog = "";
    public ScheduleStatus Status { get; private set; } = new(OperationMode.Manual, new(null, [], null, null), false, PlayerState.Stopped, null, "", "Inicialização");
    public event EventHandler<ScheduleStatus>? Changed;
    public ScheduleCoordinator(IScheduleService store, IPlaylistService playlists, IPlaybackService player,
        IScheduleClock clock, IScheduleEvaluator evaluator, Action<string, Exception?>? log = null, ScheduleCoordinatorOptions? options = null)
    { this.store = store; this.playlists = playlists; this.player = player; this.clock = clock; this.evaluator = evaluator; this.log = log ?? ((_, _) => { }); this.options = options ?? ScheduleCoordinatorOptions.Default; store.Changed += ConfigurationChanged; }
    private void ConfigurationChanged(object? sender, EventArgs args) { lock (control) { Interlocked.Increment(ref revision); retryAt = default; currentAction?.Cancel(); } }
    public async Task StartAsync(bool runWorker = true, CancellationToken token = default)
    {
        await EvaluateAsync(token);
        WriteLog("Agendador inicializado");
        if (runWorker && worker is null) worker = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(options.Interval);
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await EvaluateAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void NotifyManualIntervention()
    {
        lock (control)
        {
        if (Status.Mode != OperationMode.Automatic) return;
        Interlocked.Increment(ref revision);
        currentAction?.Cancel();
        suspendedKey = Status.Evaluation.Winner?.Key; suspended = true;
        ReleaseSession();
        Publish(Status with { IsSuspended = true, Reason = "Intervenção manual: suspenso até a próxima transição" });
        WriteLog("Automação suspensa por intervenção manual");
        }
    }
    public async Task SetModeAsync(OperationMode mode, CancellationToken token = default)
    {
        Interlocked.Increment(ref revision);
        await store.SetModeAsync(mode, token);
        suspended = false; applied = null; retryAt = default;
        await EvaluateAsync(token);
    }
    public async Task EvaluateAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await gate.WaitAsync(token);
        lock (control) currentAction = linked;
        try
        {
            var config = await store.LoadAsync(token);
            var now = clock.UtcNow; var zone = clock.TimeZone;
            var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastTime is { } before && (Math.Abs(((now - before) - System.Diagnostics.Stopwatch.GetElapsedTime(lastTimestamp, timestamp)).TotalSeconds) > 15 || System.Diagnostics.Stopwatch.GetElapsedTime(lastTimestamp, timestamp) > options.Interval * 3 || lastZone != zone.Id))
                WriteLog("Programação reavaliada após alteração do relógio, fuso ou retomada");
            lastTime = now; lastTimestamp = timestamp; lastZone = zone.Id;
            var evaluation = evaluator.Evaluate(config.Rules, now, zone);
            var key = evaluation.Winner?.Key;
            if (config.Mode == OperationMode.Automatic && previousWinner != key)
            {
                if (previousWinner is not null) WriteLog($"Regra encerrada: {previousWinnerName}");
                if (previousWinner is not null && key is not null) WriteLog($"Troca de regra vencedora: {evaluation.Winner!.Rule.Name}");
            }
            previousWinner = key; previousWinnerName = evaluation.Winner?.Rule.Name;
            if (previousMode != config.Mode) { WriteLog($"Modo de operação: {config.Mode}"); previousMode = config.Mode; }
            if (attemptedKey != key) { retryAt = default; warning = ""; attemptedKey = key; Status = Status with { PlaylistName = null }; }
            Status = Status with { Mode = config.Mode, Evaluation = evaluation };
            if (suspended && key != suspendedKey) { suspended = false; applied = null; retryAt = default; WriteLog("Automação retomada na transição de regra"); }
            if (config.Mode == OperationMode.Manual) { ReleaseSession(); applied = null; suspended = false; warning = ""; }
            else if (!suspended)
            {
                if (evaluation.Winner is null)
                {
                    lock (control)
                    {
                        if (suspended || token.IsCancellationRequested) return;
                        if (player.State != PlayerState.Stopped) { player.Stop(); WriteLog("Automático: reprodução parada fora da programação"); }
                        ReleaseSession(); applied = null; warning = ""; retryAt = default;
                    }
                }
                else if (applied == key && player.State == PlayerState.Paused) NotifyManualIntervention();
                else if (applied == key && player.State == PlayerState.Playing)
                { lock (control) { if (!suspended && !token.IsCancellationRequested) player.Volume = (float)(evaluation.Winner.Rule.Volume / 100); } }
                else if (applied == key && player.State == PlayerState.Stopped)
                    throw new InvalidOperationException($"A sessão agendada foi interrompida: {player.Message}");
                else if (clock.UtcNow >= retryAt)
                {
                    var actionRevision = Interlocked.Read(ref revision);
                    var rule = evaluation.Winner.Rule;
                    var detail = rule.PlaylistId is { } id ? await playlists.GetAsync(id, token) : null;
                    if (detail is null || !detail.Items.Any(i => PlaybackQueue.IsPlayable(i.Track)))
                        throw new InvalidOperationException("A playlist agendada foi removida, está vazia ou não contém músicas disponíveis.");
                    lock (control) { if (actionRevision != Interlocked.Read(ref revision)) return; player.Stop(); }
                    await player.LoadPlaylistAsync(detail.Playlist.Id, false, token: token);
                    token.ThrowIfCancellationRequested();
                    var fresh = evaluator.Evaluate((await store.LoadAsync(token)).Rules, clock.UtcNow, clock.TimeZone);
                    lock (control)
                    {
                    if (actionRevision != Interlocked.Read(ref revision) || fresh.Winner?.Key != key) return;
                    if (!ownsSession) { originalVolume = player.Volume; ownsSession = true; }
                    player.Volume = (float)(rule.Volume / 100); player.SetScheduledRepeat(true); player.Play();
                    if (player.State != PlayerState.Playing) throw new InvalidOperationException(player.Message);
                    applied = key; warning = ""; retryAt = default;
                    Status = Status with { PlaylistName = detail.Playlist.Name };
                    WriteLog($"Regra iniciada: {rule.Name}; playlist {detail.Playlist.Name}; volume {rule.Volume}%");
                    }
                }
            }
            Publish(new(config.Mode, evaluation, suspended, player.State, evaluation.Winner is null ? null : Status.PlaylistName,
                warning, suspended ? "Suspenso até a próxima transição" : config.Mode == OperationMode.Manual ? "Controle manual" : evaluation.Winner is null ? "Sem regra ativa" : "Programação automática"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            warning = ex.Message; retryAt = clock.UtcNow + options.RetryDelay;
            if (Status.Mode == OperationMode.Automatic && !suspended) player.Stop(); ReleaseSession(); applied = null;
            WriteLog($"Aviso de programação: {warning}", ex);
            Publish(Status with { Warning = warning, PlaybackState = player.State });
        }
        finally { lock (control) currentAction = null; gate.Release(); }
    }
    private void ReleaseSession() { player.SetScheduledRepeat(false); if (ownsSession) { ownsSession = false; player.Volume = originalVolume; } }
    private void Publish(ScheduleStatus status) { Status = status; Changed?.Invoke(this, status); }
    private void WriteLog(string message, Exception? ex = null) { if (message == lastLog) return; lastLog = message; log(message, ex); }
    public void StopScheduling() { lifetime.Cancel(); Interlocked.Increment(ref revision); }
    public async ValueTask DisposeAsync()
    {
        StopScheduling(); if (worker is not null) await worker.ConfigureAwait(false);
        store.Changed -= ConfigurationChanged;
        await gate.WaitAsync().ConfigureAwait(false); try { ReleaseSession(); } finally { gate.Release(); }
    }
}
