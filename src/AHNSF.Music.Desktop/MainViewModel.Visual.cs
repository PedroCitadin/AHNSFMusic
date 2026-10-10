using AHNSF.Music.Core;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

// Fase 0.8.3: labels for the redesigned screens. Read-only derivations; no playback, queue or schedule actions.
public partial class MainViewModel
{
    public bool ShowLibraryLoading => Tracks.Count == 0 && IsBusy;
    public string QueueOriginLabel => queue.Origin.Name;
    public string QueueCountLabel => $"{QueueRows.Count:N0} {(QueueRows.Count == 1 ? "música" : "músicas")}";
    public string QueuePositionLabel => queue.Position < 0 || QueueRows.Count == 0 ? "Nenhuma em reprodução" : $"Posição {queue.Position + 1} de {QueueRows.Count}";
    public string ShuffleStateLabel => ShuffleEnabled ? "Aleatório ligado" : "Aleatório desligado";
    public bool RepeatActive => RepeatIndex > 0;
    public string RepeatStateLabel => RepeatOptions[Math.Clamp(RepeatIndex, 0, RepeatOptions.Count - 1)];
    public string PlaylistInitial => string.IsNullOrWhiteSpace(PlaylistTitle) ? "♫" : PlaylistTitle.Trim()[..1].ToUpperInvariant();
    public bool HasCurrentTrack => CurrentTrack is not null;
    public string NowPlayingEyebrow => CurrentTrack is null ? "NADA TOCANDO" : IsPlaying ? "TOCANDO AGORA" : "NO PLAYER";
    /// <summary>Opens a playlist from the dashboard. Selection only: does not load the queue or start playback.</summary>
    [RelayCommand] private void OpenPlaylist(Playlist? playlist)
    {
        if (playlist is null || IsModalOpen) return;
        SelectedPlaylist = playlist; Section = "Playlists";
    }
    // Dependent labels follow the properties the existing partials already raise.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        string[] dependents = e.PropertyName switch
        {
            nameof(ShowEmpty) => [nameof(ShowLibraryLoading)],
            nameof(QueueSummary) => [nameof(QueueOriginLabel), nameof(QueueCountLabel), nameof(QueuePositionLabel)],
            nameof(ShuffleEnabled) => [nameof(ShuffleStateLabel)],
            nameof(RepeatIndex) => [nameof(RepeatStateLabel), nameof(RepeatActive)],
            nameof(PlaylistTitle) => [nameof(PlaylistInitial)],
            nameof(CurrentTrack) => [nameof(HasCurrentTrack), nameof(NowPlayingEyebrow)],
            nameof(IsPlaying) => [nameof(NowPlayingEyebrow)],
            _ => []
        };
        foreach (var property in dependents) base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(property));
    }
}
