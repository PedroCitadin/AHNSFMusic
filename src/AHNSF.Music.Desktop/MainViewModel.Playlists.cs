using System.Collections;
using System.Collections.ObjectModel;
using AHNSF.Music.Core;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

public partial class MainViewModel
{
    private readonly IPlaylistService? playlists;
    private IReadOnlyList<Track> libraryTracks = [];
    private PlaylistDetail? detail;
    private long detailGeneration;
    private bool creatingPlaylist;
    private Guid? deletingPlaylist;
    public bool ShowPlaylists => Section == "Playlists";
    public bool ShowQueue => Section == "Fila";
    public RangeObservableCollection<Playlist> Playlists { get; } = [];
    public RangeObservableCollection<MusicRow> PlaylistRows { get; } = [];
    public RangeObservableCollection<MusicRow> QueueRows { get; } = [];
    public IList SelectedLibraryTracks { get; } = new ObservableCollection<Track>();
    public IList SelectedPlaylistRows { get; } = new ObservableCollection<MusicRow>();
    public IList SelectedQueueRows { get; } = new ObservableCollection<MusicRow>();
    [ObservableProperty] private Playlist? selectedPlaylist;
    [ObservableProperty] private Playlist? targetPlaylist;
    [ObservableProperty] private MusicRow? selectedPlaylistRow;
    [ObservableProperty] private MusicRow? selectedQueueRow;
    [ObservableProperty] private string playlistName = "";
    [ObservableProperty] private string playlistDescription = "";
    [ObservableProperty] private bool playlistDefault;
    [ObservableProperty] private bool isPlaylistEditorVisible;
    [ObservableProperty] private bool deleteConfirmation;
    [ObservableProperty] private bool shuffleEnabled;
    [ObservableProperty] private int repeatIndex;
    public IReadOnlyList<string> RepeatOptions { get; } = ["Repetir: desativado", "Repetir: fila", "Repetir: música"];
    public string PlaylistTitle => detail?.Playlist.Name ?? "Suas playlists";
    public string PlaylistSummary => detail is null ? "Crie uma playlist e adicione músicas pela biblioteca." : $"{detail.Items.Count} músicas • {FormatDuration(TimeSpan.FromTicks(detail.Items.Sum(i => i.Track.Duration.Ticks)))}";
    public string SavedDescription => detail?.Playlist.Description ?? "";
    public bool ShowPlaylistEmpty => PlaylistRows.Count == 0 && !IsLoadingPlaylist;
    public string PlaylistEmptyText => SelectedPlaylist is null ? "Selecione ou crie uma playlist." : "Playlist vazia. Adicione músicas pela Biblioteca.";
    public bool ShowQueueEmpty => QueueRows.Count == 0;
    public string QueueSummary => $"{QueueRows.Count} músicas • origem: {queue.Origin.Name} • posição: {(queue.Position < 0 ? "—" : (queue.Position + 1).ToString())}";
    public string DeletePrompt => $"Excluir a playlist “{SelectedPlaylist?.Name}”? Os arquivos MP3 serão preservados.";
    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    public sealed record MusicRow(Guid Id, Track Track, int Position, bool IsCurrent)
    {
        public int DisplayPosition => Position + 1;
        public string Title => Track.Title;
        public string Artist => Track.Artist;
        public string Album => Track.Album;
        public string DurationText => Track.DurationText;
        public string? ArtworkCacheKey => Track.ArtworkCacheKey;
        public string AvailabilityText => Track.Availability switch { AvailabilityStatus.Missing => "Ausente", AvailabilityStatus.Invalid => "Inválida", _ => "Disponível" };
        public bool IsUnavailable => Track.Availability != AvailabilityStatus.Available;
        public string Subtitle => $"{Track.Artist} • {Track.Album}";
        public Avalonia.Media.FontWeight TitleWeight => IsCurrent ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
    }
    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(SyncPlayback); return; }
        SyncPlayback();
    }
    private void SyncPlayback()
    {
        if (disposed) return;
        updatingScheduleVolume = true; Volume = audio.Volume * 100; updatingScheduleVolume = false;
        var old = CurrentTrack; CurrentTrack = playback.CurrentTrack; IsPlaying = playback.State == PlayerState.Playing;
        Duration = CurrentTrack is null ? 0 : audio.Duration.TotalSeconds;
        if (!ReferenceEquals(old, CurrentTrack)) SetPosition(CurrentTrack is null ? 0 : audio.Position.TotalSeconds);
        if (CurrentTrack is not null && !ReferenceEquals(old, CurrentTrack)) SelectedTrack = Tracks.FirstOrDefault(t => t.Id == CurrentTrack.Id && t.Path == CurrentTrack.Path);
        Status = playback.Message;
        if (playback.LastError is not null) logger.Error(playback.LastError, "Falha no serviço de reprodução");
        RefreshRows(); OnPropertyChanged(nameof(PlaybackText));
        if (presentedSchedule is not null) OnScheduleChanged(this, presentedSchedule); else NotifyPresentation();
    }
    private void RefreshRows()
    {
        var queueId = SelectedQueueRow?.Id; var playlistId = SelectedPlaylistRow?.Id;
        var queueSelected = SelectedQueueRows.Cast<MusicRow>().Select(r => r.Id).ToHashSet();
        var playlistSelected = SelectedPlaylistRows.Cast<MusicRow>().Select(r => r.Id).ToHashSet();
        QueueRows.ReplaceRange(queue.Items.Select((i, n) => new MusicRow(i.Id, i.Track, n, CurrentTrack is not null && queue.CurrentId == i.Id)));
        PlaylistRows.ReplaceRange(detail?.Items.Select(i => new MusicRow(i.Id, i.Track, i.Position,
            CurrentTrack is not null && queue.Origin.PlaylistId == i.PlaylistId && queue.CurrentItem?.PlaylistItemId == i.Id)) ?? []);
        SelectedQueueRows.Clear(); foreach (var row in QueueRows.Where(r => queueSelected.Contains(r.Id))) SelectedQueueRows.Add(row);
        SelectedPlaylistRows.Clear(); foreach (var row in PlaylistRows.Where(r => playlistSelected.Contains(r.Id))) SelectedPlaylistRows.Add(row);
        SelectedQueueRow = QueueRows.FirstOrDefault(r => r.Id == queueId); SelectedPlaylistRow = PlaylistRows.FirstOrDefault(r => r.Id == playlistId);
        OnPropertyChanged(nameof(QueueSummary)); OnPropertyChanged(nameof(ShowQueueEmpty));
        OnPropertyChanged(nameof(PlaylistTitle)); OnPropertyChanged(nameof(PlaylistSummary)); OnPropertyChanged(nameof(SavedDescription));
        OnPropertyChanged(nameof(ShowPlaylistEmpty)); OnPropertyChanged(nameof(PlaylistEmptyText));
    }
    partial void OnShuffleEnabledChanged(bool value) => UpdateModes();
    partial void OnRepeatIndexChanged(int value) => UpdateModes();
    private void UpdateModes()
    {
        ManualIntervention();
        playback.SetModes(ShuffleEnabled, (RepeatMode)Math.Clamp(RepeatIndex, 0, 2));
        settings = settings with { Shuffle = ShuffleEnabled, Repeat = (RepeatMode)Math.Clamp(RepeatIndex, 0, 2) };
        if (initialized) TrackBackground(SaveAsync());
    }
    public async Task ReloadPlaylistsAsync()
    {
        if (playlists is null) return;
        try
        {
            var saved = await playlists.ListAsync(lifetime.Token);
            if (disposed) return;
            var selectedId = SelectedPlaylist?.Id; var targetId = TargetPlaylist?.Id;
            Playlists.ReplaceRange(saved);
            SelectedPlaylist = saved.FirstOrDefault(p => p.Id == selectedId) ?? saved.FirstOrDefault();
            TargetPlaylist = saved.FirstOrDefault(p => p.Id == targetId) ?? saved.FirstOrDefault();
            NotifyPresentation();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Não foi possível carregar as playlists. Consulte os logs."); }
    }
    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        DeleteConfirmation = false; deletingPlaylist = null; creatingPlaylist = false; IsPlaylistEditorVisible = false;
        detail = null; RefreshRows(); TrackBackground(SelectPlaylistAsync());
    }
    private async Task SelectPlaylistAsync()
    {
        try { await ReloadPlaylistDetailAsync(lifetime.Token, true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Não foi possível abrir a playlist."); }
    }
    public async Task ReloadPlaylistDetailAsync(CancellationToken token = default, bool updateEditor = false)
    {
        if (playlists is null || SelectedPlaylist is null || disposed) return;
        if (token == default) token = lifetime.Token;
        var id = SelectedPlaylist.Id; var revision = ++detailGeneration;
        IsLoadingPlaylist = true;
        PlaylistDetail? result;
        try { result = await playlists.GetAsync(id, token); }
        finally { if (revision == detailGeneration && !disposed) IsLoadingPlaylist = false; }
        if (disposed || revision != detailGeneration || SelectedPlaylist?.Id != id) return;
        detail = result;
        if (updateEditor && detail is not null)
        { PlaylistName = detail.Playlist.Name; PlaylistDescription = detail.Playlist.Description; PlaylistDefault = detail.Playlist.IsDefault; }
        RefreshRows();
    }
    private Task PlaylistActionAsync(Func<Task> action)
    {
        var task = PlaylistActionCoreAsync(action); TrackBackground(task); return task;
    }
    private async Task PlaylistActionCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (ArgumentException ex) { Status = ex.Message; }
        catch (KeyNotFoundException ex) { Status = ex.Message; }
        catch (Exception ex) { Error(ex, "Não foi possível salvar a alteração. A playlist foi preservada; consulte os logs."); }
    }
    [RelayCommand] private void NewPlaylist()
    { creatingPlaylist = true; PlaylistName = ""; PlaylistDescription = ""; PlaylistDefault = false; IsPlaylistEditorVisible = true; DeleteConfirmation = false; }
    [RelayCommand] private void EditPlaylist()
    {
        if (detail is null) { Status = "Selecione uma playlist."; return; }
        creatingPlaylist = false; PlaylistName = detail.Playlist.Name; PlaylistDescription = detail.Playlist.Description; PlaylistDefault = detail.Playlist.IsDefault; IsPlaylistEditorVisible = true;
    }
    [RelayCommand] private void CancelPlaylistEdit() => IsPlaylistEditorVisible = false;
    [RelayCommand] private Task SavePlaylistAsync() => PlaylistActionAsync(async () =>
    {
        if (playlists is null) return;
        Guid id;
        if (creatingPlaylist)
        {
            var created = await playlists.CreateAsync(PlaylistName, PlaylistDescription, lifetime.Token); id = created.Id;
            if (PlaylistDefault) await playlists.UpdateAsync(id, created.Name, created.Description, true, lifetime.Token);
        }
        else { if (SelectedPlaylist is null) return; id = SelectedPlaylist.Id; await playlists.UpdateAsync(id, PlaylistName, PlaylistDescription, PlaylistDefault, lifetime.Token); }
        await ReloadPlaylistsAsync(); SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == id);
        await ReloadPlaylistDetailAsync(lifetime.Token, true); IsPlaylistEditorVisible = false; creatingPlaylist = false;
        Status = "Playlist salva.";
    });
    [RelayCommand] private void RequestDeletePlaylist()
    {
        if (SelectedPlaylist is null) { Status = "Selecione uma playlist."; return; }
        deletingPlaylist = SelectedPlaylist.Id; DeleteConfirmation = true; OnPropertyChanged(nameof(DeletePrompt));
    }
    [RelayCommand] private void CancelDeletePlaylist() { deletingPlaylist = null; DeleteConfirmation = false; }
    [RelayCommand] private Task ConfirmDeletePlaylistAsync() => PlaylistActionAsync(async () =>
    {
        if (playlists is null || deletingPlaylist is not Guid id || !DeleteConfirmation) return;
        await playlists.DeleteAsync(id, lifetime.Token); deletingPlaylist = null; DeleteConfirmation = false;
        await ReloadPlaylistsAsync(); detail = null; await ReloadPlaylistDetailAsync(lifetime.Token, true); RefreshRows();
        Status = "Playlist excluída. A fila ativa e os arquivos foram preservados.";
    });
    private Track[] LibrarySelection() => SelectedLibraryTracks.Cast<Track>().ToArray() is { Length: > 0 } selected ? selected : SelectedTrack is null ? [] : [SelectedTrack];
    [RelayCommand] private Task AddToPlaylistAsync() => PlaylistActionAsync(async () =>
    {
        if (playlists is null || TargetPlaylist is null) { Status = "Crie uma playlist e escolha o destino."; return; }
        var selected = LibrarySelection(); if (selected.Length == 0) { Status = "Selecione músicas na biblioteca."; return; }
        await playlists.AddAsync(TargetPlaylist.Id, selected.Select(t => t.Id).ToArray(), lifetime.Token);
        await ReloadPlaylistDetailAsync(lifetime.Token); Status = $"{selected.Length} música(s) adicionada(s) a {TargetPlaylist.Name}.";
    });
    private Guid[] PlaylistSelection() => SelectedPlaylistRows.Cast<MusicRow>().Select(r => r.Id).ToArray() is { Length: > 0 } ids ? ids : SelectedPlaylistRow is null ? [] : [SelectedPlaylistRow.Id];
    [RelayCommand] private Task RemovePlaylistItemsAsync() => PlaylistActionAsync(async () =>
    {
        if (playlists is null || SelectedPlaylist is null) return;
        var ids = PlaylistSelection(); if (ids.Length == 0) { Status = "Selecione itens para remover."; return; }
        await playlists.RemoveAsync(SelectedPlaylist.Id, ids, lifetime.Token); await ReloadPlaylistDetailAsync(lifetime.Token);
        Status = "Itens removidos. Biblioteca e fila ativa preservadas.";
    });
    [RelayCommand] private Task MovePlaylistUpAsync() => MovePlaylistAsync(-1);
    [RelayCommand] private Task MovePlaylistDownAsync() => MovePlaylistAsync(1);
    private Task MovePlaylistAsync(int direction) => PlaylistActionAsync(async () =>
    {
        if (playlists is null || detail is null || SelectedPlaylistRow is null) return;
        var ids = detail.Items.Select(i => i.Id).ToList(); var index = ids.IndexOf(SelectedPlaylistRow.Id); var target = index + direction;
        if (index < 0 || target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        await playlists.ReorderAsync(detail.Playlist.Id, ids, lifetime.Token); await ReloadPlaylistDetailAsync(lifetime.Token);
        Status = "Nova ordem salva.";
    });
    public Task MovePlaylistItemToAsync(Guid playlistId, Guid source, Guid target) => PlaylistActionAsync(async () =>
    {
        if (playlists is null || detail?.Playlist.Id != playlistId) return;
        var ids = detail.Items.Select(i => i.Id).ToList(); var from = ids.IndexOf(source); var to = ids.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        ids.RemoveAt(from); ids.Insert(to, source);
        await playlists.ReorderAsync(playlistId, ids, lifetime.Token); await ReloadPlaylistDetailAsync(lifetime.Token);
        Status = "Nova ordem salva.";
    });
    public void ReportPlaylistInteractionError(Exception ex) => Error(ex, "Não foi possível concluir o arraste. Use Mover para cima ou Mover para baixo.");
    [RelayCommand] private Task PlayPlaylistAsync() => LoadPlaylistAsync(false);
    [RelayCommand] private Task ShufflePlaylistAsync() => LoadPlaylistAsync(true);
    private Task LoadPlaylistAsync(bool shuffled) => PlaylistActionAsync(async () =>
    {
        ManualIntervention();
        if (SelectedPlaylist is null) { Status = "Selecione uma playlist."; return; }
        ShuffleEnabled = shuffled; await playback.LoadPlaylistAsync(SelectedPlaylist.Id, true, token: lifetime.Token); SyncPlayback();
    });
    [RelayCommand] private Task PlayPlaylistItemAsync() => PlaylistActionAsync(async () =>
    {
        ManualIntervention();
        if (SelectedPlaylist is null || SelectedPlaylistRow is null) return;
        if (!PlaybackQueue.IsPlayable(SelectedPlaylistRow.Track)) { Status = "Música indisponível. A referência permanece na playlist."; return; }
        if (queue.Origin.PlaylistId == SelectedPlaylist.Id && queue.Items.FirstOrDefault(i => i.PlaylistItemId == SelectedPlaylistRow.Id) is { } occurrence)
            playback.PlayOccurrence(occurrence.Id);
        else await playback.LoadPlaylistAsync(SelectedPlaylist.Id, true, SelectedPlaylistRow.Id, lifetime.Token);
        SyncPlayback();
    });
    [RelayCommand] private void AddToQueue() { ManualIntervention(); foreach (var track in LibrarySelection()) playback.Add(track); }
    [RelayCommand] private void AddAsNext() { ManualIntervention(); foreach (var track in LibrarySelection().Reverse()) playback.Add(track, true); }
    [RelayCommand] private void PlayQueueItem() { ManualIntervention(); if (SelectedQueueRow is not null) playback.PlayOccurrence(SelectedQueueRow.Id); }
    [RelayCommand] private void RemoveQueueItems()
    {
        ManualIntervention();
        var ids = SelectedQueueRows.Cast<MusicRow>().Select(r => r.Id).ToArray();
        if (ids.Length == 0 && SelectedQueueRow is not null) ids = [SelectedQueueRow.Id];
        playback.Remove(ids);
    }
    [RelayCommand] private void ClearQueue() { ManualIntervention(); playback.Clear(); }
}
