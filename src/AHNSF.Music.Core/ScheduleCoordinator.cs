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
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly IAudioDeviceService? devices;
    private bool deviceWasReady;
    private readonly IOperationalHistoryService? history;
    private string? historyFailure;
    private bool historyAwaitingDeviceRecovery;
    private void Observe(OperationalEventType type, string text, OperationalEventSeverity severity = OperationalEventSeverity.Information, Guid? rule = null)
    { history.Observe(type, "Programação", text, severity, OperationalActionOrigin.Schedule, playlistId: Status.Evaluation.Winner?.Rule.PlaylistId, ruleId: rule ?? Status.Evaluation.Winner?.Rule.Id); }
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
    private readonly RecoveryPolicy recovery;
    private int failures, episodeAttempts;
    private bool exhausted;
    private DateTimeOffset? lastAttempt, lastRecovered, lastErrorAt;
    private string? lastError;
    public RecoveryStatus Recovery { get; private set; } = RecoveryStatus.Idle;
    public ScheduleStatus Status { get; private set; } = new(OperationMode.Manual, new(null, [], null, null), false, PlayerState.Stopped, null, "", "Inicialização");
    public event EventHandler<ScheduleStatus>? Changed;
    public ScheduleCoordinator(IScheduleService store, IPlaylistService playlists, IPlaybackService player,
        IScheduleClock clock, IScheduleEvaluator evaluator, Action<string, Exception?>? log = null, ScheduleCoordinatorOptions? options = null, RecoveryPolicy? recovery = null, IAudioDeviceService? devices = null, IOperationalHistoryService? history = null)
    { this.store = store; this.playlists = playlists; this.player = player; this.clock = clock; this.evaluator = evaluator; this.log = log ?? ((_, _) => { }); this.options = options ?? ScheduleCoordinatorOptions.Default; this.recovery = recovery ?? RecoveryPolicy.Default; this.devices = devices; this.history = history; deviceWasReady = devices?.Status.CanPlay == true; store.Changed += ConfigurationChanged; if (devices is not null) devices.Changed += DeviceChanged; }
    private void DeviceChanged(object? sender, AudioDeviceStatus status)
    {
        lock (control)
        {
            if (lifetime.IsCancellationRequested) return;
            if (deviceWasReady != status.CanPlay)
            {
                if (!status.CanPlay) historyAwaitingDeviceRecovery = true;
                Interlocked.Increment(ref revision); currentAction?.Cancel();
                applied = null; retryAt = default; ResetRecovery();
                WriteLog(status.CanPlay ? "Saída de áudio recuperada; a programação decidirá se deve retomar" : "Programação aguarda a saída de áudio configurada");
            }
            deviceWasReady = status.CanPlay;
            if (wake.CurrentCount == 0) wake.Release();
        }
    }
    private void ConfigurationChanged(object? sender, EventArgs args) { lock (control) { Interlocked.Increment(ref revision); retryAt = default; ResetRecovery(); currentAction?.Cancel(); } }
    public async Task StartAsync(bool runWorker = true, CancellationToken token = default)
    {
        await EvaluateAsync(token);
        WriteLog("Agendador inicializado");
        if (runWorker && worker is null) worker = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { await wake.WaitAsync(options.Interval, lifetime.Token); await EvaluateAsync(lifetime.Token); } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void NotifyManualIntervention()
    {
        lock (control)
        {
        if (Status.Mode != OperationMode.Automatic) return;
        historyAwaitingDeviceRecovery = false;
        if (!suspended) { Observe(OperationalEventType.ManualIntervention, "Intervenção manual na programação."); Observe(OperationalEventType.AutomationSuspended, "Automação suspensa até a próxima transição."); }
        Interlocked.Increment(ref revision);
        currentAction?.Cancel();
        suspendedKey = Status.Evaluation.Winner?.Key; suspended = true;
        // Manual intervention also cancels any pending recovery attempt.
        if (failures > 0) WriteLog("Recuperação cancelada por intervenção manual");
        ResetRecovery(); retryAt = default;
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
        lock (control) ResetRecovery();
        await EvaluateAsync(token);
    }
    /// <summary>Windows resumed (power event or detected time gap): recovery starts fresh and state is re-evaluated by the caller.</summary>
    public void NotifyResumed()
    {
        lock (control) { retryAt = default; ResetRecovery(); }
        WriteLog("Retomada detectada: programação, relógio e áudio serão reavaliados");
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
            {
                WriteLog("Programação reavaliada após alteração do relógio, fuso ou retomada");
                lock (control) { if (failures > 0 || exhausted) retryAt = default; ResetRecovery(); }
            }
            lastTime = now; lastTimestamp = timestamp; lastZone = zone.Id;
            var evaluation = evaluator.Evaluate(config.Rules, now, zone);
            var key = evaluation.Winner?.Key;
            if (config.Mode == OperationMode.Automatic && previousWinner != key)
            {
                if (previousWinner is not null) Observe(OperationalEventType.RuleEnded, "Janela da regra anterior encerrada.");
                if (key is not null) Observe(previousWinner is null ? OperationalEventType.RuleActivated : OperationalEventType.WinnerChanged, "Nova regra vencedora: " + evaluation.Winner!.Rule.Name, rule: evaluation.Winner.Rule.Id);
                if (previousWinner is not null) WriteLog($"Regra encerrada: {previousWinnerName}");
                if (previousWinner is not null && key is not null) WriteLog($"Troca de regra vencedora: {evaluation.Winner!.Rule.Name}");
            }
            previousWinner = key; previousWinnerName = evaluation.Winner?.Rule.Name;
            if (previousMode != config.Mode) { Observe(config.Mode == OperationMode.Automatic ? OperationalEventType.AutomaticMode : OperationalEventType.ManualMode, "Modo operacional: " + config.Mode); WriteLog($"Modo de operação: {config.Mode}"); previousMode = config.Mode; }
            if (attemptedKey != key) { retryAt = default; warning = ""; attemptedKey = key; Status = Status with { PlaylistName = null }; lock (control) ResetRecovery(); }
            Status = Status with { Mode = config.Mode, Evaluation = evaluation };
            if (suspended && key != suspendedKey) { suspended = false; applied = null; retryAt = default; WriteLog("Automação retomada na transição de regra"); }
            if (config.Mode == OperationMode.Manual) { historyAwaitingDeviceRecovery = false; ReleaseSession(); applied = null; suspended = false; warning = ""; lock (control) ResetRecovery(); }
            else if (!suspended)
            {
                player.SetOperationalContext(OperationalActionOrigin.Schedule, evaluation.Winner?.Rule.Id, devices?.Status.BoundDeviceId);
                if (devices?.Status.CanPlay == false && evaluation.Winner is not null)
                {
                    lock (control)
                    {
                        if (suspended || token.IsCancellationRequested) return;
                        if (player.State != PlayerState.Stopped) player.Stop();
                        ReleaseSession(); applied = null; warning = devices.Status.Message;
                    }
                }
                else if (evaluation.Winner is null)
                {
                    historyAwaitingDeviceRecovery = false;
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
                    throw new ScheduledPlaybackException($"A sessão agendada foi interrompida: {player.Message}", player.LastError);
                else if (clock.UtcNow >= retryAt)
                {
                    var actionRevision = Interlocked.Read(ref revision);
                    if (failures > 0) lock (control) { lastAttempt = clock.UtcNow; episodeAttempts = failures; Observe(OperationalEventType.RecoveryAttempt, $"Tentativa de recuperação {failures} de {recovery.MaxAttempts}."); WriteLog($"Tentativa de recuperação {failures} de {recovery.MaxAttempts}"); PublishRecovery(); }
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
                    if (actionRevision != Interlocked.Read(ref revision) || fresh.Winner?.Key != key || devices?.Status.CanPlay == false) return;
                    if (!ownsSession) { originalVolume = player.Volume; ownsSession = true; }
                    player.SetOperationalContext(failures > 0 ? OperationalActionOrigin.Recovery : OperationalActionOrigin.Schedule, rule.Id, devices?.Status.BoundDeviceId);
                    player.Volume = (float)(rule.Volume / 100); player.SetScheduledRepeat(true); player.Play();
                    if (player.State != PlayerState.Playing) throw new ScheduledPlaybackException(player.Message, player.LastError);
                    applied = key; historyFailure = null; warning = ""; retryAt = default;
                    if (historyAwaitingDeviceRecovery && failures == 0) Observe(OperationalEventType.RecoverySucceeded, "Reprodução retomada após retorno da saída configurada.");
                    historyAwaitingDeviceRecovery = false;
                    if (failures > 0) { Observe(OperationalEventType.RecoverySucceeded, "Reprodução recuperada."); lastRecovered = clock.UtcNow; WriteLog($"Recuperação bem-sucedida após {failures} tentativa(s)"); ResetRecovery(); }
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
            if (historyFailure != ex.GetType().Name + Status.Evaluation.Winner?.Key) { historyFailure = ex.GetType().Name + Status.Evaluation.Winner?.Key; Observe(OperationalEventType.RuleFailed, "Falha ao executar a regra; consulte o log técnico.", OperationalEventSeverity.Error); }
            warning = ex.Message; retryAt = clock.UtcNow + options.RetryDelay;
            if (ex is ScheduledPlaybackException failure && Status.Mode == OperationMode.Automatic && !suspended) lock (control) RegisterFailure(failure);
            if (Status.Mode == OperationMode.Automatic && !suspended) player.Stop(); ReleaseSession(); applied = null;
            WriteLog($"Aviso de programação: {warning}", ex);
            Publish(Status with { Warning = warning, PlaybackState = player.State });
        }
        finally { lock (control) currentAction = null; gate.Release(); }
    }
    private void RegisterFailure(ScheduledPlaybackException failure)
    {
        var now = clock.UtcNow; var cause = failure.InnerException;
        failures++; lastError = cause?.Message ?? failure.Message; lastErrorAt = now;
        if (RecoveryPolicy.Classify(cause) == FailureKind.Permanent)
        {
            exhausted = true; retryAt = DateTimeOffset.MaxValue;
            warning = $"Falha de reprodução que não se resolve com novas tentativas: {lastError} Verifique a playlist; a programação aguarda intervenção ou a próxima transição.";
            WriteLog("Falha permanente de áudio; recuperação automática não será tentada", cause);
        }
        else if (recovery.DelayAfter(failures) is { } delay)
        {
            retryAt = now + delay;
            warning = $"Falha de áudio. Tentativa de recuperação {failures} de {recovery.MaxAttempts} em {delay.TotalSeconds:0} s. Use Parar ou Suspender automação para cancelar.";
            WriteLog($"Falha de áudio; tentativa de recuperação {failures} de {recovery.MaxAttempts} em {delay.TotalSeconds:0} s", cause);
        }
        else
        {
            exhausted = true; retryAt = DateTimeOffset.MaxValue;
            warning = $"Recuperação esgotada após {recovery.MaxAttempts} tentativas. Verifique a saída de áudio; a programação aguarda intervenção (Ativar Automático) ou a próxima transição.";
            Observe(OperationalEventType.RecoveryExhausted, "Recuperação esgotada; aguardando intervenção ou nova transição.", OperationalEventSeverity.Error);
            WriteLog("Recuperação esgotada; aguardando intervenção ou nova transição", cause);
        }
        PublishRecovery();
    }
    private void ResetRecovery()
    {
        if (retryAt == DateTimeOffset.MaxValue) retryAt = default;
        failures = 0; exhausted = false; PublishRecovery();
    }
    private void PublishRecovery() => Recovery = new(failures, exhausted, failures > 0 && !exhausted && retryAt != default ? retryAt : null, episodeAttempts, lastAttempt, lastRecovered, lastError, lastErrorAt);
    private void ReleaseSession() { player.SetScheduledRepeat(false); if (ownsSession) { ownsSession = false; player.Volume = originalVolume; } }
    private void Publish(ScheduleStatus status) { Status = status; Changed?.Invoke(this, status); }
    private void WriteLog(string message, Exception? ex = null) { if (message == lastLog) return; lastLog = message; log(message, ex); }
    public void StopScheduling() { lifetime.Cancel(); Interlocked.Increment(ref revision); }
    public async ValueTask DisposeAsync()
    {
        StopScheduling(); if (worker is not null) await worker.ConfigureAwait(false);
        store.Changed -= ConfigurationChanged;
        if (devices is not null) devices.Changed -= DeviceChanged;
        await gate.WaitAsync().ConfigureAwait(false); try { ReleaseSession(); } finally { gate.Release(); }
    }
}
