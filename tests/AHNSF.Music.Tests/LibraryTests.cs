using System.Diagnostics;
using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Infrastructure;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class LibraryTests
{
    [Fact] public async Task InitialIndexPersistsCompleteMetadataAndReopensWithoutDuplicates()
    {
        await using var f = new LibraryFixture(); var path = f.WriteTrack("nested/song.MP3", "Canção", "São João", "Verão", LibraryFixture.Picture());
        var result = await f.StartAsync(); var track = Assert.Single(result.Tracks);
        Assert.Equal("São João", track.Artist); Assert.Equal("Verão", track.Album); Assert.Equal("Artista do álbum", track.AlbumArtist);
        Assert.Equal("Ambiente", track.Genre); Assert.Equal(2026u, track.Year); Assert.NotEqual(Guid.Empty, track.Id);
        Assert.Equal(Path.Combine("nested", "song.MP3"), track.RelativePath); Assert.True(track.FileSize > 0); Assert.NotNull(track.ArtworkCacheKey);
        var reopened = new SqliteLibraryRepository(f.Database); Assert.Equal(track, Assert.Single(await reopened.LoadAsync(f.Music, CancellationToken.None)));
        var second = await f.Service.ReconcileAsync(false, CancellationToken.None);
        Assert.Equal(0, second.Processed); Assert.Equal(1, f.Metadata.Count);
        Assert.Equal(track.Id, Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks).Id);
        Assert.True(File.Exists(path));
    }
    [Fact] public async Task AddAndRemoveAreReflectedWithoutLosingStableIds()
    {
        await using var f = new LibraryFixture(); var path = f.WriteTrack("a.mp3"); var first = Assert.Single((await f.StartAsync()).Tracks);
        f.WriteTrack("b.mp3", "Outra"); await f.Service.ReconcileAsync(false, CancellationToken.None);
        Assert.Equal(2, (await f.Service.QueryAsync(new(), CancellationToken.None)).Count);
        File.Delete(path); await f.Service.ReconcileAsync(false, CancellationToken.None);
        Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
        var all = await f.Repository.LoadAsync(f.Music, CancellationToken.None);
        Assert.Equal(AvailabilityStatus.Missing, all.Single(t => t.Id == first.Id).Availability);
        f.WriteTrack("a.mp3"); await f.Service.ReconcileAsync(false, CancellationToken.None);
        Assert.Equal(first.Id, (await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks.Single(t => t.Path == path).Id);
    }
    [Fact] public async Task MetadataChangeUpdatesRecordWithoutChangingId()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "Antes"); var first = Assert.Single((await f.StartAsync()).Tracks);
        f.WriteTrack("a.mp3", "Depois da alteração", "Novo", "Novo álbum"); await f.Service.ReconcileAsync(false, CancellationToken.None);
        var updated = Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
        Assert.Equal("Depois da alteração", updated.Title); Assert.Equal(first.Id, updated.Id); Assert.Equal(first.IndexedAtUtc, updated.IndexedAtUtc);
    }
    [Fact] public async Task ForcedReindexDetectsSameSizeAndTimestampEdit()
    {
        await using var f = new LibraryFixture(); var path = f.WriteTrack("a.mp3", "AAAA"); var first = Assert.Single((await f.StartAsync()).Tracks);
        f.WriteTrack("a.mp3", "BBBB"); File.SetLastWriteTimeUtc(path, first.LastModifiedUtc);
        Assert.Equal(0, (await f.Service.ReconcileAsync(false, CancellationToken.None)).Processed);
        await f.Service.ReconcileAsync(true, CancellationToken.None);
        Assert.Equal("BBBB", Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks).Title);
    }
    [Fact] public async Task RenameCreatesNewLocationAndCaseOnlyRenamePreservesIdentity()
    {
        await using var f = new LibraryFixture(); var path = f.WriteTrack("a.mp3"); var first = Assert.Single((await f.StartAsync()).Tracks);
        var upper = Path.Combine(f.Music, "A.MP3"); File.Move(path, upper); await f.Service.ReconcileAsync(true, CancellationToken.None);
        Assert.Equal(first.Id, Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks).Id);
        File.Move(upper, Path.Combine(f.Music, "renamed.mp3")); await f.Service.ReconcileAsync(false, CancellationToken.None);
        Assert.Equal("renamed.mp3", Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks).FileName);
    }
    [Fact] public async Task SwitchingDirectorySeparatesCatalogsAndPreservesPreviousRoot()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3"); var first = Assert.Single((await f.StartAsync()).Tracks);
        var other = Path.Combine(f.Base, "other"); Directory.CreateDirectory(other);
        await f.Service.SetDirectoryAsync(other, CancellationToken.None); await f.Service.ReconcileAsync(false, CancellationToken.None);
        Assert.Empty((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
        await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        Assert.Equal(first.Id, Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks).Id);
    }
    [Fact] public async Task UnavailableRootNeverErasesCatalog()
    {
        await using var f = new LibraryFixture(watcher: false); f.WriteTrack("a.mp3"); await f.StartAsync();
        Directory.Move(f.Music, Path.Combine(f.Base, "offline"));
        Assert.False((await f.Service.ReconcileAsync(false, CancellationToken.None)).DirectoryAvailable);
        Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
    }
    [Fact] public async Task CorruptMp3IsIsolatedAndHealthyFileStillIndexes()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("healthy.mp3"); await File.WriteAllTextAsync(Path.Combine(f.Music, "bad.mp3"), "bad");
        var result = await f.StartAsync(); Assert.Equal(2, result.Count);
        Assert.Contains(result.Tracks, t => t.Availability == AvailabilityStatus.Invalid);
        Assert.Contains(result.Tracks, t => t.Availability == AvailabilityStatus.Available);
    }
    [Theory]
    [InlineData("cancao", "Canção", "Outro", "Outro", "a.mp3")]
    [InlineData("sao joao", "Outra", "São João", "Outro", "a.mp3")]
    [InlineData("verao", "Outra", "Outro", "Verão", "a.mp3")]
    [InlineData("meu arquivo", "Outra", "Outro", "Outro", "Meu Arquivo.mp3")]
    public async Task SearchIgnoresCaseAndAccents(string search, string title, string artist, string album, string file)
    {
        await using var f = new LibraryFixture(); f.WriteTrack(file, title, artist, album); await f.StartAsync();
        Assert.Single((await f.Service.QueryAsync(new(search.ToUpperInvariant()), CancellationToken.None)).Tracks);
        Assert.Empty((await f.Service.QueryAsync(new("%_' OR 1=1"), CancellationToken.None)).Tracks);
    }
    [Theory]
    [InlineData(LibrarySort.Title)] [InlineData(LibrarySort.Artist)] [InlineData(LibrarySort.Album)]
    [InlineData(LibrarySort.Duration)] [InlineData(LibrarySort.Added)]
    public async Task SortAndDirectionAreApplied(LibrarySort sort)
    {
        await using var f = new LibraryFixture(); var a = f.WriteTrack("a.mp3"); var b = f.WriteTrack("b.mp3");
        var now = DateTime.UtcNow;
        await f.Repository.ApplyAsync([
            new Track(a,"A","A",TimeSpan.FromSeconds(1)) { Id=Guid.NewGuid(), RootPath=f.Music, Album="A", IndexedAtUtc=now },
            new Track(b,"B","B",TimeSpan.FromSeconds(2)) { Id=Guid.NewGuid(), RootPath=f.Music, Album="B", IndexedAtUtc=now.AddSeconds(1) }
        ], CancellationToken.None);
        var ascending = await f.Repository.QueryAsync(f.Music, new(Sort: sort), CancellationToken.None);
        var descending = await f.Repository.QueryAsync(f.Music, new(Sort: sort, Descending:true), CancellationToken.None);
        Assert.Equal("A", ascending.Tracks[0].Title); Assert.Equal("B", descending.Tracks[0].Title);
    }
    [Fact] public async Task ArtistAndAlbumFiltersCombine()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", artist:"João",album:"Azul"); f.WriteTrack("b.mp3",artist:"Maria",album:"Azul"); f.WriteTrack("c.mp3",artist:"João",album:"Verde"); await f.StartAsync();
        Assert.Equal(2, (await f.Service.QueryAsync(new(Artist:"JOAO"), CancellationToken.None)).Tracks.Count);
        Assert.Single((await f.Service.QueryAsync(new(Artist:"joao",Album:"AZUL"), CancellationToken.None)).Tracks);
    }
    [Fact] public async Task CancelledIndexNeverCommitsPartialBatch()
    {
        await using var f = new LibraryFixture(stability: TimeSpan.FromSeconds(2)); f.WriteTrack("a.mp3");
        await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        using var cancel = new CancellationTokenSource(60);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ReconcileAsync(false, cancel.Token));
        Assert.Empty(await f.Repository.LoadAsync(f.Music, CancellationToken.None));
    }
    [Fact] public async Task SwitchCancelsOldIndexBeforeStartingNewRoot()
    {
        await using var f = new LibraryFixture(stability: TimeSpan.FromSeconds(2)); f.WriteTrack("a.mp3");
        await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        var indexing = f.Service.ReconcileAsync(false, CancellationToken.None);
        await Task.Delay(70); var other = Path.Combine(f.Base,"other"); Directory.CreateDirectory(other);
        await f.Service.SetDirectoryAsync(other, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexing);
        Assert.Empty((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
    }
    [Fact] public async Task FileBeingCopiedIsDeferredUntilWriterCloses()
    {
        await using var f = new LibraryFixture(); var path = f.WriteTrack("a.mp3");
        await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        using (var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var pending = await f.Service.ReconcileAsync(false, CancellationToken.None);
            Assert.Equal(1, pending.Pending); Assert.Empty((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
        }
        await f.Service.ReconcileAsync(false, CancellationToken.None); Assert.Single((await f.Service.QueryAsync(new(), CancellationToken.None)).Tracks);
    }
    [Fact] public async Task DuplicateEventsAreCoalescedAndOverflowTriggersReconciliation()
    {
        await using var f = new LibraryFixture(automatic:true,watcher:false); f.WriteTrack("a.mp3"); await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        await WaitUntilAsync(async () => (await f.Service.QueryAsync(new(), CancellationToken.None)).Count == 1);
        for (int i=0;i<100;i++) f.Service.SignalChange();
        await Task.Delay(200); Assert.Equal(1, f.Metadata.Count);
        f.WriteTrack("b.mp3"); f.Service.ReportWatcherError(new InternalBufferOverflowException("Simulação"));
        await WaitUntilAsync(async () => (await f.Service.QueryAsync(new(), CancellationToken.None)).Count == 2);
        Assert.Equal(2, f.Metadata.Count);
    }
    [Fact] public async Task RealWatcherFindsSubfolderChangesAndDeletion()
    {
        await using var f = new LibraryFixture(automatic:true); await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        var path = f.WriteTrack("nested/a.mp3");
        await WaitUntilAsync(async () => (await f.Service.QueryAsync(new(), CancellationToken.None)).Count == 1);
        File.Delete(path);
        await WaitUntilAsync(async () => (await f.Service.QueryAsync(new(), CancellationToken.None)).Count == 0);
    }
    [Fact] public async Task PeriodicReconciliationRecoversMissedEvents()
    {
        await using var f = new LibraryFixture(automatic:true,watcher:false,periodic:TimeSpan.FromMilliseconds(120));
        await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None); await Task.Delay(200);
        f.WriteTrack("missed.mp3"); await WaitUntilAsync(async () => (await f.Service.QueryAsync(new(), CancellationToken.None)).Count == 1);
    }
    [Fact] public async Task WatcherRestartAfterSimulatedOverflowStillDetectsNewFiles()
    {
        await using var f = new LibraryFixture(automatic:true);
        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.Changed += (_, notification) => { if (!notification.IsBusy && notification.DirectoryAvailable) reconciled.TrySetResult(); };
        await f.Service.SetDirectoryAsync(f.Music, CancellationToken.None);
        f.Service.ReportWatcherError(new InternalBufferOverflowException("Reinício simulado"));
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(6));
        f.WriteTrack("after-restart.mp3");
        await WaitUntilAsync(async () => (await f.Service.QueryAsync(new(),CancellationToken.None)).Count == 1);
        Assert.Equal(1,f.Metadata.Count);
    }
    [Fact] public async Task MigrationPreservesPhase01SettingsAndNormalizedPathIsUnique()
    {
        await using var f = new LibraryFixture(); var settings = new AppSettings(f.Music,.22f,1050,700,15,25);
        await new SqliteSettingsStore(f.Database).SaveAsync(settings);
        f.WriteTrack("a.mp3"); var track = Assert.Single((await f.StartAsync()).Tracks);
        await f.Repository.ApplyAsync([track with { Path=track.Path.ToUpperInvariant(),Id=Guid.NewGuid() }],CancellationToken.None);
        var stored = Assert.Single(await f.Repository.LoadAsync(f.Music,CancellationToken.None)); Assert.Equal(track.Id,stored.Id);
        Assert.Equal(settings, await new SqliteSettingsStore(f.Database).LoadAsync());
    }
    [Fact] public async Task ArtworkIsResizedCachedAndCleanedWithoutTouchingOtherFiles()
    {
        await using var f = new LibraryFixture(); var picture = LibraryFixture.Picture(); var key = f.Artwork.Store(picture,CancellationToken.None); Assert.NotNull(key);
        using (var bitmap=SKBitmap.Decode(f.Artwork.GetPath(key))) { Assert.InRange(bitmap.Width,1,256); Assert.InRange(bitmap.Height,1,256); }
        Assert.Equal(key,f.Artwork.Store(picture,CancellationToken.None));
        Assert.Null(f.Artwork.Store([1,2,3],CancellationToken.None)); Assert.Null(f.Artwork.GetPath("../outside"));
        var keep = Path.Combine(f.CacheDirectory,"user-file.mp3"); await File.WriteAllTextAsync(keep,"do not delete");
        var cleaner=new ArtworkCache(f.CacheDirectory,f.Logger) { Retention=TimeSpan.Zero }; cleaner.Clean(new HashSet<string>(),CancellationToken.None);
        Assert.Null(f.Artwork.GetPath(key)); Assert.True(File.Exists(keep));
    }
    [Fact] public async Task InvalidPictureDoesNotInvalidatePlayableMp3()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3",picture:[1,2,3]);
        var track=Assert.Single((await f.StartAsync()).Tracks); Assert.Null(track.ArtworkCacheKey); Assert.Equal(AvailabilityStatus.Available,track.Availability);
    }
    [Fact] public async Task ChangedEmbeddedPictureInvalidatesCacheKey()
    {
        await using var f=new LibraryFixture(); f.WriteTrack("a.mp3",picture:LibraryFixture.Picture());
        var first=Assert.Single((await f.StartAsync()).Tracks);
        f.WriteTrack("a.mp3",picture:LibraryFixture.Picture(150,100)); await f.Service.ReconcileAsync(false,CancellationToken.None);
        var changed=Assert.Single((await f.Service.QueryAsync(new(),CancellationToken.None)).Tracks);
        Assert.NotEqual(first.ArtworkCacheKey,changed.ArtworkCacheKey); Assert.Equal(first.Id,changed.Id);
    }
    [Fact] public async Task SqliteFailureIsReportedInsteadOfSilentlyReturningEmptyCatalog()
    {
        await using var f = new LibraryFixture(); await File.WriteAllTextAsync(f.Database,"not a database");
        await Assert.ThrowsAsync<SqliteException>(()=>f.Repository.LoadAsync(f.Music,CancellationToken.None));
    }
    [Fact] public async Task SynchronizationPreservesLoadedAudioAndFiltersDoNotChangeQueue()
    {
        await using var f=new LibraryFixture(); f.WriteTrack("a.mp3","A"); f.WriteTrack("b.mp3","B"); await f.StartAsync();
        var audio=new TestAudio(); using var vm=new MainViewModel(audio,new MusicScanner(f.Logger,new AHNSF.Music.Audio.Mp3DurationReader()),new SqliteSettingsStore(f.Database),f.Logger,f.Service);
        await vm.LoadCatalogAsync(true); vm.SelectedTrack=vm.Tracks[0]; vm.PlaySelectedCommand.Execute(null); vm.Position=0.5;
        var current=vm.CurrentTrack; f.WriteTrack("c.mp3","C"); await f.Service.ReconcileAsync(false,CancellationToken.None); await vm.LoadCatalogAsync(true);
        Assert.Same(current,vm.CurrentTrack); Assert.Equal(1,audio.Loads); Assert.Equal(TimeSpan.FromSeconds(.5),audio.Position);
        vm.PlayPauseCommand.Execute(null); vm.SearchText="C"; await vm.LoadCatalogAsync(false);
        Assert.Null(vm.SelectedTrack); vm.PlayPauseCommand.Execute(null); Assert.Same(current,vm.CurrentTrack); Assert.Equal(1,audio.Loads);
        vm.NextCommand.Execute(null); Assert.Equal("B",vm.CurrentTrack?.Title);
    }
    private sealed class TestAudio:IAudioEngine
    {
        public event EventHandler<AudioEndedEventArgs>? Ended { add{} remove{} }
        public int Loads {get;private set;} public PlayerState State{get;private set;}
        public TimeSpan Position{get;set;} public TimeSpan Duration=>TimeSpan.FromSeconds(1); public float Volume{get;set;}
        public void Load(string path)=>Loads++; public void Play()=>State=PlayerState.Playing; public void Pause()=>State=PlayerState.Paused; public void Stop()=>State=PlayerState.Stopped; public void Dispose(){}
    }
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var clock=Stopwatch.StartNew(); while(clock.Elapsed<TimeSpan.FromSeconds(6)) { if(await condition())return; await Task.Delay(40); }
        Assert.True(await condition(),"Sincronização não ocorreu dentro do limite do ensaio.");
    }
}
