using System.Globalization;
using System.Text.Json;
using AHNSF.Music.Core;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;
public partial class MainViewModel
{
    private readonly IOperationalHistoryService? history;
    private CancellationTokenSource? historySearching;
    private long historyGeneration;
    private int historyPage;
    private DateTimeOffset nextHistorySummary;
    private bool historySummaryLoading;
    public bool ShowHistory => Section == "Histórico";
    public IReadOnlyList<string> HistoryPeriods { get; } = ["Hoje", "Últimos 7 dias", "Últimos 30 dias", "Período personalizado"];
    public IReadOnlyList<int> HistoryRetentionOptions { get; } = [30, 90, 180, 365];
    public sealed record HistoryTypeOption(OperationalEventType? Value, string Label);
    public sealed record HistorySeverityOption(OperationalEventSeverity? Value, string Label);
    public IReadOnlyList<HistoryTypeOption> HistoryTypes { get; } = new[] { new HistoryTypeOption(null, "Todos os tipos") }.Concat(Enum.GetValues<OperationalEventType>().Select(v => new HistoryTypeOption(v, OperationalLabels.Type(v)))).ToArray();
    public IReadOnlyList<HistorySeverityOption> HistorySeverities { get; } = [new(null, "Todas as severidades"), new(OperationalEventSeverity.Information, "Informação"), new(OperationalEventSeverity.Warning, "Aviso"), new(OperationalEventSeverity.Error, "Erro")];
    public sealed record HistoryEventRow(OperationalEvent Event)
    {
        public string Time => Event.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
        public string Type => OperationalLabels.Type(Event.Type);
        public string Severity => OperationalLabels.Severity(Event.Severity);
        public string Description => Event.Description;
        public string Origin => OperationalLabels.Origin(Event.Origin);
    }
    public sealed record HistoryPlaybackRow(PlaybackPeriod Period)
    {
        public string Title => Period.TrackTitle;
        public string Description => $"{Period.PlaylistName} • {OperationalLabels.Origin(Period.Origin)}";
        public string Timing => $"{Period.StartedUtc.ToLocalTime():dd/MM HH:mm:ss} → {(Period.EndedUtc is { } end ? end.ToLocalTime().ToString("dd/MM HH:mm:ss") : "sem término confirmado")}";
        public string Duration => Period.EffectiveSeconds is { } seconds && Period.Confirmed ? $"Tempo reproduzido observado: {TimeSpan.FromSeconds(seconds):g} • {Period.EndReason}" : $"Duração desconhecida • {Period.EndReason}";
    }
    public sealed record HistorySessionRow(OperationalSession Session)
    {
        public string Title => $"{Session.StartedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} • {Session.FinalState}";
        public string Description => $"{Session.Id} • {(Session.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds).ToString("g") : "duração desconhecida")} • {Session.RelevantErrors} erro(s)";
    }
    [ObservableProperty] private int historyPeriodIndex;
    [ObservableProperty] private string historyFrom = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private string historyUntil = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private HistoryTypeOption? historyType;
    [ObservableProperty] private HistorySeverityOption? historySeverity;
    [ObservableProperty] private string historySearch = "";
    [ObservableProperty] private bool historyLoading;
    [ObservableProperty] private bool historyExporting;
    [ObservableProperty] private string historyFeedback = "";
    [ObservableProperty] private string historyPagination = "Página 1";
    [ObservableProperty] private int historyRetentionDays = 90;
    [ObservableProperty] private IReadOnlyList<HistoryEventRow> historyEvents = [];
    [ObservableProperty] private IReadOnlyList<HistoryPlaybackRow> historyPlayback = [];
    [ObservableProperty] private IReadOnlyList<HistorySessionRow> historySessions = [];
    [ObservableProperty] private HistoryEventRow? selectedHistoryEvent;
    [ObservableProperty] private string historyEventDetails = "Selecione um evento para ver os detalhes.";
    [ObservableProperty] private string historyDashboard = "Histórico local • carregando resumo…";
    [ObservableProperty] private bool historyHasNext;
    public bool HistoryHasPrevious => historyPage > 0;
    public bool HistoryCustomPeriod => HistoryPeriodIndex == 3;
    public bool HistoryEmpty => HistoryEvents.Count == 0 && !HistoryLoading;
    public Func<Task<string?>>? PickHistoryCsv { get; set; }
    partial void OnHistoryPeriodIndexChanged(int value) => OnPropertyChanged(nameof(HistoryCustomPeriod));
    partial void OnHistoryEventsChanged(IReadOnlyList<HistoryEventRow> value) => OnPropertyChanged(nameof(HistoryEmpty));
    partial void OnHistoryLoadingChanged(bool value) => OnPropertyChanged(nameof(HistoryEmpty));
    partial void OnSelectedHistoryEventChanged(HistoryEventRow? value)
    {
        if (value is null) { HistoryEventDetails = "Selecione um evento para ver os detalhes."; return; }
        var e = value.Event;
        HistoryEventDetails = $"{e.TimestampUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz} • UTC {e.TimestampUtc:O}\n{value.Type} • {value.Severity} • {e.Component} • {value.Origin}\n{e.Description}\nSessão: {e.SessionId}\nMúsica: {e.TrackId} • Playlist: {e.PlaylistId} • Regra: {e.RuleId}\nDispositivo: {e.DeviceId}\nPeríodo: {e.PlaybackId}\nDados: {e.DataJson}";
    }
    public OperationalHistoryQuery BuildHistoryQuery(int page = 0)
    {
        DateTime from, until;
        if (HistoryCustomPeriod)
        {
            if (!DateTime.TryParseExact(HistoryFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from) || !DateTime.TryParseExact(HistoryUntil, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out until) || until < from) throw new ArgumentException("Informe datas válidas (AAAA-MM-DD), com fim igual ou posterior ao início.");
            until = until.AddDays(1);
        }
        else { until = DateTime.Today.AddDays(1); from = DateTime.Today.AddDays(HistoryPeriodIndex == 1 ? -6 : HistoryPeriodIndex == 2 ? -29 : 0); }
        return new(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from, DateTimeKind.Unspecified), TimeZoneInfo.Local)), new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(until, DateTimeKind.Unspecified), TimeZoneInfo.Local)), HistoryType?.Value, HistorySeverity?.Value, HistorySearch, page, 50);
    }
    [RelayCommand] private async Task RefreshHistoryAsync()
    { historyPage = 0; await LoadHistoryPageAsync(); }
    [RelayCommand] private async Task NextHistoryPageAsync()
    { if (!HistoryHasNext) return; historyPage++; await LoadHistoryPageAsync(); }
    [RelayCommand] private async Task PreviousHistoryPageAsync()
    { if (historyPage == 0) return; historyPage--; await LoadHistoryPageAsync(); }
    public async Task LoadHistoryPageAsync()
    {
        if (history is null) { HistoryFeedback = "Histórico indisponível nesta execução."; return; }
        historySearching?.Cancel(); historySearching?.Dispose(); historySearching = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = historySearching.Token; var generation = ++historyGeneration;
        HistoryLoading = true; HistoryFeedback = "Consultando histórico…";
        try
        {
            var query = BuildHistoryQuery(historyPage);
            var page = await history.QueryAsync(query, token);
            var periods = await history.PlaybackAsync(query, token);
            var sessions = await history.SessionsAsync(historyPage, token);
            if (disposed || generation != historyGeneration) return;
            HistoryEvents = page.Events.Select(e => new HistoryEventRow(e)).ToArray();
            HistoryPlayback = periods.Select(p => new HistoryPlaybackRow(p)).ToArray(); HistorySessions = sessions.Select(s => new HistorySessionRow(s)).ToArray();
            SelectedHistoryEvent = null; HistoryHasNext = (long)(historyPage + 1) * 50 < page.Total || periods.Count == 50 || sessions.Count == 50;
            HistoryPagination = $"Página {historyPage + 1} • {page.Total:N0} evento(s) no filtro"; OnPropertyChanged(nameof(HistoryHasPrevious));
            HistoryFeedback = history.Failure ?? $"Consulta concluída. {history.DroppedEvents} evento(s) não persistido(s) nesta execução.";
            await RefreshHistorySummaryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == historyGeneration) HistoryFeedback = "Não foi possível consultar o histórico. Consulte os logs técnicos."; logger.Warning(ex, "Falha de consulta do histórico"); }
        finally { if (generation == historyGeneration) HistoryLoading = false; }
    }
    private async Task RefreshHistorySummaryAsync()
    {
        if (history is null || historySummaryLoading) return;
        historySummaryLoading = true;
        try
        {
            var summary = await history.SummaryAsync(lifetime.Token);
            if (disposed) return;
            HistoryDashboard = $"{PlayerStateLabel} • {summary.RecentFailures} falha(s) nas últimas 24 h\nÚltimo erro: {summary.LastError?.Description ?? "Nenhum"}\nRecuperação: {summary.LastRecovery?.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "—"} • Desconexão: {summary.LastDisconnect?.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "—"}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { HistoryDashboard = "Histórico indisponível. O player continua operacional."; logger.Warning(ex, "Falha do resumo histórico"); }
        finally { historySummaryLoading = false; }
    }
    [RelayCommand] private async Task SaveHistoryRetentionAsync()
    {
        try { history?.SetRetention(HistoryRetentionDays); settings = settings with { HistoryRetentionDays = HistoryRetentionDays }; await SaveAsync(); HistoryFeedback = $"Retenção salva: {HistoryRetentionDays} dias. Limpeza incremental a cada hora."; }
        catch (Exception ex) { HistoryFeedback = "Não foi possível salvar a retenção."; logger.Warning(ex, "Falha na retenção"); }
    }
    [RelayCommand] private async Task ExportHistoryAsync()
    {
        if (history is null || PickHistoryCsv is null || HistoryExporting) return;
        try
        {
            var query = BuildHistoryQuery(); var path = await PickHistoryCsv(); if (path is null) return;
            if (File.Exists(path)) { RequestDialog("Substituir relatório?", "O arquivo CSV escolhido já existe. Substituí-lo remove seu conteúdo anterior.", "Substituir", () => WriteHistoryCsvAsync(path, query, true), danger: true); return; }
            await WriteHistoryCsvAsync(path, query, false);
        }
        catch (Exception ex) { HistoryFeedback = ex.Message; logger.Warning(ex, "Falha ao escolher relatório"); }
    }
    public async Task WriteHistoryCsvAsync(string path, OperationalHistoryQuery query, bool overwrite)
    {
        HistoryExporting = true;
        try { await history!.ExportAsync(path, query, overwrite, lifetime.Token); HistoryFeedback = "Relatório CSV exportado (UTF-8 com BOM)."; }
        catch (OperationCanceledException) { HistoryFeedback = "Exportação cancelada; um arquivo parcial pode existir."; }
        catch (Exception ex) { HistoryFeedback = "Falha de escrita do CSV. Verifique a pasta e as permissões; um arquivo parcial pode existir."; logger.Warning(ex, "Falha de exportação CSV"); }
        finally { HistoryExporting = false; }
    }
}
