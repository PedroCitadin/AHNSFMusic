using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AHNSF.Music.Audio;

/// <summary>Shared WASAPI routes to a stable endpoint; volume is applied by AudioFileReader only.</summary>
public sealed class WasapiOutputFactory : IWaveOutputFactory
{
    public IWavePlayer Create(string? endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = endpointId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(endpointId);
        try
        {
            if (device.State != DeviceState.Active) throw new AHNSF.Music.Core.AudioDeviceUnavailableException("A saída configurada não está ativa.");
            return new OwnedOutput(device);
        }
        catch { device.Dispose(); throw; }
    }
    private sealed class OwnedOutput : IWavePlayer
    {
        private readonly MMDevice device;
        private readonly WasapiOut output;
        public OwnedOutput(MMDevice device) { this.device = device; output = new(device, AudioClientShareMode.Shared, true, 150); output.PlaybackStopped += OnStopped; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        private void OnStopped(object? sender, StoppedEventArgs args) => PlaybackStopped?.Invoke(this, args);
        public PlaybackState PlaybackState => output.PlaybackState;
        public WaveFormat OutputWaveFormat => output.OutputWaveFormat;
        public float Volume { get => output.Volume; set => output.Volume = value; }
        public void Init(IWaveProvider provider) => output.Init(provider);
        public void Play() => output.Play();
        public void Pause() => output.Pause();
        public void Stop() => output.Stop();
        public void Dispose() { output.PlaybackStopped -= OnStopped; try { output.Dispose(); } finally { device.Dispose(); } }
    }
}
