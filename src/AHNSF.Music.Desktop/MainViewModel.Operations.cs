using AHNSF.Music.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

// Fase 0.6: start-up/tray/close preferences, diagnostics, power events and orderly shutdown.
// Playback is still only driven through existing commands, PlaybackService and ScheduleCoordinator.
public partial class MainViewModel
{
    private readonly AutoStartService? autoStart;
    private bool syncingPreferences;
    private Task? shutdown;
    private string lastRelevantError = "";
    private DateTimeOffset? lastRelevantErrorAt, lastSuspendAt, lastResumeAt;
    [ObservableProperty] private bool autoStartEnabled;
    [ObservableProperty] private bool startMinimized;
    [ObservableProperty] private bool closeToTray = true;
    [ObservableProperty] private AutoStartStatus autoStartStatus = new(AutoStartState.Disabled, "Inicialização com o Windows desativada.");
    [ObservableProperty] private bool trayAvailable;
    [ObservableProperty] private bool launchedByWindows;
    public sealed record DiagnosticRow(string Label, string Value, bool Attention = false);
    [ObservableProperty] private IReadOnlyList<DiagnosticRow> diagnostics = [];
    public string AutoStartText => AutoStartStatus.Message;
    public bool AutoStartNeedsAttention => AutoStartStatus.NeedsAttention;
    public bool CloseExits { get => !CloseToTray; set => CloseToTray = !value; }
    public string TrayStatusText => TrayAvailable ? "Ícone disponível na área de notificação." : "Área de notificação indisponível: fechar a janela encerra o aplicativo.";
    /// <summary>Exiting would silence the building or stop the automatic schedule.</summary>
    public bool MusicAtRisk => playback.State == PlayerState.Playing || presentedSchedule?.Mode == OperationMode.Automatic;
    public string TrayStatus => $"{PlayerStateLabel}{(CurrentTrack is null ? "" : $": {CurrentTitle}")} • {ModeLabel}";
    public string TrayPlayPauseLabel => IsPlaying ? "Pausar" : "Reproduzir";
    public bool IsAutomaticMode => presentedSchedule?.Mode == OperationMode.Automatic;
    private void LoadPreferences()
    {
        syncingPreferences = true;
        AutoStartEnabled = settings.AutoStartEnabled; StartMinimized = settings.StartMinimized;
        CloseToTray = settings.CloseBehavior != CloseBehavior.Exit;
        syncingPreferences = false;
        if (autoStart is null) return;
        AutoStartStatus = autoStart.Inspect(settings.AutoStartEnabled);
        if (AutoStartStatus.NeedsAttention) logger.Warning(AutoStartStatus.Error, "Inicialização automática: {State} — {Message}", AutoStartStatus.State, AutoStartStatus.Message);
    }
    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (syncingPreferences || !initialized || disposed) return;
        if (autoStart is null) { RevertAutoStart(!value); Status = "Inicialização automática indisponível nesta execução."; return; }
        var result = autoStart.Apply(value);
        AutoStartStatus = result;
        if (result.State is AutoStartState.Error or AutoStartState.Unsupported)
        {
            logger.Error(result.Error, "Falha na integração com a inicialização automática: {Message}", result.Message);
            RevertAutoStart(!value); Status = result.Message; return;
        }
        logger.Information("Inicialização com o Windows {State}", value ? "ativada" : "desativada");
        settings = settings with { AutoStartEnabled = value }; TrackBackground(SaveAsync());
        Status = value ? "O AHNSF Music será iniciado com o Windows para este usuário." : "Inicialização com o Windows desativada.";
    }
    private void RevertAutoStart(bool value) { syncingPreferences = true; AutoStartEnabled = value; syncingPreferences = false; }
    partial void OnStartMinimizedChanged(bool value) { if (!syncingPreferences && initialized && !disposed) { settings = settings with { StartMinimized = value }; TrackBackground(SaveAsync()); } }
    partial void OnCloseToTrayChanged(bool value)
    {
        OnPropertyChanged(nameof(CloseExits));
        if (!syncingPreferences && initialized && !disposed) { settings = settings with { CloseBehavior = value ? CloseBehavior.MinimizeToTray : CloseBehavior.Exit }; TrackBackground(SaveAsync()); }
    }
    partial void OnAutoStartStatusChanged(AutoStartStatus value) { OnPropertyChanged(nameof(AutoStartText)); OnPropertyChanged(nameof(AutoStartNeedsAttention)); NotifyPresentation(); }
    partial void OnTrayAvailableChanged(bool value) { OnPropertyChanged(nameof(TrayStatusText)); NotifyPresentation(); }
    [RelayCommand] private void ReapplyAutoStart()
    {
        if (autoStart is null) return;
        AutoStartStatus = autoStart.Apply(settings.AutoStartEnabled);
        if (AutoStartStatus.State is AutoStartState.Error or AutoStartState.Unsupported) logger.Error(AutoStartStatus.Error, "Falha na integração com a inicialização automática: {Message}", AutoStartStatus.Message);
        else logger.Information("Inicialização automática reaplicada: {State}", AutoStartStatus.State);
        Status = AutoStartStatus.Message;
    }
    public async Task MarkCloseNoticeShownAsync() { settings = settings with { CloseNoticeShown = true }; await SaveAsync(); }
    /// <summary>Tray play/pause resumes the loaded queue instead of replacing it with the library.</summary>
    [RelayCommand] private void TrayPlayPause()
    {
        if (disposed) return;
        if (IsModalOpen) { Status = "Conclua ou cancele a confirmação aberta na janela."; return; }
        if (IsPlaying || queue.Items.Count == 0) { PlayPause(); return; }
        ManualIntervention(); playback.Play();
    }
    public Task SetModeFromTrayAsync(OperationMode mode) => mode == OperationMode.Automatic ? AutomaticModeCommand.ExecuteAsync(null) : ManualModeCommand.ExecuteAsync(null);
    public void OnSystemSuspending() { lastSuspendAt = DateTimeOffset.Now; NotifyPresentation(); }
    /// <summary>After resume: recovery restarts fresh, the library is reconciled and the schedule decides again (Manual never starts audio).</summary>
    public async Task OnSystemResumedAsync()
    {
        if (disposed) return;
        lastResumeAt = DateTimeOffset.Now;
        scheduler?.NotifyResumed();
        if (library is not null && settings.MusicDirectory is not null) TrackBackground(RefreshAsync());
        if (scheduler is not null)
        {
            try { await scheduler.EvaluateAsync(lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Error(ex, "Falha ao reavaliar a programação após a retomada."); }
        }
        NotifyPresentation();
    }
    /// <summary>Orderly exit: scheduler first, then pending work, audio, settings, resources. Safe to call more than once.</summary>
    public Task ShutdownAsync(Func<AppSettings, AppSettings>? geometry = null) => shutdown ??= ShutdownCoreAsync(geometry);
    private async Task ShutdownCoreAsync(Func<AppSettings, AppSettings>? geometry)
    {
        scheduler?.StopScheduling();
        searching?.Cancel();
        try { if (!disposed) playback.Stop(); } catch (Exception ex) { logger.Warning(ex, "Falha ao parar a reprodução no encerramento"); }
        if (geometry is not null && initialized) { settings = geometry(settings); await SaveAsync(); }
        await DisposeAsync();
    }
    private void TrackRelevantError()
    {
        var text = playback.LastError?.Message is { } audioError ? $"Áudio: {audioError}" : ScheduleWarning.Length > 0 ? ScheduleWarning : "";
        if (text.Length == 0 || text == lastRelevantError) return;
        lastRelevantError = text; lastRelevantErrorAt = DateTimeOffset.Now;
    }
    private static string Time(DateTimeOffset? value) => value is { } time ? TimeZoneInfo.ConvertTime(time, TimeZoneInfo.Local).ToString("dd/MM HH:mm:ss") : "—";
    private string AudioHealth(RecoveryStatus recovery) => recovery.Exhausted ? "Recuperação esgotada: aguardando intervenção"
        : recovery.IsRecovering ? $"Em recuperação: falha {recovery.ConsecutiveFailures} de {RecoveryPolicy.Default.MaxAttempts}; próxima tentativa {Time(recovery.NextAttemptUtc)}"
        : AudioDeviceNeedsAttention ? "Bloqueado: saída configurada indisponível ou aguardando intervenção"
        : playback.LastError is not null ? "Falha na última reprodução" : playback.State == PlayerState.Playing ? "Operacional (reproduzindo na saída configurada)" : "Pronto (saída configurada)";
    private void UpdateDiagnostics()
    {
        TrackRelevantError();
        var recovery = scheduler?.Recovery ?? RecoveryStatus.Idle;
        var schedule = presentedSchedule;
        Diagnostics =
        [
            new("Player", $"{PlayerStateLabel}{(CurrentTrack is null ? "" : $" • {CurrentTitle}")}"),
            new("Modo operacional", ModeLabel),
            new("Agendador", schedule is null ? "Não iniciado" : schedule.IsSuspended ? "Suspenso por intervenção manual" : schedule.Reason, schedule?.IsSuspended == true),
            new("Playlist ativa", ActiveOrigin),
            new("Próxima transição", NextAction),
            new("Pasta musical", settings.MusicDirectory ?? "Não configurada", settings.MusicDirectory is null),
            new("Biblioteca", LibraryHealth, !DirectoryAvailable),
            new("Mecanismo de áudio", AudioHealth(recovery), recovery.Exhausted || recovery.IsRecovering || playback.LastError is not null),
            new("Último erro relevante", lastRelevantError.Length == 0 ? "Nenhum nesta sessão" : $"{Time(lastRelevantErrorAt)} • {lastRelevantError}"),
            new("Última recuperação", recovery.LastRecoveredUtc is null ? "Nenhuma nesta sessão" : $"{Time(recovery.LastRecoveredUtc)} (bem-sucedida)"),
            new("Tentativas recentes de recuperação", $"{Math.Max(recovery.ConsecutiveFailures, recovery.LastEpisodeAttempts)} (última {Time(recovery.LastAttemptUtc)})"),
            new("Inicialização com o Windows", AutoStartStatus.Message, AutoStartStatus.NeedsAttention),
            new("Área de notificação", TrayAvailable ? "Disponível" : "Indisponível", !TrayAvailable),
            new("Esta execução", LaunchedByWindows ? "Iniciada automaticamente pelo Windows" : "Iniciada pelo usuário"),
            new("Suspensão/retomada", lastSuspendAt is null && lastResumeAt is null ? "Nenhuma nesta sessão" : $"Suspenso {Time(lastSuspendAt)} • retomado {Time(lastResumeAt)}")
        ];
        if (audioDevices?.Status is { } device)
            Diagnostics = Diagnostics.Concat(new DiagnosticRow[]
            {
                new("Dispositivo configurado", device.ConfiguredName),
                new("Identificador configurado", device.BoundDeviceId ?? "Nenhum"),
                new("Dispositivo efetivamente utilizado", device.EffectiveName),
                new("Padrão do Windows", device.DefaultDevice?.Name ?? "Nenhum disponível"),
                new("Disponibilidade de áudio", device.Message, !device.CanPlay),
                new("Última desconexão", Time(device.LastDisconnectedUtc)),
                new("Última reconexão", Time(device.LastReconnectedUtc)),
                new("Última falha de dispositivo", device.LastFailure is null ? "Nenhuma nesta sessão" : $"{Time(device.LastFailureUtc)} • {device.LastFailure}"),
                new("Intervenção no dispositivo", device.RequiresIntervention ? "Necessária: revise e aplique a seleção" : !device.IsAvailable ? "Aguardando o mesmo dispositivo; Manual não inicia automaticamente" : "Não necessária", device.RequiresIntervention)
            }).ToArray();
        foreach (var property in new[] { nameof(TrayStatus), nameof(TrayPlayPauseLabel), nameof(IsAutomaticMode), nameof(MusicAtRisk) }) OnPropertyChanged(property);
    }
}
