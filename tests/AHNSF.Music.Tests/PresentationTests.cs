using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Infrastructure;
using Xunit;

namespace AHNSF.Music.Tests;

// Fase 0.5: presentation state derived by MainViewModel; no visual tree is required.
public sealed class PresentationTests
{
    private sealed class Scanner : IMusicScanner
    { public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new ScanResult([], [])); }
    private static async Task<(LibraryFixture Fixture, SqlitePlaylistService Playlists, MainViewModel ViewModel, QueueTests.TestAudio Audio)> CreateAsync(params string[] titles)
    {
        var f = new LibraryFixture();
        for (var i = 0; i < titles.Length; i++) f.WriteTrack($"{(char)('a' + i)}.mp3", titles[i]);
        if (titles.Length > 0) await f.StartAsync();
        var s = new SqlitePlaylistService(f.Database, f.Repository); var store = new SqliteSettingsStore(f.Database);
        if (titles.Length > 0) await store.SaveAsync(new(f.Music));
        var audio = new QueueTests.TestAudio();
        var vm = new MainViewModel(audio, new Scanner(), store, f.Logger, f.Service, s);
        await vm.InitializeAsync();
        return (f, s, vm, audio);
    }

    [Fact] public async Task NavigationExposesExactlyOneActiveSectionWithoutTouchingPlayback()
    {
        var (f, _, vm, _) = await CreateAsync("A");
        await using (f) await using (vm)
        {
            Assert.True(vm.ShowHome); Assert.Equal("Painel operacional", vm.PageTitle);
            foreach (var section in new[] { "Biblioteca", "Playlists", "Fila", "Programação", "Configurações", "Início" })
            {
                vm.NavigateCommand.Execute(section);
                var flags = new[] { vm.ShowHome, vm.IsLibrarySection, vm.IsPlaylistSection, vm.IsQueueSection, vm.IsScheduleSection, vm.IsSettingsSection };
                Assert.Single(flags, flag => flag);
                Assert.Equal(vm.IsLibrarySection, vm.ShowLibrary); Assert.Equal(vm.IsPlaylistSection, vm.ShowPlaylists); Assert.Equal(vm.IsQueueSection, vm.ShowQueue);
                Assert.Equal(vm.IsScheduleSection, vm.ShowSchedule); Assert.Equal(vm.IsSettingsSection, vm.ShowSettings);
                Assert.False(string.IsNullOrWhiteSpace(vm.PageDescription));
            }
            Assert.Null(vm.CurrentTrack); Assert.False(vm.IsPlaying); Assert.Equal("Parado", vm.PlayerStateLabel); Assert.Equal("Manual", vm.ModeLabel);
        }
    }

    [Fact] public async Task ResultsTextAndClearSearchFollowFiltersWithoutChangingTotals()
    {
        var (f, _, vm, _) = await CreateAsync("Alfa", "Beta");
        await using (f) await using (vm)
        {
            Assert.False(vm.HasSearchCriteria); Assert.Equal(2, vm.LibraryTotal); Assert.StartsWith("2 resultado(s)", vm.ResultsText);
            var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.SearchText = "Beta"; Assert.True(vm.HasSearchCriteria); Assert.Contains(nameof(MainViewModel.HasSearchCriteria), changed);
            await vm.LoadCatalogAsync(false);
            Assert.Equal("Beta", Assert.Single(vm.Tracks).Title); Assert.Equal(2, vm.LibraryTotal); Assert.StartsWith("1 resultado(s) • 2 músicas", vm.ResultsText);
            vm.ClearSearchCommand.Execute(null); Assert.False(vm.HasSearchCriteria); Assert.Equal("", vm.SearchText);
            await vm.LoadCatalogAsync(false); Assert.Equal(2, vm.Tracks.Count);
        }
    }

    [Fact] public async Task SelectionSummariesFollowBoundSelections()
    {
        var (f, s, vm, _) = await CreateAsync("A", "B");
        await using (f) await using (vm)
        {
            Assert.Equal("", vm.LibrarySelectionText);
            var raised = 0; vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.LibrarySelectionText)) raised++; };
            vm.SelectedLibraryTracks.Add(vm.Tracks[0]); Assert.Equal("1 música selecionada", vm.LibrarySelectionText);
            vm.SelectedLibraryTracks.Add(vm.Tracks[1]); Assert.Equal("2 músicas selecionadas", vm.LibrarySelectionText); Assert.Equal(2, raised);
            vm.AddToQueueCommand.Execute(null); Assert.Contains("Duplo clique", vm.QueueSelectionText);
            vm.SelectedQueueRows.Add(vm.QueueRows[0]); Assert.Equal("1 ocorrência selecionada", vm.QueueSelectionText);
            var p = await s.CreateAsync("P"); await vm.ReloadPlaylistsAsync(); vm.SelectedPlaylist = vm.Playlists.Single(x => x.Id == p.Id); Assert.Contains("Arraste", vm.PlaylistSelectionText);
        }
    }

    [Fact] public async Task DestructiveQueueActionsWaitForExplicitConfirmation()
    {
        var (f, _, vm, _) = await CreateAsync("A", "B");
        await using (f) await using (vm)
        {
            vm.SelectedLibraryTracks.Add(vm.Tracks[0]); vm.SelectedLibraryTracks.Add(vm.Tracks[1]); vm.AddToQueueCommand.Execute(null);
            var count = vm.QueueRows.Count; Assert.True(count >= 2);
            vm.RequestClearQueueCommand.Execute(null); Assert.True(vm.DestructiveConfirmation); Assert.Equal(count, vm.QueueRows.Count);
            vm.CancelDestructiveCommand.Execute(null); Assert.False(vm.DestructiveConfirmation);
            await vm.ConfirmDestructiveCommand.ExecuteAsync(null); Assert.Equal(count, vm.QueueRows.Count);
            var removed = vm.QueueRows[0].Id; vm.SelectedQueueRows.Add(vm.QueueRows[0]); vm.RequestRemoveQueueItemsCommand.Execute(null); Assert.Equal(count, vm.QueueRows.Count);
            await vm.ConfirmDestructiveCommand.ExecuteAsync(null); Assert.Equal(count - 1, vm.QueueRows.Count); Assert.DoesNotContain(vm.QueueRows, r => r.Id == removed);
            vm.RequestClearQueueCommand.Execute(null); await vm.ConfirmDestructiveCommand.ExecuteAsync(null); Assert.Empty(vm.QueueRows); Assert.True(vm.ShowQueueEmpty);
        }
    }

    [Fact] public async Task PlaylistItemRemovalWaitsForConfirmationAndKeepsLibrary()
    {
        var (f, s, vm, _) = await CreateAsync("A", "B");
        await using (f) await using (vm)
        {
            var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, vm.Tracks.Select(t => t.Id).ToArray());
            await vm.ReloadPlaylistsAsync(); vm.SelectedPlaylist = vm.Playlists.Single(x => x.Id == p.Id); await vm.ReloadPlaylistDetailAsync();
            vm.SelectedPlaylistRows.Add(vm.PlaylistRows[0]); vm.RequestRemovePlaylistItemsCommand.Execute(null);
            Assert.True(vm.DestructiveConfirmation); Assert.Equal(2, (await s.GetAsync(p.Id))!.Items.Count);
            await vm.ConfirmDestructiveCommand.ExecuteAsync(null);
            Assert.Single((await s.GetAsync(p.Id))!.Items); Assert.Single(vm.PlaylistRows); Assert.Equal(2, vm.LibraryTotal);
        }
    }

    [Fact] public async Task OperationalAlertsAndRowsDescribeAvailability()
    {
        var (f, _, vm, _) = await CreateAsync("A");
        await using (f) await using (vm)
        {
            Assert.False(vm.HasOperationalAlert); Assert.Equal("✓ Biblioteca disponível", vm.LibraryHealth);
            vm.DirectoryAvailable = false; Assert.True(vm.HasOperationalAlert); Assert.Contains("indisponível", vm.OperationalAlert); Assert.StartsWith("⚠", vm.LibraryHealth);
            var track = vm.Tracks[0];
            var missing = new MainViewModel.MusicRow(Guid.NewGuid(), track with { Availability = AvailabilityStatus.Missing }, 0, false);
            var current = new MainViewModel.MusicRow(Guid.NewGuid(), track, 1, true);
            Assert.True(missing.IsUnavailable); Assert.Equal("Ausente", missing.AvailabilityText); Assert.Equal(1, missing.DisplayPosition);
            Assert.False(current.IsUnavailable); Assert.Equal($"{track.Artist} • {track.Album}", current.Subtitle); Assert.Equal(Avalonia.Media.FontWeight.SemiBold, current.TitleWeight);
        }
    }
}
