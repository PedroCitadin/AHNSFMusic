using AHNSF.Music.Audio;
using AHNSF.Music.Core;
using NAudio.Wave;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class AudioOutputEngineTests
{
    private sealed class Factory : IWaveOutputFactory
    {
        public List<Output> Outputs { get; } = [];
        public bool Fail { get; set; }
        public bool FailInit { get; set; }
        public IWavePlayer Create(string? id) { if (Fail) throw new IOException("Driver falhou"); var value = new Output(id, FailInit); Outputs.Add(value); return value; }
    }
    private sealed class Output(string? id, bool failInit) : IWavePlayer
    {
        public string? Id { get; } = id;
        public int Disposals { get; private set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = new(44100, 2);
        public float Volume { get; set; }
        public IWaveProvider? Provider { get; private set; }
        public void Init(IWaveProvider provider) { Provider = provider; OutputWaveFormat = provider.WaveFormat; if (failInit) throw new IOException("Init falhou"); }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new()); }
        public void End(Exception? error = null) { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new(error)); }
        public void Dispose() => Disposals++;
    }
    private static string File => Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.mp3");
    [Theory] [InlineData(false)] [InlineData(true)]
    public void SwitchPreservesPositionVolumeAndStateWithoutDuplicateOutput(bool paused)
    {
        var factory = new Factory(); using var audio = new NAudioEngine(factory); audio.SwitchOutput("usb-a"); audio.Load(File); audio.Volume = .42f; audio.Play(); if (paused) audio.Pause(); audio.Position = TimeSpan.FromSeconds(.4);
        var position = audio.Position; var ended = 0; audio.Ended += (_, _) => ended++; audio.SwitchOutput("usb-b");
        Assert.Equal(paused ? PlayerState.Paused : PlayerState.Playing, audio.State); Assert.Equal(position, audio.Position); Assert.Equal(.42f, audio.Volume); Assert.Equal("usb-b", audio.EffectiveDeviceId);
        Assert.Equal(1, factory.Outputs[0].Disposals); Assert.Equal(0, factory.Outputs[1].Disposals); Assert.Equal(0, ended);
        if (paused) { audio.Play(); Assert.Equal(PlaybackState.Playing, factory.Outputs[1].PlaybackState); }
        Assert.Equal(.42f, ((AudioFileReader)factory.Outputs[1].Provider!).Volume);
    }
    [Fact] public void SwitchFailureReleasesResourcesAndBlocksAllLoadsUntilReapplied()
    {
        var factory = new Factory(); using var audio = new NAudioEngine(factory); audio.SwitchOutput("usb-a"); audio.Load(File); audio.Play(); factory.Fail = true; var errors = 0; audio.Ended += (_, e) => { if (e.Error is not null) errors++; };
        Assert.Throws<IOException>(() => audio.SwitchOutput("usb-b")); Assert.Equal(1, errors); Assert.Equal(PlayerState.Stopped, audio.State); Assert.Null(audio.EffectiveDeviceId); Assert.Equal(1, factory.Outputs[0].Disposals);
        Assert.Throws<AudioDeviceUnavailableException>(() => audio.Load(File)); factory.Fail = false; audio.SwitchOutput("usb-b"); audio.Load(File); audio.Play(); Assert.Equal("usb-b", audio.EffectiveDeviceId);
    }
    [Fact] public void FailedInitDisposesCandidateAndReader()
    {
        var factory = new Factory { FailInit = true }; using var audio = new NAudioEngine(factory); audio.SwitchOutput("usb"); Assert.Throws<IOException>(() => audio.Load(File)); Assert.Equal(1, Assert.Single(factory.Outputs).Disposals);
        using var exclusive = System.IO.File.Open(File, FileMode.Open, FileAccess.Read, FileShare.None); Assert.Equal(PlayerState.Stopped, audio.State);
    }
    [Fact] public void DisconnectDoesNotAdvanceQueueAndCannotUseDefaultFallback()
    {
        var factory = new Factory(); using var audio = new NAudioEngine(factory); using var player = new PlaybackService(audio); audio.SwitchOutput("usb");
        var a = new Track(File, "A", "Artista", TimeSpan.FromSeconds(1)); var b = a with { Title = "B" }; player.PlayLibrary(a, [a, b]); audio.BlockOutput("USB removido");
        Assert.Equal(PlayerState.Stopped, player.State); Assert.Null(player.CurrentTrack); Assert.IsType<AudioDeviceUnavailableException>(player.LastError); Assert.Single(factory.Outputs);
        player.Play(); Assert.Single(factory.Outputs); Assert.Equal(PlayerState.Stopped, player.State);
    }
    [Fact] public void NaturalEndStillAdvancesAndOldOutputsCannotAdvanceNewSession()
    {
        var factory = new Factory(); using var audio = new NAudioEngine(factory); using var player = new PlaybackService(audio); audio.SwitchOutput("usb"); var a = new Track(File, "A", "Artista", TimeSpan.FromSeconds(1));
        player.PlayLibrary(a, [a, a with { Title = "B" }]); var old = factory.Outputs[0]; audio.SwitchOutput("other"); old.End(); Assert.Equal("A", player.CurrentTrack!.Title);
        factory.Outputs[^1].End(); Assert.Equal("B", player.CurrentTrack!.Title); Assert.Equal(PlayerState.Playing, player.State);
    }
    [Fact] public void StopAndDisposeReleaseEveryOutputOnceAndKeepVolume()
    {
        var factory = new Factory(); var audio = new NAudioEngine(factory); audio.SwitchOutput("usb"); audio.Volume = .22f; audio.Load(File); audio.Play(); audio.Stop(); audio.Stop(); audio.Dispose(); audio.Dispose();
        Assert.Equal(1, Assert.Single(factory.Outputs).Disposals); Assert.Equal(.22f, audio.Volume); Assert.Equal(TimeSpan.Zero, audio.Duration);
    }
}
