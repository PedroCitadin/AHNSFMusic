using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AHNSF.Music.Core;

/// <summary>Nonblocking producers; one bounded writer, with reserved critical capacity and finite shutdown.</summary>
public sealed class OperationalHistoryService : IOperationalHistoryService
{
    private readonly IOperationalHistoryRepository repository;
    private readonly TimeProvider clock;
    private readonly Action<string, Exception?> log;
    private readonly object gate = new();
    private readonly LinkedList<OperationalWrite> pending = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly int capacity, reserve;
    private Task? worker, disposal;
    private bool started, ending, accepting = true;
    private long accepted, completed, dropped, reportedDrops;
    private int retention = 90, errors;
    private DateTimeOffset start, maintenance;
    private long startTimestamp;
    private PlaybackPeriod? period;
    private long? playingSince;
    private double played;
    private bool resumeAfterPower;
    private OperationalActionOrigin currentOrigin;
    public Guid SessionId { get; } = Guid.NewGuid();
    public string? Failure { get; private set; }
    public long DroppedEvents => Interlocked.Read(ref dropped);
    public OperationalHistoryService(IOperationalHistoryRepository repository, Action<string, Exception?>? log = null, TimeProvider? clock = null, int capacity = 2048)
    { this.repository = repository; this.log = log ?? ((_, _) => { }); this.clock = clock ?? TimeProvider.System; this.capacity = Math.Max(8, capacity); reserve = Math.Max(2, this.capacity / 8); }
    public void SetRetention(int days) { if (days is not (30 or 90 or 180 or 365)) throw new ArgumentOutOfRangeException(nameof(days)); retention = days; }
    public Task StartAsync(bool autoStart, int retentionDays = 90, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (started || !accepting) return Task.CompletedTask;
            SetRetention(retentionDays is 30 or 90 or 180 or 365 ? retentionDays : 90); started = true; start = clock.GetUtcNow(); startTimestamp = clock.GetTimestamp();
            var e = Event(OperationalEventType.ApplicationStarted, "Aplicativo", "Aplicativo iniciado.");
            Enqueue(new(e, new(SessionId, start, null, null, "Em execução", 0)));
            if (autoStart) Record(OperationalEventType.ApplicationAutoStarted, "Aplicativo", "Inicialização automática pelo Windows.");
            worker = Task.Run(RunAsync);
        }
        return Task.CompletedTask;
    }
    private OperationalEvent Event(OperationalEventType type, string component, string text, OperationalEventSeverity severity = OperationalEventSeverity.Information,
        OperationalActionOrigin origin = OperationalActionOrigin.Application, Guid? track = null, Guid? playlist = null, Guid? rule = null, string? device = null, Guid? playback = null, string? data = null)
        => new(Guid.NewGuid(), clock.GetUtcNow(), type, severity, Limit(component, 64), Limit(text, 512), SessionId, origin, track, playlist, rule, device is null ? null : Limit(device, 256), playback, SafeJson(data));
    public static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    public static string SafeJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 2048) return "{}";
        try { using var doc = JsonDocument.Parse(json, new() { MaxDepth = 8 }); return doc.RootElement.ValueKind == JsonValueKind.Object ? json : "{}"; }
        catch (JsonException) { return "{}"; }
    }
    public void Record(OperationalEventType type, string component, string description, OperationalEventSeverity severity = OperationalEventSeverity.Information,
        OperationalActionOrigin origin = OperationalActionOrigin.Application, Guid? trackId = null, Guid? playlistId = null, Guid? ruleId = null, string? deviceId = null, string? dataJson = null)
    {
        lock (gate)
        {
            if (!started || !accepting) return;
            if (type == OperationalEventType.ComputerSuspended) { resumeAfterPower = playingSince is not null; Accumulate(clock.GetTimestamp()); }
            if (type == OperationalEventType.ComputerResumed) { if (resumeAfterPower && period is not null) playingSince = clock.GetTimestamp(); resumeAfterPower = false; }
            if (severity == OperationalEventSeverity.Error) errors++;
            Enqueue(new(Event(type, component, description, severity, origin, trackId, playlistId, ruleId, deviceId, data: dataJson)));
        }
    }
    public void Playback(OperationalEventType type, Track? track, PlaybackOrigin? playlist, OperationalActionOrigin origin, string reason, Guid? ruleId = null, string? deviceId = null)
    {
        lock (gate)
        {
            if (!started || !accepting) return;
            var timestamp = clock.GetTimestamp();
            if (type == OperationalEventType.PlaybackStarted && track is not null)
            {
                ClosePeriod("Troca de música", timestamp);
                played = 0; playingSince = timestamp; currentOrigin = origin;
                period = new(Guid.NewGuid(), SessionId, track.Id, playlist?.PlaylistId, ruleId, deviceId, Limit(track.Title, 256), Limit(playlist?.Name ?? "Biblioteca", 256), origin, clock.GetUtcNow(), null, null, "Em reprodução");
            }
            else if (type == OperationalEventType.PlaybackPaused)
            { if (period is null || playingSince is null) return; Accumulate(timestamp); }
            else if (type == OperationalEventType.PlaybackResumed)
            { if (period is null || playingSince is not null) return; playingSince = timestamp; }
            else if (type is OperationalEventType.PlaybackStopped or OperationalEventType.PlaybackEnded or OperationalEventType.PlaybackError)
            { if (period is null && type != OperationalEventType.PlaybackError) return; ClosePeriod(reason, timestamp, type); return; }
            var e = Event(type, "Player", reason, origin: origin, track: track?.Id ?? period?.TrackId, playlist: playlist?.PlaylistId ?? period?.PlaylistId, rule: ruleId ?? period?.RuleId, device: deviceId ?? period?.DeviceId, playback: period?.Id);
            Enqueue(new(e, Playback: period));
        }
    }
    private void Accumulate(long timestamp) { if (playingSince is { } from) { played += Math.Max(0, clock.GetElapsedTime(from, timestamp).TotalSeconds); playingSince = null; } }
    private void ClosePeriod(string reason, long timestamp, OperationalEventType type = OperationalEventType.PlaybackEnded)
    {
        Accumulate(timestamp);
        if (period is null) { if (type == OperationalEventType.PlaybackError) { errors++; Enqueue(new(Event(type, "Player", reason, OperationalEventSeverity.Error, currentOrigin))); } return; }
        var finished = period with { EndedUtc = clock.GetUtcNow(), EffectiveSeconds = played, EndReason = Limit(reason, 256) };
        if (type == OperationalEventType.PlaybackError) errors++;
        Enqueue(new(Event(type, "Player", reason, type == OperationalEventType.PlaybackError ? OperationalEventSeverity.Error : OperationalEventSeverity.Information,
            currentOrigin, period.TrackId, period.PlaylistId, period.RuleId, period.DeviceId, period.Id), Playback: finished));
        period = null; played = 0;
    }
    private void Enqueue(OperationalWrite entry)
    {
        // Lifecycle and playback snapshots are critical, regardless of informational severity.
        static bool Critical(OperationalWrite w) => w.Session is not null || w.Playback is not null || w.Event.Severity != OperationalEventSeverity.Information;
        if (pending.Count >= (Critical(entry) ? capacity : capacity - reserve))
        {
            var victim = pending.First;
            while (victim is not null && Critical(victim.Value)) victim = victim.Next;
            if (Critical(entry) && victim is not null) { pending.Remove(victim); completed++; }
            else { Interlocked.Increment(ref dropped); return; }
            Interlocked.Increment(ref dropped);
        }
        pending.AddLast(entry); accepted++; if (signal.CurrentCount == 0) signal.Release();
    }
    private void ReportFailure(Exception ex)
    { var message = "Histórico indisponível; reprodução e programação continuam."; if (Failure is null) log(message, ex); Failure = message; }
    private async Task RunAsync()
    {
        try
        {
            try
            {
                await repository.InitializeAsync(lifetime.Token).ConfigureAwait(false);
                await repository.MarkUnconfirmedAsync(SessionId, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { ReportFailure(ex); }
            while (!lifetime.IsCancellationRequested)
            {
                await signal.WaitAsync(TimeSpan.FromMinutes(1), lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    List<OperationalWrite> batch = [];
                    lock (gate) { while (pending.First is { } node && batch.Count < 64) { batch.Add(node.Value); pending.RemoveFirst(); } }
                    if (batch.Count == 0) break;
                    try { await repository.AppendAsync(batch, lifetime.Token).ConfigureAwait(false); Failure = null; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ReportFailure(ex); Interlocked.Add(ref dropped, batch.Count); }
                    finally { lock (gate) completed += batch.Count; }
                }
                if (DroppedEvents != reportedDrops)
                {
                    var lost = DroppedEvents - reportedDrops; reportedDrops = DroppedEvents;
                    log($"Histórico: {lost} evento(s) não persistido(s); fila limitada ou falha de armazenamento.", null);
                    // No recursion: this notice is best effort and never re-enqueued on failure.
                    try { await repository.AppendAsync([new(Event(OperationalEventType.HistoryGap, "Histórico", $"{lost} evento(s) não persistido(s). Períodos afetados podem estar incompletos.", OperationalEventSeverity.Warning))], lifetime.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ReportFailure(ex); }
                }
                if (clock.GetUtcNow() >= maintenance)
                {
                    maintenance = clock.GetUtcNow().AddHours(1);
                    try { await repository.RetainAsync(clock.GetUtcNow().AddDays(-retention), lifetime.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ReportFailure(ex); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async Task FlushAsync(CancellationToken token = default)
    {
        long target; lock (gate) target = accepted;
        while (true) { token.ThrowIfCancellationRequested(); lock (gate) { if (completed >= target) return; } await Task.Delay(10, token).ConfigureAwait(false); }
    }
    public async Task EndAsync(string finalState, CancellationToken token = default)
    {
        lock (gate)
        {
            if (!ending && started) { ending = true; ClosePeriod("Encerramento do aplicativo", clock.GetTimestamp()); Enqueue(new(Event(OperationalEventType.ApplicationEnded, "Aplicativo", "Encerramento confirmado."), new(SessionId, start, clock.GetUtcNow(), Math.Max(0, clock.GetElapsedTime(startTimestamp).TotalSeconds), Limit(finalState, 128), errors))); }
            accepting = false;
        }
        await FlushAsync(token).ConfigureAwait(false);
    }
    public Task<OperationalHistoryPage> QueryAsync(OperationalHistoryQuery query, CancellationToken token = default) => repository.QueryAsync(query, token);
    public Task<IReadOnlyList<OperationalSession>> SessionsAsync(int page = 0, CancellationToken token = default) => repository.SessionsAsync(page, token);
    public Task<IReadOnlyList<PlaybackPeriod>> PlaybackAsync(OperationalHistoryQuery query, CancellationToken token = default) => repository.PlaybackAsync(query, token);
    public Task<OperationalHistorySummary> SummaryAsync(CancellationToken token = default) => repository.SummaryAsync(clock.GetUtcNow().AddDays(-1), token);
    public async Task ExportAsync(string path, OperationalHistoryQuery query, bool overwrite = false, CancellationToken token = default)
    {
        // CreateNew guarantees no overwrite, even if the file appears after the picker checked.
        await using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("Id;Data UTC;Data local;Tipo;Severidade;Componente;Descrição;Origem;Sessão;Música Id;Playlist Id;Regra Id;Dispositivo;Período Id;Dados estruturados;Música;Playlist;Início UTC;Fim UTC;Segundos reproduzidos;Qualidade da duração;Motivo do término".AsMemory(), token);
        var snapshot = clock.GetUtcNow().AddTicks(1);
        var bounded = query with { UntilUtc = query.UntilUtc is { } until && until < snapshot ? until : snapshot, PageSize = 200 };
        while (true)
        {
            var rows = await QueryAsync(bounded with { Page = 0 }, token).ConfigureAwait(false);
            var periods = (await repository.PlaybackByIdsAsync(rows.Events.Where(e => e.PlaybackId is not null).Select(e => e.PlaybackId!.Value).Distinct().ToArray(), token)).ToDictionary(p => p.Id);
            foreach (var e in rows.Events)
            {
                var p = e.PlaybackId is { } id ? periods.GetValueOrDefault(id) : null;
                var values = new[] { e.Id.ToString(), e.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture), e.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz"), OperationalLabels.Type(e.Type), OperationalLabels.Severity(e.Severity), e.Component, e.Description, OperationalLabels.Origin(e.Origin), e.SessionId.ToString(), e.TrackId?.ToString() ?? "", e.PlaylistId?.ToString() ?? "", e.RuleId?.ToString() ?? "", e.DeviceId ?? "", e.PlaybackId?.ToString() ?? "", e.DataJson };
                var terminal = e.Type is OperationalEventType.PlaybackStopped or OperationalEventType.PlaybackEnded or OperationalEventType.PlaybackError;
                var durations = new[] { p?.TrackTitle ?? "", p?.PlaylistName ?? "", p?.StartedUtc.ToString("O") ?? "", p?.EndedUtc?.ToString("O") ?? "", terminal ? p?.EffectiveSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "" : "", p is { Confirmed: true, EffectiveSeconds: not null } ? terminal ? "Observada (relógio monotônico, pausas excluídas)" : "Observada no evento de término" : p is null ? "Não se aplica" : "Desconhecida; não estimada", p?.EndReason ?? "" };
                await writer.WriteLineAsync(string.Join(';', values.Concat(durations).Select(CsvCell)).AsMemory(), token);
            }
            if (rows.Events.Count < bounded.PageSize) break;
            var last = rows.Events[^1]; bounded = bounded with { BeforeUtc = last.TimestampUtc, BeforeId = last.Id };
        }
    }
    public static string CsvCell(string value)
    {
        // Quoting alone does not prevent spreadsheet formula injection.
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public ValueTask DisposeAsync() { lock (gate) return new(disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await EndAsync("Parado", timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { log("Flush do histórico excedeu 3 s; encerramento não garantido.", null); }
        lifetime.Cancel(); if (worker is not null) { try { await worker.WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { log("Writer do histórico não concluiu; sessão pode ficar sem confirmação.", null); } }
    }
}
