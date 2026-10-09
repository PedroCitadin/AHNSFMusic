using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Infrastructure;
using Serilog;
using Xunit;

namespace AHNSF.Music.Tests;
public sealed class PlayerTests
{
    [Fact] public async Task RealMp3MetadataAndDurationAreReadWithoutModifyingFile()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.mp3");
        var original = await File.ReadAllBytesAsync(fixture);
        using var log = new LoggerConfiguration().CreateLogger();
        var scanner = new MusicScanner(log, new AHNSF.Music.Audio.Mp3DurationReader());
        var result = await scanner.ScanAsync(Path.GetDirectoryName(fixture)!, CancellationToken.None);
        var track = Assert.Single(result.Tracks);
        Assert.Empty(result.Warnings); Assert.Equal("Faixa de teste", track.Title); Assert.Equal("AHNSF Tests", track.Artist);
        Assert.InRange(track.Duration.TotalSeconds, 1, 1.2);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture));
        using var unlocked = File.Open(fixture, FileMode.Open, FileAccess.Read, FileShare.None);
    }
    private static Track Track(string name) => new(name + ".mp3", name, "Artista", TimeSpan.FromSeconds(60));
    [Fact] public void QueueDoesNotWrapAtBoundaries()
    {
        var queue = new PlaybackQueue(); var a = Track("A"); var b = Track("B");
        queue.Replace([a, b]); Assert.Equal(a, queue.Move(1)); Assert.Null(queue.Move(-1));
        Assert.Equal(b, queue.Move(1)); Assert.Null(queue.Move(1)); Assert.Equal(a, queue.Move(-1));
    }
    [Fact] public void RefreshPreservesCurrentByPathAndClearsRemovedTrack()
    {
        var queue = new PlaybackQueue(); var a = Track("A"); queue.Replace([a]); queue.Select(a);
        var updated = a with { Title = "Atualizada" }; queue.Replace([updated]); Assert.Equal(updated, queue.Current);
        queue.Replace([]); Assert.Null(queue.Current); Assert.Null(queue.Move(1));
    }
    [Fact] public void ViewModelPausesResumesSeeksAndStops()
    {
        var engine = new FakeAudio(); using var vm = CreateViewModel(engine);
        vm.Tracks.Add(Track("A")); vm.SelectedTrack = vm.Tracks[0];
        vm.PlayPauseCommand.Execute(null); Assert.Equal(PlayerState.Playing, engine.State);
        vm.PlayPauseCommand.Execute(null); Assert.Equal(PlayerState.Paused, engine.State);
        vm.PlayPauseCommand.Execute(null); Assert.Equal(1, engine.LoadCount);
        vm.Position = 30; Assert.Equal(TimeSpan.FromSeconds(30), engine.Position);
        vm.Volume = 70; Assert.Equal(0.7f, engine.Volume);
        vm.StopCommand.Execute(null); Assert.Equal(PlayerState.Stopped, engine.State); Assert.Null(vm.CurrentTrack);
    }
    [Fact] public void CorruptAudioShowsPortugueseErrorWithoutEscaping()
    {
        var engine = new FakeAudio { FailLoad = true }; using var vm = CreateViewModel(engine);
        vm.Tracks.Add(Track("Inválida")); vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying); Assert.Contains("Não foi possível reproduzir", vm.Status);
    }
    [Fact] public void EmptyPlayerDoesNotOpenOutput()
    {
        var engine = new FakeAudio(); using var vm = CreateViewModel(engine);
        vm.PlayPauseCommand.Execute(null); Assert.Equal(0, engine.LoadCount); Assert.Contains("Selecione uma pasta", vm.Status);
    }
    [Fact] public void ShutdownCanBeCalledTwice()
    {
        var engine = new FakeAudio(); var vm = CreateViewModel(engine);
        vm.Dispose(); vm.Dispose(); Assert.Equal(PlayerState.Stopped, engine.State);
    }
    [Fact] public async Task SqlitePersistsSettingsAcrossInstances()
    {
        var folder = NewTemp();
        try
        {
            var path = Path.Combine(folder, "settings.db"); var store = new SqliteSettingsStore(path);
            Assert.Equal(new AppSettings(), await store.LoadAsync());
            var expected = new AppSettings(@"D:\Músicas", .62f, 1230, 810, 20, 40);
            await store.SaveAsync(expected);
            Assert.Equal(expected, await new SqliteSettingsStore(path).LoadAsync());
            await store.SaveAsync(expected with { Volume = .2f });
            Assert.Equal(.2f, (await store.LoadAsync()).Volume);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
    [Fact] public async Task ScannerFindsNestedMp3AndSurvivesBadMetadata()
    {
        var folder = NewTemp();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            await File.WriteAllTextAsync(Path.Combine(folder, "sub", "Teste.MP3"), "invalid");
            await File.WriteAllTextAsync(Path.Combine(folder, "ignorar.wav"), "invalid");
            using var log = new LoggerConfiguration().CreateLogger();
            var result = await new MusicScanner(log, new FakeDurationReader()).ScanAsync(folder, CancellationToken.None);
            Assert.Single(result.Tracks); Assert.Single(result.Warnings); Assert.Equal("Teste", result.Tracks[0].Title);
            Assert.Equal("invalid", await File.ReadAllTextAsync(result.Tracks[0].Path));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public async Task ScannerReportsMissingDirectoryAndCancellation()
    {
        using var log = new LoggerConfiguration().CreateLogger(); var scanner = new MusicScanner(log, new FakeDurationReader());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => scanner.ScanAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(Path.GetTempPath(), new CancellationToken(true)));
    }
    private static string NewTemp() { var path = Path.Combine(Path.GetTempPath(), "AHNSF-test-" + Guid.NewGuid()); Directory.CreateDirectory(path); return path; }
    private static MainViewModel CreateViewModel(FakeAudio engine) => new(engine, new FakeScanner(), new FakeStore(), new LoggerConfiguration().CreateLogger());
    private sealed class FakeScanner : IMusicScanner
    { public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new ScanResult([], [])); }
    private sealed class FakeDurationReader : ITrackDurationReader
    { public TimeSpan Read(string path) => TimeSpan.FromSeconds(60); }
    private sealed class FakeStore : ISettingsStore
    { public Task<AppSettings> LoadAsync() => Task.FromResult(new AppSettings()); public Task SaveAsync(AppSettings settings) => Task.CompletedTask; }
    private sealed class FakeAudio : IAudioEngine
    {
        public event EventHandler<AudioEndedEventArgs>? Ended { add { } remove { } }
        public PlayerState State { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(60);
        public float Volume { get; set; }
        public int LoadCount { get; private set; }
        public bool FailLoad { get; init; }
        public void Load(string path) { LoadCount++; if (FailLoad) throw new InvalidDataException(); }
        public void Play() => State = PlayerState.Playing;
        public void Pause() => State = PlayerState.Paused;
        public void Stop() => State = PlayerState.Stopped;
        public void Dispose() { }
    }
}
