using AHNSF.Music.Core;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

public partial class MainViewModel
{
    private readonly IAudioDeviceService? audioDevices;
    [ObservableProperty] private IReadOnlyList<AudioOutputDevice> audioOutputs = [];
    [ObservableProperty] private AudioOutputDevice? selectedAudioOutput;
    [ObservableProperty] private bool useDefaultAudioOutput = true;
    [ObservableProperty] private bool isChangingAudioOutput;
    [ObservableProperty] private string audioDeviceFeedback = "";
    public bool HasAudioDeviceService => audioDevices is not null;
    public string AudioDeviceMessage => audioDevices?.Status.Message ?? "Seleção de áudio indisponível nesta execução.";
    public bool AudioDeviceNeedsAttention => audioDevices is not null && !audioDevices.Status.CanPlay;
    public string AudioDeviceConfigured => audioDevices?.Status.ConfiguredName ?? "Padrão do Windows";
    public string AudioDeviceEffective => audioDevices?.Status.EffectiveName ?? "Saída padrão";
    public bool IsAudioSelectionEnabled => !UseDefaultAudioOutput && !IsChangingAudioOutput;
    partial void OnUseDefaultAudioOutputChanged(bool value) => OnPropertyChanged(nameof(IsAudioSelectionEnabled));
    partial void OnIsChangingAudioOutputChanged(bool value) => OnPropertyChanged(nameof(IsAudioSelectionEnabled));
    private async Task InitializeAudioDevicesAsync()
    {
        if (audioDevices is null) return;
        audioDevices.Changed += OnAudioDevicesChanged;
        await audioDevices.StartAsync(settings.AudioDevice ?? new(), token: lifetime.Token);
        UseDefaultAudioOutput = audioDevices.Status.Selection.Mode == AudioDeviceSelectionMode.Default;
        UpdateAudioDevices(audioDevices.Status);
        SelectedAudioOutput = AudioOutputs.FirstOrDefault(d => d.Id == audioDevices.Status.BoundDeviceId);
    }
    private void OnAudioDevicesChanged(object? sender, AudioDeviceStatus value)
    {
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.Post(() => UpdateAudioDevices(value));
        else UpdateAudioDevices(value);
    }
    private void UpdateAudioDevices(AudioDeviceStatus value)
    {
        if (disposed) return;
        var choices = value.Devices.ToList();
        if (value.Selection.Mode == AudioDeviceSelectionMode.Specific && value.Selection.DeviceId is { } id && !choices.Any(d => d.Id == id))
            choices.Add(new(id, value.Selection.DeviceName ?? id, false, false));
        var selectedId = SelectedAudioOutput?.Id;
        if (!AudioOutputs.SequenceEqual(choices)) { AudioOutputs = choices; SelectedAudioOutput = choices.FirstOrDefault(d => d.Id == selectedId); }
        NotifyAudioDevicePresentation(); NotifyPresentation();
    }
    private void NotifyAudioDevicePresentation()
    {
        foreach (var property in new[] { nameof(AudioDeviceMessage), nameof(AudioDeviceNeedsAttention), nameof(AudioDeviceConfigured), nameof(AudioDeviceEffective) }) OnPropertyChanged(property);
    }
    [RelayCommand] private async Task RefreshAudioOutputsAsync()
    {
        if (audioDevices is null || IsChangingAudioOutput) return;
        try { await audioDevices.RefreshAsync(lifetime.Token); UpdateAudioDevices(audioDevices.Status); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Não foi possível atualizar os dispositivos de áudio."); }
    }
    [RelayCommand] private async Task ApplyAudioOutputAsync()
    {
        if (audioDevices is null || IsChangingAudioOutput) return;
        if (!UseDefaultAudioOutput && SelectedAudioOutput is null) { AudioDeviceFeedback = "Selecione um dispositivo de saída."; return; }
        var selection = UseDefaultAudioOutput ? new AudioDeviceSelection() : new(AudioDeviceSelectionMode.Specific, SelectedAudioOutput!.Id, SelectedAudioOutput.Name);
        IsChangingAudioOutput = true; ManualIntervention();
        try
        {
            var success = await audioDevices.SelectAsync(selection, lifetime.Token);
            settings = settings with { AudioDevice = audioDevices.Status.Selection }; await SaveAsync();
            AudioDeviceFeedback = success ? "Seleção salva. A troca é uma intervenção manual; a automação respeitará a suspensão." : audioDevices.Status.Message;
            Status = AudioDeviceFeedback; SyncPlayback(); UpdateAudioDevices(audioDevices.Status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AudioDeviceFeedback = ex.Message; logger.Warning(ex, "Falha ao selecionar saída de áudio"); }
        finally { IsChangingAudioOutput = false; }
    }
}
