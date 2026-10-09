using System.Collections.ObjectModel;
using AHNSF.Music.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AHNSF.Music.Desktop;

public partial class MainViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly IAudioEngine audio;
    private readonly IMusicScanner scanner;
    private readonly ISettingsStore store;
    private readonly ILogger logger;
    private readonly ILibraryService? library;
    private CancellationTokenSource? searching;
    private long directoryGeneration;
    private long queryGeneration;
    private readonly PlaybackService playback;
    private PlaybackQueue queue => playback.Queue;
    private readonly DispatcherTimer timer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim saving = new(1, 1);
    private readonly HashSet<Task> backgroundTasks = [];
    private AppSettings settings = new();
    private bool updatingPosition;
    private bool initialized;
    private bool disposed;
    public Func<Task<string?>>? PickDirectory { get; set; }
    public RangeObservableCollection<Track> Tracks { get; } = [];
    public sealed record FilterOption(string? Value, string Label);
    public IReadOnlyList<string> SortOptions { get; } = ["Título", "Artista", "Álbum", "Duração", "Data de inclusão"];
    [ObservableProperty] private IReadOnlyList<FilterOption> artistOptions = [new(null, "Todos os artistas")];
    [ObservableProperty] private IReadOnlyList<FilterOption> albumOptions = [new(null, "Todos os álbuns")];
    [ObservableProperty] private FilterOption? selectedArtist;
    [ObservableProperty] private FilterOption? selectedAlbum;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private bool descending;
    [ObservableProperty] private string libraryStatistics = "0 músicas • 0 artistas • 0 álbuns";
    [ObservableProperty] private string libraryStatus = "Selecione uma pasta com músicas MP3 para começar.";
    [ObservableProperty] private bool directoryAvailable = true;
    public bool ShowEmpty => Tracks.Count == 0 && !IsBusy;
    public string EmptyText => settings.MusicDirectory is null ? "Selecione uma pasta de músicas." : !DirectoryAvailable ? "Pasta indisponível. O catálogo foi preservado." : SearchText.Length > 0 || SelectedArtist?.Value is not null || SelectedAlbum?.Value is not null ? "Nenhum resultado encontrado." : "Sua biblioteca ainda não contém músicas MP3.";
    [ObservableProperty] private Track? selectedTrack;
    [ObservableProperty] private Track? currentTrack;
    [ObservableProperty] private string musicDirectory = "Nenhuma pasta selecionada";
    [ObservableProperty] private string status = "Selecione uma pasta com músicas MP3 para começar.";
    [ObservableProperty] private string section = "Início";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private double volume = 35;
    [ObservableProperty] private double position;
    [ObservableProperty] private double duration;
    public bool ShowLibrary => Section == "Biblioteca";
    public bool ShowSettings => Section == "Configurações";
    public bool ShowSchedule => Section == "Programação";
    public string CurrentTitle => CurrentTrack?.Title ?? "Nenhuma música carregada";
    public string CurrentArtist => CurrentTrack?.Artist ?? "Reprodução local • Offline";
    public string PlaybackText => CurrentTrack is null ? "Player parado" : $"{(IsPlaying ? "Reproduzindo" : audio.State == PlayerState.Paused ? "Pausado" : "Parado")}: {CurrentTitle}";
    public string PlayLabel => IsPlaying ? "Pausar" : "Reproduzir";
    public string PositionText => TimeSpan.FromSeconds(Position).ToString(@"mm\:ss");
    public string DurationText => TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss");
    public string CountText => $"{Tracks.Count} músicas locais";
    public bool CanSeek => CurrentTrack is not null && Duration > 0;
    public AppSettings Settings => settings;

    public MainViewModel(IAudioEngine audio, IMusicScanner scanner, ISettingsStore store, ILogger logger, ILibraryService? library = null,
        IPlaylistService? playlists = null, PlaybackService? playback = null, IScheduleService? scheduleStore = null, ScheduleCoordinator? scheduler = null)
    {
        this.audio = audio; this.scanner = scanner; this.store = store; this.logger = logger;
        this.library = library;
        this.playlists = playlists;
        this.scheduleStore = scheduleStore; this.scheduler = scheduler;
        if (scheduler is not null) scheduler.Changed += OnScheduleChanged;
        this.playback = playback ?? new PlaybackService(audio, playlists, action => Dispatcher.UIThread.Post(action));
        this.playback.Changed += OnPlaybackChanged;
        if (library is not null) library.Changed += OnLibraryChanged;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        timer.Stop();
        InitializePresentation();
    }
    public async Task InitializeAsync()
    {
        try { settings = await store.LoadAsync(); }
        catch (Exception ex) { Error(ex, "Não foi possível carregar as configurações. Serão usados os valores padrão."); }
        if (disposed) return;
        Volume = Math.Clamp(settings.Volume * 100, 0, 100);
        audio.Volume = (float)(Volume / 100);
        var savedShuffle = settings.Shuffle; var savedRepeat = settings.Repeat;
        ShuffleEnabled = savedShuffle; RepeatIndex = (int)savedRepeat;
        playback.SetModes(ShuffleEnabled, (RepeatMode)Math.Clamp(RepeatIndex, 0, 2));
        initialized = true;
        timer.Start();
        await ReloadPlaylistsAsync();
        if (!string.IsNullOrWhiteSpace(settings.MusicDirectory))
        {
            MusicDirectory = settings.MusicDirectory;
            if (library is null) await RefreshAsync();
            else
            {
                try { await library.SetDirectoryAsync(MusicDirectory, lifetime.Token); await LoadCatalogAsync(true); }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is not OperationCanceledException) { Error(ex, "Não foi possível abrir o catálogo musical. Consulte os logs."); LibraryStatus = Status; }
            }
        }
        await InitializeScheduleAsync();
    }
    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(ShowLibrary)); OnPropertyChanged(nameof(ShowSettings));
        OnPropertyChanged(nameof(ShowSchedule));
        OnPropertyChanged(nameof(ShowPlaylists)); OnPropertyChanged(nameof(ShowQueue));
        foreach (var property in new[] { nameof(ShowHome), nameof(IsLibrarySection), nameof(IsPlaylistSection), nameof(IsQueueSection), nameof(IsScheduleSection), nameof(IsSettingsSection), nameof(PageTitle), nameof(PageDescription) }) OnPropertyChanged(property);
    }
    partial void OnCurrentTrackChanged(Track? value)
    { OnPropertyChanged(nameof(CurrentTitle)); OnPropertyChanged(nameof(CurrentArtist)); OnPropertyChanged(nameof(PlaybackText)); OnPropertyChanged(nameof(CanSeek)); }
    partial void OnIsPlayingChanged(bool value)
    { OnPropertyChanged(nameof(PlayLabel)); OnPropertyChanged(nameof(PlaybackText)); }
    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        if (updatingPosition || CurrentTrack is null) return;
        ManualIntervention();
        try { audio.Position = TimeSpan.FromSeconds(value); }
        catch (Exception ex) { FailPlayback(ex); }
    }
    partial void OnDurationChanged(double value) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(CanSeek)); }
    partial void OnVolumeChanged(double value)
    {
        if (updatingScheduleVolume) return;
        ManualIntervention();
        updatingScheduleVolume = true; Volume = Math.Clamp(value, 0, 100); updatingScheduleVolume = false;
        playback.Volume = (float)(Math.Clamp(value, 0, 100) / 100);
        settings = settings with { Volume = audio.Volume };
        if (initialized) TrackBackground(SaveAsync());
        OnPropertyChanged(nameof(VolumeLabel));
    }
    [RelayCommand] private void Navigate(string destination) => Section = destination;
    [RelayCommand] private async Task ChooseDirectoryAsync()
    {
        if (PickDirectory is null) return;
        try
        {
            var path = await PickDirectory();
            if (path is null) return;
            Stop();
            settings = settings with { MusicDirectory = path };
            MusicDirectory = path;
            directoryGeneration++; searching?.Cancel();
            await SaveAsync();
            if (library is null) await RefreshAsync();
            else { await library.SetDirectoryAsync(path, lifetime.Token); await LoadCatalogAsync(true); }
        }
        catch (Exception ex) { Error(ex, "Não foi possível selecionar a pasta de músicas."); }
    }
    [RelayCommand] private async Task RefreshAsync()
    {
        if (library is not null)
        {
            if (string.IsNullOrWhiteSpace(settings.MusicDirectory)) { LibraryStatus = "Selecione uma pasta de músicas."; return; }
            try { await library.ReconcileAsync(false, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Error(ex, "Falha ao atualizar a biblioteca. Consulte os logs."); LibraryStatus = Status; IsBusy = false; }
            return;
        }
        if (IsBusy || string.IsNullOrWhiteSpace(settings.MusicDirectory)) return;
        IsBusy = true; Status = "Lendo músicas…";
        try
        {
            var result = await scanner.ScanAsync(settings.MusicDirectory, lifetime.Token);
            if (disposed) return;
            var selectedPath = SelectedTrack?.Path;
            Tracks.Clear(); foreach (var track in result.Tracks) Tracks.Add(track);
            libraryTracks = result.Tracks;
            LibraryTotal = result.Tracks.Count;
            playback.ReconcileLibrary(result.Tracks);
            if (CurrentTrack is not null && !result.Tracks.Any(t => t.Path == CurrentTrack.Path)) Stop();
            SelectedTrack = Tracks.FirstOrDefault(t => t.Path == selectedPath) ?? Tracks.FirstOrDefault();
            OnPropertyChanged(nameof(CountText));
            Status = result.Warnings.Count > 0 ? $"{Tracks.Count} músicas encontradas. {result.Warnings.Count} avisos; consulte os logs. {result.Warnings[0]}"
                : Tracks.Count == 0 ? "Nenhum arquivo MP3 encontrado nesta pasta e subpastas." : "Pasta carregada. Selecione uma música e pressione Reproduzir.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Não foi possível ler a pasta. Verifique se ela existe e se você tem permissão de acesso."); }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task ForceReindexAsync()
    {
        if (library is null || settings.MusicDirectory is null) return;
        try { await library.ReconcileAsync(true, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Falha na reindexação. Consulte os logs."); LibraryStatus = Status; IsBusy = false; }
    }
    partial void OnSearchTextChanged(string value) { ScheduleQuery(); OnPropertyChanged(nameof(HasSearchCriteria)); }
    partial void OnSelectedArtistChanged(FilterOption? value) { ScheduleQuery(); OnPropertyChanged(nameof(HasSearchCriteria)); }
    partial void OnSelectedAlbumChanged(FilterOption? value) { ScheduleQuery(); OnPropertyChanged(nameof(HasSearchCriteria)); }
    partial void OnSortIndexChanged(int value) => ScheduleQuery();
    partial void OnDescendingChanged(bool value) => ScheduleQuery();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(ShowEmpty)); NotifyPresentation(); }
    partial void OnDirectoryAvailableChanged(bool value) { OnPropertyChanged(nameof(EmptyText)); NotifyPresentation(); }
    private void ScheduleQuery()
    {
        if (library is null || !initialized || disposed) return;
        searching?.Cancel(); searching?.Dispose(); searching = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        TrackBackground(DebouncedQueryAsync(searching.Token));
    }
    private async Task DebouncedQueryAsync(CancellationToken token)
    {
        try { await Task.Delay(250, token); await LoadCatalogAsync(false, token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Falha ao pesquisar a biblioteca."); LibraryStatus = Status; }
    }
    private void OnLibraryChanged(object? sender, LibraryNotification notification)
    {
        var revision = directoryGeneration;
        Dispatcher.UIThread.Post(async () =>
        {
            if (disposed || revision != directoryGeneration || (notification.RootPath is not null && !string.Equals(notification.RootPath, settings.MusicDirectory, StringComparison.OrdinalIgnoreCase))) return;
            IsBusy = notification.IsBusy; DirectoryAvailable = notification.DirectoryAvailable; LibraryStatus = notification.Message;
            if (!notification.IsBusy)
            {
                try { var loading = LoadCatalogAsync(true); TrackBackground(loading); await loading; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Error(ex, "Falha ao carregar o catálogo. Consulte os logs."); LibraryStatus = Status; }
            }
        });
    }
    public async Task LoadCatalogAsync(bool reconcileQueue, CancellationToken token = default)
    {
        if (library is null || disposed) return;
        if (token == default) token = lifetime.Token;
        var revision = directoryGeneration;
        var queryRevision = ++queryGeneration;
        var query = new LibraryQuery(SearchText, SelectedArtist?.Value, SelectedAlbum?.Value, (LibrarySort)Math.Clamp(SortIndex, 0, 4), Descending);
        var result = await library.QueryAsync(query, token);
        token.ThrowIfCancellationRequested();
        if (disposed || revision != directoryGeneration) return;
        if (reconcileQueue)
        {
            // Queue follows the complete catalog, never the filtered search results.
            var complete = query == new LibraryQuery() ? result : await library.QueryAsync(new LibraryQuery(), token);
            if (disposed || revision != directoryGeneration) return;
            libraryTracks = complete.Tracks;
            playback.ReconcileLibrary(complete.Tracks);
            await playback.RefreshMetadataAsync(token);
            await ReloadPlaylistDetailAsync(token);
        }
        if (queryRevision != queryGeneration) return;
        var selectedPath = SelectedTrack?.Path;
        Tracks.ReplaceRange(result.Tracks);
        SelectedTrack = Tracks.FirstOrDefault(t => t.Path == selectedPath) ?? (CurrentTrack is null ? Tracks.FirstOrDefault() : null);
        if (Status == "Selecione uma pasta com músicas MP3 para começar." && settings.MusicDirectory is not null)
            Status = "Selecione uma música e pressione Reproduzir.";
        LibraryStatistics = $"{result.Count} músicas • {result.Artists.Count} artistas • {result.Albums.Count} álbuns";
        LibraryTotal = result.Count;
        // Reuse option objects so result refreshes do not trigger redundant filter queries.
        var artists = new[] { new FilterOption(null, "Todos os artistas") }.Concat(result.Artists.Select(a => new FilterOption(a, a))).ToArray();
        var albums = new[] { new FilterOption(null, "Todos os álbuns") }.Concat(result.Albums.Select(a => new FilterOption(a, a))).ToArray();
        if (!ArtistOptions.SequenceEqual(artists)) { ArtistOptions = artists; SelectedArtist = artists.FirstOrDefault(a => a.Value == SelectedArtist?.Value) ?? artists[0]; }
        if (!AlbumOptions.SequenceEqual(albums)) { AlbumOptions = albums; SelectedAlbum = albums.FirstOrDefault(a => a.Value == SelectedAlbum?.Value) ?? albums[0]; }
        OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(EmptyText));
        NotifyPresentation();
    }
    [RelayCommand] private void PlaySelected()
    {
        if (SelectedTrack is null) return;
        ManualIntervention();
        playback.PlayLibrary(SelectedTrack, libraryTracks);
    }
    [RelayCommand] private void PlayPause()
    {
        ManualIntervention();
        if (IsPlaying) { playback.Pause(); return; }
        if (CurrentTrack is not null && (Section is "Playlists" or "Fila" || SelectedTrack is null || SelectedTrack.Path == CurrentTrack.Path) && audio.State == PlayerState.Paused)
        { playback.Play(); return; }
        if (Section is "Playlists" or "Fila" && queue.Items.Count > 0) { playback.Play(); return; }
        var track = SelectedTrack ?? Tracks.FirstOrDefault();
        if (track is null) { Status = "Selecione uma pasta que contenha músicas MP3."; return; }
        playback.PlayLibrary(track, libraryTracks.Count > 0 ? libraryTracks : Tracks.ToArray());
    }
    [RelayCommand] private void Stop()
    {
        ManualIntervention();
        playback.Stop();
    }
    [RelayCommand] private void Next() { ManualIntervention(); playback.Next(); }
    [RelayCommand] private void Previous() { ManualIntervention(); playback.Previous(); }
    private void Tick()
    {
        try { if (CurrentTrack is not null) SetPosition(audio.Position.TotalSeconds); }
        catch (Exception ex) { FailPlayback(ex); }
    }
    private void SetPosition(double value) { updatingPosition = true; Position = value; updatingPosition = false; }
    private void FailPlayback(Exception ex)
    {
        Stop(); Error(ex, "Não foi possível reproduzir a música. O arquivo pode estar inválido ou removido, ou a saída de áudio indisponível.");
    }
    private void Error(Exception ex, string message) { logger.Error(ex, "{Message}", message); Status = message; }
    private async Task SaveAsync()
    {
        await saving.WaitAsync();
        try { await store.SaveAsync(settings); }
        catch (Exception ex) { Error(ex, "Não foi possível salvar as configurações. Verifique as permissões do perfil do Windows."); }
        finally { saving.Release(); }
    }
    public async Task SaveWindowAsync(double width, double height, int x, int y)
    { settings = settings with { Width = width, Height = height, X = x, Y = y }; await SaveAsync(); }
    public void Dispose()
    {
        if (disposed) return;
        scheduler?.StopScheduling();
        if (scheduler is not null) scheduler.Changed -= OnScheduleChanged;
        disposed = true; lifetime.Cancel(); timer.Stop(); Stop(); playback.Changed -= OnPlaybackChanged; playback.Dispose(); lifetime.Dispose();
        searching?.Cancel(); searching?.Dispose();
        if (library is not null) library.Changed -= OnLibraryChanged;
    }
    private void TrackBackground(Task task)
    {
        lock (backgroundTasks) backgroundTasks.Add(task);
        _ = task.ContinueWith(completed => { lock (backgroundTasks) backgroundTasks.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync()
    {
        if (scheduler is not null) await scheduler.DisposeAsync();
        Dispose(); Task[] pending; lock (backgroundTasks) pending = backgroundTasks.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await saving.WaitAsync().ConfigureAwait(false); saving.Release();
    }
}


