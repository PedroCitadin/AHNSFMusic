namespace AHNSF.Music.Core;

// Domain entry point for future automation; no dependency on UI commands or scheduled execution.
public sealed class PlaybackService : IPlaybackService, IDisposable
{
    private readonly IAudioEngine audio;
    private readonly IPlaylistService? playlists;
    private readonly Action<Action> dispatch;
    private readonly object gate = new();
    private long generation;
    private long queueRevision;
    private bool disposed;
    private bool scheduledRepeat;
    private readonly IOperationalHistoryService? history;
    private OperationalActionOrigin actionOrigin = OperationalActionOrigin.Manual;
    private Guid? ruleId;
    private string? deviceId;
    public void SetOperationalContext(OperationalActionOrigin origin, Guid? ruleId = null, string? deviceId = null)
    { lock (gate) { actionOrigin = origin; this.ruleId = ruleId; this.deviceId = deviceId; } }
    private void Observe(OperationalEventType type, string reason)
    {
        try { history?.Playback(type, CurrentTrack, Queue.Origin, actionOrigin, reason, ruleId, deviceId); }
        catch { /* Observability must never compromise audio. */ }
    }
    public float Volume
    {
        get { lock (gate) return audio.Volume; }
        set { lock (gate) { var volume = Math.Clamp(value, 0, 1); if (audio.Volume == volume) return; audio.Volume = volume; Notify(); } }
    }
    public void SetScheduledRepeat(bool enabled) { lock (gate) scheduledRepeat = enabled; }
    public event EventHandler? Changed;
    public PlaybackQueue Queue { get; } = new();
    public Track? CurrentTrack { get; private set; }
    public PlayerState State => audio.State;
    public string Message { get; private set; } = "Selecione uma música e pressione Reproduzir.";
    public Exception? LastError { get; private set; }
    public PlaybackService(IAudioEngine audio, IPlaylistService? playlists = null, Action<Action>? dispatch = null, IOperationalHistoryService? history = null)
    { this.audio = audio; this.playlists = playlists; this.dispatch = dispatch ?? (action => action()); this.history = history; audio.Ended += OnEnded; }
    private void Notify(string? message = null) { if (message is not null) Message = message; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task LoadPlaylistAsync(Guid id, bool start = false, Guid? itemId = null, CancellationToken token = default)
    {
        if (playlists is null) throw new InvalidOperationException("Serviço de playlists indisponível.");
        long revision; lock (gate) revision = queueRevision;
        var detail = await playlists.GetAsync(id, token).ConfigureAwait(false) ?? throw new KeyNotFoundException("A playlist não existe mais.");
        if (itemId is Guid requested && !detail.Items.Any(i => i.Id == requested)) throw new ArgumentException("Item não encontrado na playlist.");
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (revision != queueRevision) throw new OperationCanceledException("A fila mudou enquanto a playlist era carregada.");
            StopCore();
            Queue.Load(detail.Items.Select(i => new QueueItem(Guid.NewGuid(), i.Track, i.Id)), new(id, detail.Playlist.Name)); queueRevision++;
            if (itemId is Guid selected)
            {
                var occurrence = Queue.Items.FirstOrDefault(i => i.PlaylistItemId == selected) ?? throw new ArgumentException("Item não encontrado na playlist.");
                Queue.SelectOccurrence(occurrence.Id);
            }
            var unavailable = detail.Items.Count(i => !PlaybackQueue.IsPlayable(i.Track));
            Notify(detail.Items.Count == 0 ? "A playlist está vazia. Adicione músicas pela biblioteca." : $"Playlist carregada: {detail.Playlist.Name}. {unavailable} música(s) indisponível(is)." );
            if (start) PlayCore();
        }
    }
    public void Play() { lock (gate) PlayCore(); }
    private void PlayCore()
    {
        if (CurrentTrack is not null && State == PlayerState.Paused)
        { try { audio.Play(); Observe(OperationalEventType.PlaybackResumed, "Reprodução retomada."); Notify($"Reproduzindo: {CurrentTrack.Title}"); } catch (Exception ex) { Fail(ex); } return; }
        var item = Queue.CurrentItem ?? Queue.Navigate(1);
        if (item is null) { Notify("A fila está vazia ou não contém músicas disponíveis."); return; }
        Start(item);
    }
    public void PlayOccurrence(Guid id)
    {
        lock (gate)
        {
            var item = Queue.Items.FirstOrDefault(i => i.Id == id);
            if (item is null) { Notify("O item não está mais na fila."); return; }
            if (!PlaybackQueue.IsPlayable(item.Track)) { Notify("Música indisponível. A referência foi preservada na fila."); return; }
            if (CurrentTrack is not null) Observe(OperationalEventType.ManualTrackChange, "Seleção manual de música.");
            Queue.SelectOccurrence(id); Start(item);
        }
    }
    public void PlayLibrary(Track track, IReadOnlyList<Track> library)
    {
        lock (gate)
        {
            if (CurrentTrack is not null) Observe(OperationalEventType.ManualTrackChange, "Seleção manual na biblioteca.");
            Queue.Replace(library.Count == 0 ? [track] : library); queueRevision++;
            var selected = Queue.Select(track);
            if (selected is null) { Queue.Add(track); Queue.Select(track); }
            Start(Queue.CurrentItem!);
        }
    }
    private void Start(QueueItem item)
    {
        generation++; LastError = null;
        if (!PlaybackQueue.IsPlayable(item.Track)) { StopCore(); Notify("Música indisponível. A referência foi preservada na fila."); return; }
        try { audio.Load(item.Track.Path); CurrentTrack = item.Track; audio.Play(); Observe(OperationalEventType.PlaybackStarted, $"Reproduzindo: {item.Track.Title}"); Notify($"Reproduzindo: {item.Track.Title}"); }
        catch (Exception ex) { Fail(ex); }
    }
    public void Pause()
    { lock (gate) { try { audio.Pause(); Observe(OperationalEventType.PlaybackPaused, "Reprodução pausada."); Notify($"Pausado: {CurrentTrack?.Title}"); } catch (Exception ex) { Fail(ex); } } }
    public void Stop() { lock (gate) { StopCore(); Notify("Reprodução parada."); } }
    private void StopCore()
    { Observe(OperationalEventType.PlaybackStopped, "Reprodução parada."); generation++; CurrentTrack = null; try { audio.Stop(); } catch (Exception ex) { LastError = ex; } }
    private void Fail(Exception error)
    { Observe(OperationalEventType.PlaybackError, "Falha de reprodução; consulte o log técnico."); StopCore(); LastError = error; Notify("Não foi possível reproduzir a música. O arquivo pode estar inválido ou removido, ou a saída de áudio indisponível."); }
    public void Next() { lock (gate) Move(1, false); }
    public void Previous() { lock (gate) Move(-1, false); }
    private void Move(int direction, bool automatic)
    {
        var repeat = Queue.Repeat; QueueItem? item;
        try { if (automatic && scheduledRepeat) Queue.Repeat = RepeatMode.Queue; item = Queue.Navigate(direction, automatic); }
        finally { Queue.Repeat = repeat; }
        if (item is not null) { Observe(automatic ? OperationalEventType.AutomaticAdvance : OperationalEventType.ManualTrackChange, automatic ? "Avanço automático da fila." : "Navegação manual da fila."); Start(item); }
        else if (automatic) { Observe(OperationalEventType.PlaybackEnded, "Fim da fila."); StopCore(); Notify("Reprodução concluída. Fim da fila."); }
        else Notify(direction > 0 ? "Você chegou ao fim da fila." : "Você está na primeira música.");
    }
    private void OnEnded(object? sender, AudioEndedEventArgs args)
    {
        long revision; lock (gate) revision = generation;
        dispatch(() =>
        {
            lock (gate)
            {
                if (disposed || revision != generation || CurrentTrack is null) return;
                if (args.Error is not null) Fail(args.Error); else Move(1, true);
            }
        });
    }
    public void Add(Track track, bool next = false)
    { lock (gate) { Queue.Add(track, next); queueRevision++; Notify(next ? "Música adicionada como próxima." : "Música adicionada ao final da fila."); } }
    public void Remove(IReadOnlyList<Guid> ids)
    { lock (gate) { if (Queue.Remove(ids)) StopCore(); queueRevision++; Notify("Item(ns) removido(s) da fila."); } }
    public void Clear() { lock (gate) { StopCore(); Queue.Clear(); queueRevision++; Notify("Fila limpa. As playlists salvas foram preservadas."); } }
    public void SetModes(bool shuffle, RepeatMode repeat)
    { lock (gate) { Queue.SetShuffle(shuffle); Queue.Repeat = Enum.IsDefined(repeat) ? repeat : RepeatMode.Off; Notify(); } }
    public void ReconcileLibrary(IReadOnlyList<Track> tracks)
    {
        lock (gate)
        {
            if (!Queue.UsesLibraryOrder) return;
            Queue.Replace(tracks); queueRevision++;
            if (CurrentTrack is not null && !tracks.Any(t => string.Equals(t.Path, CurrentTrack.Path, StringComparison.OrdinalIgnoreCase)))
            { StopCore(); Notify("A música carregada foi removida da biblioteca. Reprodução interrompida."); }
            else Notify();
        }
    }
    public async Task RefreshMetadataAsync(CancellationToken token = default)
    {
        if (playlists is null) return;
        Guid[] ids; long revision;
        lock (gate) { ids = Queue.Items.Select(i => i.Track.Id).ToArray(); revision = queueRevision; }
        var resolved = await playlists.ResolveAsync(ids, token).ConfigureAwait(false);
        lock (gate)
        {
            if (disposed || revision != queueRevision) return;
            Queue.UpdateMetadata(resolved);
            if (CurrentTrack is not null && resolved.TryGetValue(CurrentTrack.Id, out var track) && !PlaybackQueue.IsPlayable(track))
            { StopCore(); Notify("A música carregada ficou indisponível. Reprodução interrompida; referência preservada."); }
            else Notify();
        }
    }
    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; audio.Ended -= OnEnded; StopCore(); } }
}
