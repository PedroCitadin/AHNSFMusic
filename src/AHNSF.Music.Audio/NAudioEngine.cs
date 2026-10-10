using AHNSF.Music.Core;
using NAudio.Wave;

namespace AHNSF.Music.Audio;

public interface IWaveOutputFactory { IWavePlayer Create(string? endpointId); }

public sealed class NAudioEngine : IAudioEngine, IAudioOutputController
{
    private readonly object gate = new();
    private readonly IWaveOutputFactory factory;
    private AudioFileReader? reader;
    private IWavePlayer? output;
    private string? configuredId;
    private bool blocked, disposed;
    private float volume = 0.35f;
    private PlayerState state;
    public NAudioEngine() : this(new WasapiOutputFactory()) { }
    public NAudioEngine(IWaveOutputFactory factory) => this.factory = factory;
    public event EventHandler<AudioEndedEventArgs>? Ended;
    public string? EffectiveDeviceId { get { lock (gate) return output is null ? null : configuredId; } }
    public PlayerState State { get { lock (gate) return state; } }
    public TimeSpan Duration { get { lock (gate) return reader?.TotalTime ?? TimeSpan.Zero; } }
    public TimeSpan Position
    {
        get { lock (gate) return reader?.CurrentTime ?? TimeSpan.Zero; }
        set { lock (gate) { if (reader is not null) reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, reader.TotalTime.TotalSeconds)); } }
    }
    public float Volume
    {
        get { lock (gate) return volume; }
        set { lock (gate) { volume = Math.Clamp(value, 0, 1); if (reader is not null) reader.Volume = volume; } }
    }
    public void Load(string path)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); EnsureAvailable(); Release();
            try { reader = new AudioFileReader(path) { Volume = volume }; OpenOutput(); }
            catch { Release(); throw; }
        }
    }
    private void OpenOutput()
    {
        var candidate = factory.Create(configuredId);
        try { candidate.Init(reader!); output = candidate; output.PlaybackStopped += OnStopped; }
        catch { candidate.Dispose(); throw; }
    }
    private void EnsureAvailable() { if (blocked) throw new AudioDeviceUnavailableException("Saída de áudio bloqueada ou indisponível. Revise o dispositivo em Configurações."); }
    public void Play()
    {
        lock (gate)
        {
            EnsureAvailable();
            if (output is null) throw new InvalidOperationException("Nenhuma música carregada.");
            output.Play(); state = PlayerState.Playing;
        }
    }
    public void Pause() { lock (gate) { if (output is null) return; output.Pause(); state = PlayerState.Paused; } }
    public void Stop() { lock (gate) Release(); }
    public void SwitchOutput(string deviceId)
    {
        Exception? failure = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Identificador da saída ausente.");
            if (!blocked && configuredId == deviceId) return;
            var previous = state; var position = reader?.CurrentTime;
            CloseOutput(); configuredId = deviceId; blocked = false;
            try
            {
                if (reader is not null)
                {
                    reader.CurrentTime = position!.Value; OpenOutput();
                    if (previous == PlayerState.Playing) { output!.Play(); state = PlayerState.Playing; }
                    else state = previous;
                }
            }
            catch (Exception ex) { blocked = true; Release(); failure = ex; }
        }
        if (failure is not null) { Ended?.Invoke(this, new(failure)); throw failure; }
    }
    public void BlockOutput(string reason)
    {
        bool hadTrack;
        lock (gate) { hadTrack = reader is not null; blocked = true; Release(); }
        if (hadTrack) Ended?.Invoke(this, new(new AudioDeviceUnavailableException(reason)));
    }
    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        // WasapiOut.Stop joins its worker. Never wait for gate from that worker.
        if (!ReferenceEquals(sender, output) || disposed) return;
        state = PlayerState.Stopped;
        Ended?.Invoke(this, new(args.Exception));
    }
    private void CloseOutput()
    {
        var old = output; output = null; state = PlayerState.Stopped;
        if (old is null) return;
        old.PlaybackStopped -= OnStopped;
        try { old.Stop(); } finally { old.Dispose(); }
    }
    private void Release() { try { CloseOutput(); } finally { reader?.Dispose(); reader = null; state = PlayerState.Stopped; } }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; Release(); } }
}
