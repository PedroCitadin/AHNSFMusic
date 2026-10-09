using AHNSF.Music.Core;
using NAudio.Wave;

namespace AHNSF.Music.Audio;

public sealed class NAudioEngine : IAudioEngine
{
    private AudioFileReader? reader;
    private WaveOutEvent? output;
    private float volume = 0.35f;
    public event EventHandler<AudioEndedEventArgs>? Ended;
    public PlayerState State { get; private set; }
    public TimeSpan Duration => reader?.TotalTime ?? TimeSpan.Zero;
    public TimeSpan Position
    {
        get => reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (reader is not null) reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, Duration.TotalSeconds)); }
    }
    public float Volume
    {
        get => volume;
        set { volume = Math.Clamp(value, 0, 1); if (reader is not null) reader.Volume = volume; }
    }
    public void Load(string path)
    {
        Release();
        try
        {
            reader = new AudioFileReader(path) { Volume = volume };
            output = new WaveOutEvent();
            output.Init(reader);
            output.PlaybackStopped += OnStopped;
        }
        catch { Release(); throw; }
    }
    public void Play()
    {
        if (output is null) throw new InvalidOperationException("Nenhuma música carregada.");
        output.Play(); State = PlayerState.Playing;
    }
    public void Pause() { output?.Pause(); State = PlayerState.Paused; }
    public void Stop() => Release();
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, output)) return;
        State = PlayerState.Stopped;
        Ended?.Invoke(this, new AudioEndedEventArgs(e.Exception));
    }
    private void Release()
    {
        var old = output;
        output = null;
        try
        {
            if (old is not null)
            {
                old.PlaybackStopped -= OnStopped;
                try { old.Stop(); }
                finally { old.Dispose(); }
            }
        }
        finally { reader?.Dispose(); reader = null; State = PlayerState.Stopped; }
    }
    public void Dispose() => Release();
}
