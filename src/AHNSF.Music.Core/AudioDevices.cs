namespace AHNSF.Music.Core;

public enum AudioDeviceSelectionMode { Default, Specific }
public sealed record AudioDeviceSelection(AudioDeviceSelectionMode Mode = AudioDeviceSelectionMode.Default, string? DeviceId = null, string? DeviceName = null);
public sealed record AudioOutputDevice(string Id, string Name, bool IsAvailable, bool IsDefault)
{
    public string Label => $"{Name}{(IsDefault ? " • padrão do Windows" : "")}{(!IsAvailable ? " • indisponível" : "")}";
}
public interface IAudioDeviceDiscovery : IDisposable
{
    event EventHandler? Changed;
    IReadOnlyList<AudioOutputDevice> Enumerate();
}
/// <summary>Output routing only. It never chooses a fallback or changes the Windows mixer.</summary>
public interface IAudioOutputController
{
    string? EffectiveDeviceId { get; }
    void SwitchOutput(string deviceId);
    void BlockOutput(string reason);
}
public sealed record AudioDeviceStatus(AudioDeviceSelection Selection, IReadOnlyList<AudioOutputDevice> Devices,
    string? BoundDeviceId, string? EffectiveDeviceId, bool IsAvailable, bool RequiresIntervention, string Message,
    DateTimeOffset? LastDisconnectedUtc = null, DateTimeOffset? LastReconnectedUtc = null,
    string? LastFailure = null, DateTimeOffset? LastFailureUtc = null)
{
    public bool CanPlay => IsAvailable && !RequiresIntervention;
    public AudioOutputDevice? DefaultDevice => Devices.FirstOrDefault(d => d.IsDefault);
    public string ConfiguredName => Selection.Mode == AudioDeviceSelectionMode.Default ? "Padrão do Windows" : Selection.DeviceName ?? Selection.DeviceId ?? "Dispositivo não configurado";
    public string EffectiveName => Devices.FirstOrDefault(d => d.Id == EffectiveDeviceId)?.Name ?? "Nenhuma saída ativa";
    public static AudioDeviceStatus Initial { get; } = new(new(), [], null, null, false, false, "Descoberta de áudio não iniciada.");
}
public interface IAudioDeviceService : IAsyncDisposable
{
    AudioDeviceStatus Status { get; }
    event EventHandler<AudioDeviceStatus>? Changed;
    Task StartAsync(AudioDeviceSelection selection, bool runWorker = true, CancellationToken token = default);
    Task<bool> SelectAsync(AudioDeviceSelection selection, CancellationToken token = default);
    Task RefreshAsync(CancellationToken token = default);
}
public sealed class AudioDeviceUnavailableException(string message) : IOException(message);
