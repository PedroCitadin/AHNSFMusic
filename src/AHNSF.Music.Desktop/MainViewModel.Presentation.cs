using AHNSF.Music.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

// Presentation derives from existing services. It never issues playback or schedule actions.
public partial class MainViewModel
{
    private ScheduleStatus? presentedSchedule;
    [ObservableProperty] private int libraryTotal;
    [ObservableProperty] private bool isLoadingPlaylist;
    public bool ShowHome => Section == "Início";
    public bool IsLibrarySection => Section == "Biblioteca";
    public bool IsPlaylistSection => Section == "Playlists";
    public bool IsQueueSection => Section == "Fila";
    public bool IsScheduleSection => Section == "Programação";
    public bool IsSettingsSection => Section == "Configurações";
    public string PageTitle => ShowHome ? "Painel operacional" : Section;
    public string PageDescription => Section switch
    {
        "Início" => "Tudo o que você precisa acompanhar, em um só lugar.",
        "Biblioteca" => "Suas músicas locais, organizadas e sempre disponíveis para consulta.",
        "Playlists" => "Organize o repertório. A fila em reprodução permanece independente.",
        "Fila" => "A sequência desta sessão. Alterações não modificam playlists salvas.",
        "Histórico" => "Eventos, sessões e tempo de reprodução observados neste computador.",
        "Importar músicas" => "Cole o link de uma playlist, confira as músicas e baixe para a pasta musical, sem interromper o player.",
        "Programação" => "Veja quando o som estará ligado e configure os horários da semana.",
        _ => "Preferências locais e manutenção da biblioteca."
    };
    public string PlayerStateLabel => playback.State switch { PlayerState.Playing => "Reproduzindo", PlayerState.Paused => "Pausado", _ => "Parado" };
    public string PlayerStateIcon => playback.State switch { PlayerState.Playing => "▶", PlayerState.Paused => "Ⅱ", _ => "■" };
    /// <summary>Semantic tone, resolved to the active theme brush by ToneBrushConverter.</summary>
    public string PlayerStateColor => playback.LastError is not null ? "Error" : playback.State == PlayerState.Playing ? "AccentText" : "TextSecondary";
    public string ActiveOrigin => CurrentTrack is null ? "Nenhuma sessão em reprodução" : queue.Origin.Name;
    public string QueueOrigin => queue.Origin.Name;
    public string ModeLabel => presentedSchedule?.Mode == OperationMode.Automatic ? "Automático" : "Manual";
    public string AutomationLabel => presentedSchedule?.IsSuspended == true ? "Ⅱ Automação suspensa até a próxima transição" : presentedSchedule?.Mode == OperationMode.Automatic ? "◷ Programação automática ativada" : "Controle manual do player";
    public string RuleLabel => presentedSchedule?.Evaluation.Winner?.Rule.Name ?? "Nenhuma regra vigente";
    public string RuleContext => presentedSchedule?.Mode == OperationMode.Automatic && presentedSchedule?.IsSuspended != true ? "REGRA VIGENTE" : "REGRA PREVISTA";
    public string NextAction => presentedSchedule?.Evaluation.NextTransitionUtc is { } next
        ? $"{(presentedSchedule.Evaluation.NextWinner is { } winner ? $"Iniciar {winner.Rule.Name}" : "Encerrar reprodução")} • {TimeZoneInfo.ConvertTime(next, TimeZoneInfo.Local):ddd dd/MM HH:mm}"
        : "Nenhuma transição programada";
    public string ExpectedEnd => presentedSchedule?.Evaluation.Winner is { } winner
        ? $"Janela vigente até {TimeZoneInfo.ConvertTime(winner.EndUtc, TimeZoneInfo.Local):ddd dd/MM HH:mm}" : "Sem horário de encerramento previsto";
    public int PlaylistTotal => Playlists.Count;
    public string LibraryTotalLabel => $"{LibraryTotal:N0} {(LibraryTotal == 1 ? "música" : "músicas")}";
    public string PlaylistTotalLabel => $"{PlaylistTotal:N0} {(PlaylistTotal == 1 ? "coleção" : "coleções")}";
    public string LibraryHealth => IsBusy ? "↻ Indexação em andamento" : !DirectoryAvailable ? "⚠ Biblioteca indisponível" : settings.MusicDirectory is null ? "○ Pasta não configurada" : "✓ Biblioteca disponível";
    public bool HasOperationalAlert => OperationalAlert.Length > 0;
    public string OperationalAlert => AudioDeviceNeedsAttention ? AudioDeviceMessage : playback.LastError is not null ? "Falha de reprodução. Verifique o arquivo e a saída de áudio; consulte os logs."
        : !DirectoryAvailable ? "A pasta musical está indisponível. O catálogo foi preservado; verifique o acesso à pasta."
        : ScheduleWarning.Length > 0 ? ScheduleWarning : presentedSchedule?.IsSuspended == true ? "A automação está suspensa por intervenção manual. Retomará na próxima mudança da regra vencedora."
        : Rules.Any(r => r.PlaylistId is null) ? "Uma regra está sem playlist. Associe uma playlist válida em Programação."
        : "";
    public bool HasFeedback => Status.Length > 0;
    public bool HasNoPlaylists => Playlists.Count == 0;
    public bool HasNoRules => Rules.Count == 0;
    public string ResultsText => $"{Tracks.Count:N0} resultado(s) • {LibraryTotal:N0} músicas na biblioteca";
    public string VolumeLabel => $"Volume • {Volume:0}%";
    public string RuleDeletePrompt => $"Excluir a regra “{SelectedRule?.Name}”? A playlist e os arquivos serão preservados.";
    private void NotifyPresentation()
    {
        UpdateDiagnostics();
        NotifyAudioDevicePresentation();
        foreach (var property in new[] { nameof(PlayerStateLabel), nameof(PlayerStateIcon), nameof(PlayerStateColor), nameof(ActiveOrigin), nameof(QueueOrigin), nameof(ModeLabel), nameof(AutomationLabel), nameof(RuleLabel), nameof(RuleContext), nameof(NextAction), nameof(ExpectedEnd), nameof(PlaylistTotal), nameof(LibraryTotalLabel), nameof(PlaylistTotalLabel), nameof(LibraryHealth), nameof(OperationalAlert), nameof(HasOperationalAlert), nameof(HasNoPlaylists), nameof(HasNoRules), nameof(ResultsText), nameof(VolumeLabel), nameof(RuleDeletePrompt) }) OnPropertyChanged(property);
    }
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasFeedback));
    partial void OnScheduleWarningChanged(string value) { OnPropertyChanged(nameof(OperationalAlert)); OnPropertyChanged(nameof(HasOperationalAlert)); }
    partial void OnIsLoadingPlaylistChanged(bool value) => OnPropertyChanged(nameof(ShowPlaylistEmpty));
    partial void OnLibraryTotalChanged(int value) { OnPropertyChanged(nameof(ResultsText)); OnPropertyChanged(nameof(LibraryTotalLabel)); }
    [RelayCommand] private void ClearSearch() { SearchText = ""; SelectedArtist = ArtistOptions.FirstOrDefault(); SelectedAlbum = AlbumOptions.FirstOrDefault(); }
    public bool HasSearchCriteria => SearchText.Length > 0 || SelectedArtist?.Value is not null || SelectedAlbum?.Value is not null;
    public string LibrarySelectionText => SelectionText(SelectedLibraryTracks.Count, "música selecionada", "músicas selecionadas");
    public string PlaylistSelectionText => SelectedPlaylistRows.Count == 0 ? "Arraste uma linha sobre outra para reordenar." : SelectionText(SelectedPlaylistRows.Count, "ocorrência selecionada", "ocorrências selecionadas");
    public string QueueSelectionText => SelectedQueueRows.Count == 0 ? "Duplo clique reproduz uma ocorrência." : SelectionText(SelectedQueueRows.Count, "ocorrência selecionada", "ocorrências selecionadas");
    private static string SelectionText(int count, string singular, string plural) => count switch { 0 => "", 1 => $"1 {singular}", _ => $"{count:N0} {plural}" };
    private void InitializePresentation()
    {
        // Selection lists are bound by ListBox.SelectedItems; summaries follow them without touching playback.
        ((System.Collections.Specialized.INotifyCollectionChanged)SelectedLibraryTracks).CollectionChanged += (_, _) => OnPropertyChanged(nameof(LibrarySelectionText));
        ((System.Collections.Specialized.INotifyCollectionChanged)SelectedPlaylistRows).CollectionChanged += (_, _) => OnPropertyChanged(nameof(PlaylistSelectionText));
        ((System.Collections.Specialized.INotifyCollectionChanged)SelectedQueueRows).CollectionChanged += (_, _) => OnPropertyChanged(nameof(QueueSelectionText));
    }
}

