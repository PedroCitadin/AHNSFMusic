using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class PlaylistViewModelTests
{
    private sealed class Scanner : IMusicScanner
    { public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new ScanResult([], [])); }
    private static MainViewModel ViewModel(LibraryFixture f, IPlaylistService s, ISettingsStore? store = null) => new(new QueueTests.TestAudio(), new Scanner(), store ?? new SqliteSettingsStore(f.Database), f.Logger, f.Service, s);
    [Fact] public async Task InitializePreservesBothPlaybackPreferencesWithoutAutoplay()
    {
        await using var f = new LibraryFixture(); var s = new SqlitePlaylistService(f.Database, f.Repository); var store = new SqliteSettingsStore(f.Database);
        await store.SaveAsync(new(Volume: .22f, Shuffle: true, Repeat: RepeatMode.Track));
        await using var vm = ViewModel(f, s, store); await vm.InitializeAsync();
        Assert.True(vm.ShuffleEnabled); Assert.Equal(2, vm.RepeatIndex); Assert.False(vm.IsPlaying); Assert.Null(vm.CurrentTrack);
        await vm.SaveWindowAsync(1100, 760, 1, 2); var saved = await store.LoadAsync(); Assert.True(saved.Shuffle); Assert.Equal(RepeatMode.Track, saved.Repeat); Assert.Equal(.22f, saved.Volume);
    }
    [Fact] public async Task CreateValidateEditAndDeleteRequiresExplicitConfirmationCommand()
    {
        await using var f = new LibraryFixture(); var s = new SqlitePlaylistService(f.Database, f.Repository); await using var vm = ViewModel(f, s);
        await vm.InitializeAsync(); vm.NewPlaylistCommand.Execute(null); await vm.SavePlaylistCommand.ExecuteAsync(null); Assert.Empty(await s.ListAsync()); Assert.Contains("nome", vm.Status);
        vm.PlaylistName = "Nova"; vm.PlaylistDescription = "Descrição"; vm.PlaylistDefault = true; await vm.SavePlaylistCommand.ExecuteAsync(null);
        var p = Assert.Single(await s.ListAsync()); Assert.True(p.IsDefault); vm.SelectedPlaylist = p; await vm.ReloadPlaylistDetailAsync(updateEditor: true);
        vm.EditPlaylistCommand.Execute(null); vm.PlaylistName = "Renomeada"; await vm.SavePlaylistCommand.ExecuteAsync(null); Assert.Equal("Renomeada", (await s.GetAsync(p.Id))!.Playlist.Name);
        await vm.ConfirmDeletePlaylistCommand.ExecuteAsync(null); Assert.NotNull(await s.GetAsync(p.Id));
        vm.RequestDeletePlaylistCommand.Execute(null); Assert.True(vm.DeleteConfirmation); vm.CancelDeletePlaylistCommand.Execute(null); await vm.ConfirmDeletePlaylistCommand.ExecuteAsync(null); Assert.NotNull(await s.GetAsync(p.Id));
        vm.RequestDeletePlaylistCommand.Execute(null); await vm.ConfirmDeletePlaylistCommand.ExecuteAsync(null); Assert.Null(await s.GetAsync(p.Id));
    }
    [Fact] public async Task MultiSelectionAddsAndRemovesSpecificOccurrencesAndMovesOrder()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B"); var tracks = (await f.StartAsync()).Tracks;
        var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await using var vm = ViewModel(f, s); await vm.InitializeAsync(); vm.TargetPlaylist = p; vm.SelectedPlaylist = p;
        vm.SelectedLibraryTracks.Add(tracks[0]); vm.SelectedLibraryTracks.Add(tracks[1]); await vm.AddToPlaylistCommand.ExecuteAsync(null); await vm.AddToPlaylistCommand.ExecuteAsync(null);
        await vm.ReloadPlaylistDetailAsync(); Assert.Equal(4, vm.PlaylistRows.Count); var first = vm.PlaylistRows[0]; var last = vm.PlaylistRows[^1];
        await vm.MovePlaylistItemToAsync(p.Id, first.Id, last.Id); Assert.Equal(first.Id, vm.PlaylistRows[^1].Id);
        vm.SelectedPlaylistRow = vm.PlaylistRows[^1]; await vm.MovePlaylistUpCommand.ExecuteAsync(null); Assert.Equal(first.Id, vm.PlaylistRows[^2].Id);
        vm.SelectedPlaylistRows.Add(vm.PlaylistRows[0]); vm.SelectedPlaylistRows.Add(vm.PlaylistRows[1]); await vm.RemovePlaylistItemsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.PlaylistRows.Count); Assert.Equal(new[] { 0, 1 }, vm.PlaylistRows.Select(r => r.Position));
    }
    [Fact] public async Task LibraryReconciliationPreservesActivePlaylistQueueAndHiddenPausedTrack()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B"); var tracks = (await f.StartAsync()).Tracks;
        var s = new SqlitePlaylistService(f.Database, f.Repository); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [tracks[1].Id, tracks[0].Id, tracks[1].Id]);
        var store = new SqliteSettingsStore(f.Database); await store.SaveAsync(new(f.Music)); await using var vm = ViewModel(f, s, store); await vm.InitializeAsync(); vm.SelectedPlaylist = p; await vm.ReloadPlaylistDetailAsync();
        vm.Section = "Playlists"; await vm.PlayPlaylistCommand.ExecuteAsync(null); var current = vm.CurrentTrack; Assert.Equal("B", current!.Title);
        vm.PlayPauseCommand.Execute(null); vm.Section = "Biblioteca"; vm.SearchText = "a.mp3"; await vm.LoadCatalogAsync(false); Assert.Null(vm.SelectedTrack);
        await vm.LoadCatalogAsync(true); Assert.Equal(3, vm.QueueRows.Count); Assert.Same(current, vm.CurrentTrack); vm.PlayPauseCommand.Execute(null); Assert.Same(current, vm.CurrentTrack);
        vm.NextCommand.Execute(null); Assert.Equal("A", vm.CurrentTrack!.Title);
    }
    [Fact] public async Task QueueAppendSelectionPreservesOrderAndCurrentOccurrence()
    {
        await using var f = new LibraryFixture(); f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B"); var tracks = (await f.StartAsync()).Tracks;
        var s = new SqlitePlaylistService(f.Database, f.Repository); await using var vm = ViewModel(f, s); await vm.InitializeAsync();
        vm.SelectedLibraryTracks.Add(tracks[0]); vm.SelectedLibraryTracks.Add(tracks[1]); vm.AddToQueueCommand.Execute(null);
        Assert.Equal(new[] { "A", "B" }, vm.QueueRows.Select(r => r.Title)); vm.Section = "Fila"; vm.SelectedQueueRow = vm.QueueRows[0]; vm.PlayQueueItemCommand.Execute(null); var current = vm.CurrentTrack;
        vm.AddAsNextCommand.Execute(null); Assert.Equal(new[] { "A", "A", "B", "B" }, vm.QueueRows.Select(r => r.Title)); Assert.Same(current, vm.CurrentTrack);
        vm.NextCommand.Execute(null); Assert.Equal("A", vm.CurrentTrack!.Title); vm.NextCommand.Execute(null); Assert.Equal("B", vm.CurrentTrack!.Title);
    }
}

