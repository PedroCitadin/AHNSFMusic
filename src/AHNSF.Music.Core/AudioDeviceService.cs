using System.Threading.Channels;

namespace AHNSF.Music.Core;

/// <summary>Serializes discovery/routing. Recovery decisions belong only to ScheduleCoordinator.</summary>
public sealed class AudioDeviceService : IAudioDeviceService
{
    private readonly IAudioDeviceDiscovery discovery;
    private readonly IAudioOutputController output;
    private readonly IOperationalHistoryService? history;
    private readonly Action<string, Exception?> log;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly TimeSpan reconcileInterval;
    private Task? worker;
    private readonly object disposeGate = new();
    private Task? disposal;
    private bool started, disposed;
    private AudioDeviceStatus status = AudioDeviceStatus.Initial;
    public AudioDeviceStatus Status { get => status with { EffectiveDeviceId = output.EffectiveDeviceId }; private set => status = value; }
    public event EventHandler<AudioDeviceStatus>? Changed;
    public AudioDeviceService(IAudioDeviceDiscovery discovery, IAudioOutputController output, Action<string, Exception?>? log = null,
        Func<DateTimeOffset>? now = null, TimeSpan? reconcileInterval = null, IOperationalHistoryService? history = null)
    { this.discovery = discovery; this.output = output; this.history = history; this.log = log ?? ((_, _) => { }); this.now = now ?? (() => DateTimeOffset.UtcNow); this.reconcileInterval = reconcileInterval ?? TimeSpan.FromSeconds(30); }
    public async Task StartAsync(AudioDeviceSelection selection, bool runWorker = true, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) return;
            started = true; discovery.Changed += OnDiscoveryChanged;
            Status = Status with { Selection = selection };
            RefreshCore(initial: true);
            if (runWorker) worker = Task.Run(RunAsync);
        }
        finally { gate.Release(); }
    }
    private void OnDiscoveryChanged(object? sender, EventArgs args) => signals.Writer.TryWrite(true);
    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(reconcileInterval);
                try { await signals.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                // Coalesce bursts. A periodic reconciliation also protects against lost notifications.
                while (signals.Reader.TryRead(out _)) { }
                await RefreshAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async Task RefreshAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); linked.Token.ThrowIfCancellationRequested(); RefreshCore(); }
        finally { gate.Release(); }
    }
    private void RefreshCore(bool initial = false)
    {
        try
        {
            var devices = discovery.Enumerate().OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCulture).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var selected = Status.Selection;
            if (!Enum.IsDefined(selected.Mode)) throw new ArgumentException("Modo de seleção de áudio inválido. Revise a configuração.");
            var bound = initial ? selected.Mode == AudioDeviceSelectionMode.Specific ? selected.DeviceId : devices.FirstOrDefault(d => d.IsDefault)?.Id : Status.BoundDeviceId;
            // The default is pinned for this session: a default change never redirects sound silently.
            bool defaultChanged = !initial && selected.Mode == AudioDeviceSelectionMode.Default && devices.FirstOrDefault(d => d.IsDefault)?.Id != bound;
            var available = bound is not null && devices.Any(d => d.Id == bound && d.IsAvailable);
            var old = Status;
            var next = old with { Devices = devices, BoundDeviceId = bound, IsAvailable = available, RequiresIntervention = old.RequiresIntervention || defaultChanged };
            if (old.CanPlay && (!available || defaultChanged))
            {
                var message = defaultChanged ? "O padrão do Windows mudou. A saída foi interrompida; aplique a seleção para autorizar o padrão atual."
                    : "Dispositivo de áudio desconectado ou indisponível. Nenhuma saída alternativa será utilizada.";
                next = next with { LastDisconnectedUtc = now(), Message = message };
                history.Observe(OperationalEventType.DeviceDisconnected, "Dispositivo", defaultChanged ? "Padrão Windows alterado; saída bloqueada." : "Saída configurada desconectada.", OperationalEventSeverity.Warning, OperationalActionOrigin.Device, deviceId: bound);
                output.BlockOutput(message); log(message, null);
            }
            if (initial || !old.IsAvailable && available && !next.RequiresIntervention)
            {
                if (available)
                {
                    output.SwitchOutput(bound!);
                    if (!initial) { history.Observe(OperationalEventType.DeviceReconnected, "Dispositivo", "Saída configurada reconectada.", origin: OperationalActionOrigin.Device, deviceId: bound); next = next with { LastReconnectedUtc = now() }; log("Dispositivo selecionado reconectado: " + bound, null); }
                }
                else { history.Observe(OperationalEventType.DeviceUnavailable, "Dispositivo", "Saída configurada indisponível; seleção preservada.", OperationalEventSeverity.Warning, OperationalActionOrigin.Device, deviceId: bound); output.BlockOutput("Dispositivo de áudio configurado indisponível."); }
            }
            next = next with { EffectiveDeviceId = output.EffectiveDeviceId, Message = next.RequiresIntervention ? "Saída bloqueada. Revise e aplique a seleção do dispositivo."
                : available ? "Dispositivo disponível. Manual aguarda o operador; Automático respeita a programação." : "Dispositivo configurado indisponível. A seleção foi preservada; não há fallback." };
            Publish(next);
        }
        catch (Exception ex) { BlockFailure(ex); }
    }
    public async Task<bool> SelectAsync(AudioDeviceSelection selection, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); linked.Token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(selection.Mode)) throw new ArgumentException("Modo de seleção de áudio inválido.");
            var devices = discovery.Enumerate();
            var target = selection.Mode == AudioDeviceSelectionMode.Default ? devices.FirstOrDefault(d => d.IsDefault) : devices.FirstOrDefault(d => d.Id == selection.DeviceId);
            if (target?.IsAvailable != true) throw new AudioDeviceUnavailableException("A saída escolhida não está disponível. Conecte ou habilite o dispositivo e tente novamente.");
            selection = selection.Mode == AudioDeviceSelectionMode.Default ? new() : selection with { DeviceName = target.Name };
            // Once validated, the operator's selection is authoritative even if opening it fails.
            var previousId = Status.BoundDeviceId;
            Status = Status with { Selection = selection, Devices = devices.ToArray(), BoundDeviceId = target.Id };
            history.Observe(OperationalEventType.OutputSelected, "Dispositivo", "Seleção explícita de saída.", origin: OperationalActionOrigin.Manual, deviceId: target.Id);
            try
            {
                output.SwitchOutput(target.Id);
                if (previousId != target.Id) history.Observe(OperationalEventType.OutputChanged, "Dispositivo", "Saída de áudio alterada.", origin: OperationalActionOrigin.Manual, deviceId: target.Id);
                Publish(Status with { EffectiveDeviceId = output.EffectiveDeviceId, IsAvailable = true, RequiresIntervention = false, Message = "Saída selecionada: " + target.Name });
                log("Seleção manual de saída: " + target.Id, null); return true;
            }
            catch (Exception ex) { BlockFailure(ex); return false; }
        }
        finally { gate.Release(); }
    }
    private void BlockFailure(Exception ex)
    {
        var message = "Não foi possível preparar a saída de áudio. A reprodução foi bloqueada; revise o dispositivo e aplique a seleção novamente.";
        if (Status.RequiresIntervention && Status.LastFailure == ex.Message && Status.Message == message) return;
        try { output.BlockOutput(message); } catch (Exception stopError) { log("Falha ao liberar saída de áudio", stopError); }
        Publish(Status with { EffectiveDeviceId = null, RequiresIntervention = true, Message = message, LastFailure = ex.Message, LastFailureUtc = now() });
        history.Observe(OperationalEventType.DeviceOpenFailed, "Dispositivo", "Falha ao preparar saída; revisão manual necessária.", OperationalEventSeverity.Error, OperationalActionOrigin.Device, deviceId: Status.BoundDeviceId);
        log(message, ex);
    }
    private void Publish(AudioDeviceStatus next)
    {
        var old = Status;
        if (old with { Devices = next.Devices } == next && old.Devices.SequenceEqual(next.Devices)) return;
        Status = next; Changed?.Invoke(this, next);
    }
    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        if (disposed) return;
        disposed = true; discovery.Changed -= OnDiscoveryChanged; lifetime.Cancel();
        if (worker is not null) await worker.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { output.BlockOutput("Monitor de áudio encerrado."); discovery.Dispose(); }
        finally { gate.Release(); }
    }
}
