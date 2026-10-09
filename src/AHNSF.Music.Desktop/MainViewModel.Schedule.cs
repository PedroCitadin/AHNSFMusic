using System.Globalization;
using AHNSF.Music.Core;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;
public partial class MainViewModel
{
    private readonly IScheduleService? scheduleStore;
    private readonly ScheduleCoordinator? scheduler;
    private bool updatingScheduleVolume;
    private Guid editingRule;
    [ObservableProperty] private ScheduleRule? selectedRule;
    [ObservableProperty] private IReadOnlyList<ScheduleRule> rules = [];
    [ObservableProperty] private string scheduleSummary = "Modo Manual • Parado";
    [ObservableProperty] private string scheduleNext = "Sem próxima transição";
    [ObservableProperty] private string scheduleWarning = "";
    [ObservableProperty] private string weeklySummary = "";
    public sealed record WeekLine(string Text, string Color);
    [ObservableProperty] private IReadOnlyList<WeekLine> weekLines = [];
    [ObservableProperty] private IReadOnlyList<WeeklyDay> weeklyDays = [];
    [ObservableProperty] private IReadOnlyList<ScheduleCard> scheduleCards = [];
    [ObservableProperty] private ScheduleCard? selectedScheduleCard;
    private bool updatingScheduleCards;
    private IReadOnlyList<ScheduleRule> previousDisplayRules = [];
    private IReadOnlyList<Playlist> previousDisplayPlaylists = [];
    private string? previousDisplaySignature;
    partial void OnSelectedScheduleCardChanged(ScheduleCard? value) { if (!updatingScheduleCards) SelectedRule = value?.Rule; }
    partial void OnSelectedRuleChanged(ScheduleRule? value)
    { updatingScheduleCards = true; SelectedScheduleCard = ScheduleCards.FirstOrDefault(c => c.Rule.Id == value?.Id); updatingScheduleCards = false; OnPropertyChanged(nameof(RuleDeletePrompt)); }
    [ObservableProperty] private string ruleName = "";
    [ObservableProperty] private Playlist? rulePlaylist;
    [ObservableProperty] private string ruleStart = "08:00";
    [ObservableProperty] private string ruleEnd = "18:00";
    [ObservableProperty] private double ruleVolume = 35;
    [ObservableProperty] private string rulePriority = "0";
    [ObservableProperty] private bool ruleEnabled = true;
    [ObservableProperty] private bool scheduleEditor;
    [ObservableProperty] private bool scheduleDeleteConfirmation;
    public sealed partial class DayChoice : ObservableObject
    {
        public string Name { get; }
        public ScheduleDays Day { get; }
        [ObservableProperty] private bool selected;
        public DayChoice(string name, ScheduleDays day) { Name = name; Day = day; }
    }
    public IReadOnlyList<DayChoice> RuleDays { get; } = new[] { "Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb" }
        .Select((name, i) => new DayChoice(name, (ScheduleDays)(1 << i))).ToArray();
    private void ManualIntervention() { if (initialized && !disposed) scheduler?.NotifyManualIntervention(); }
    private async Task InitializeScheduleAsync()
    {
        if (scheduleStore is null || scheduler is null || disposed) return;
        try { await ReloadRulesAsync(); await scheduler.StartAsync(token: lifetime.Token); }
        catch (Exception ex) { Error(ex, "Não foi possível iniciar a programação."); ScheduleWarning = ex.Message; }
    }
    private async Task ReloadRulesAsync()
    { if (scheduleStore is not null) { var id = SelectedRule?.Id; Rules = (await scheduleStore.LoadAsync(lifetime.Token)).Rules; SelectedRule = Rules.FirstOrDefault(r => r.Id == id); } }
    private void OnScheduleChanged(object? sender, ScheduleStatus value)
    {
        void Update()
        {
            if (disposed) return;
            presentedSchedule = value;
            var ruleLabel = value.Mode == OperationMode.Manual || value.IsSuspended ? "Regra prevista" : "Regra";
            var activePlaylist = playback.CurrentTrack is null ? "—" : queue.Origin.Name;
            ScheduleSummary = $"Modo {(value.Mode == OperationMode.Manual ? "Manual" : "Automático")} • {(value.IsSuspended ? "Suspenso" : playback.State == PlayerState.Playing ? "Tocando" : playback.State == PlayerState.Paused ? "Pausado" : "Parado")}\n{ruleLabel}: {value.Evaluation.Winner?.Rule.Name ?? "nenhuma"} • Playlist: {activePlaylist}";
            ScheduleNext = value.Evaluation.NextTransitionUtc is { } next ? $"Próxima transição: {TimeZoneInfo.ConvertTime(next, TimeZoneInfo.Local):ddd dd/MM HH:mm} • {value.Evaluation.NextWinner?.Rule.Name ?? "parar"}" : "Sem próxima transição";
            ScheduleWarning = value.Warning;
            UpdateWeek(value);
            NotifyPresentation();
        }
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.Post(Update); else Update();
    }
    private void UpdateWeek(ScheduleStatus status)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var signature = $"{today}/{status.Evaluation.Winner?.Key}/{status.Evaluation.NextTransitionUtc}/{string.Join(';', status.Evaluation.Active.Select(a => a.Key))}";
        if (signature == previousDisplaySignature && Rules.SequenceEqual(previousDisplayRules) && Playlists.SequenceEqual(previousDisplayPlaylists)) return;
        previousDisplaySignature = signature; previousDisplayRules = Rules.ToArray(); previousDisplayPlaylists = Playlists.ToArray();
        WeeklyDays = WeeklyPresentation.Build(Rules, Playlists.ToArray(), status.Evaluation, DateTimeOffset.Now, TimeZoneInfo.Local);
        var cards = Rules.Select(rule => new ScheduleCard(rule, Playlists.FirstOrDefault(p => p.Id == rule.PlaylistId)?.Name ?? "Playlist indisponível",
            WeeklyPresentation.DaysLabel(rule.Days), $"{rule.StartTime:HH:mm} – {rule.EndTime:HH:mm}{(rule.EndTime < rule.StartTime ? " (+1 dia)" : "")}",
            $"Volume {rule.Volume:0}% · prioridade {rule.Priority}", !rule.IsEnabled ? "○ Desativada" : status.Evaluation.Winner?.Rule.Id == rule.Id ? "▶ Vencedora agora" : status.Evaluation.Active.Any(a => a.Rule.Id == rule.Id) ? "⚠ Ativa em conflito" : "◷ Habilitada",
            !rule.IsEnabled ? "#A5B2C2" : status.Evaluation.Winner?.Rule.Id == rule.Id ? "#8FD3C5" : status.Evaluation.Active.Any(a => a.Rule.Id == rule.Id) ? "#E8BC79" : "#E8EEF4")).ToArray();
        if (!ScheduleCards.SequenceEqual(cards))
        { var id = SelectedRule?.Id; updatingScheduleCards = true; ScheduleCards = cards; SelectedScheduleCard = cards.FirstOrDefault(c => c.Rule.Id == id); updatingScheduleCards = false; }
        var lines = new List<string>();
        foreach (var day in RuleDays)
        {
            lines.Add(day.Name.ToUpperInvariant());
            var spans = new List<(long Start, long End)>();
            var index = (int)Math.Log2((int)day.Day);
            foreach (var rule in Rules.OrderBy(r => r.StartTime).ThenByDescending(r => r.Priority))
            {
                var overnight = rule.EndTime < rule.StartTime;
                if ((rule.Days & day.Day) != 0)
                {
                    bool StartsHere(ScheduleOccurrence occurrence) => occurrence.Rule.Id == rule.Id && (int)TimeZoneInfo.ConvertTime(occurrence.StartUtc, TimeZoneInfo.Local).DayOfWeek == index;
                    var state = !rule.IsEnabled ? "Desativada" : status.Evaluation.Winner is { } winner && StartsHere(winner) ? "Vencedora agora" : status.Evaluation.Active.Any(StartsHere) ? "Ativa em conflito" : "Habilitada";
                    var playlist = Playlists.FirstOrDefault(p => p.Id == rule.PlaylistId)?.Name ?? "Playlist removida";
                    lines.Add($"  {rule.StartTime:HH:mm}–{rule.EndTime:HH:mm}{(overnight ? " (+1 dia)" : "")} • {rule.Name} • {playlist} • {rule.Volume:0}% • prioridade {rule.Priority} • {state}");
                    if (rule.IsEnabled) spans.Add((rule.StartTime.Ticks, overnight ? TimeSpan.TicksPerDay : rule.EndTime.Ticks));
                }
                if (overnight && rule.IsEnabled && (rule.Days & (ScheduleDays)(1 << ((index + 6) % 7))) != 0)
                { spans.Add((0, rule.EndTime.Ticks)); lines.Add($"  00:00–{rule.EndTime:HH:mm} • continuação: {rule.Name}"); }
            }
            long cursor = 0;
            var gaps = new List<string>();
            string Time(long ticks) => ticks == TimeSpan.TicksPerDay ? "24:00" : new TimeOnly(ticks).ToString("HH:mm");
            foreach (var span in spans.OrderBy(s => s.Start)) { if (span.Start > cursor) gaps.Add($"{Time(cursor)}–{Time(span.Start)}"); cursor = Math.Max(cursor, span.End); }
            if (cursor < TimeSpan.TicksPerDay) gaps.Add($"{Time(cursor)}–24:00");
            lines.Add($"  Sem programação: {(gaps.Count == 0 ? "nenhuma lacuna" : string.Join(", ", gaps))}\n");
        }
        WeeklySummary = string.Join("\n", lines);
        WeekLines = lines.Select(line => new WeekLine(line, line.Contains("Vencedora agora") ? "#75CDD2" : line.Contains("Ativa em conflito") ? "#FFC08A" : line.Contains("Desativada") ? "#82909B" : line.Contains("Sem programação") ? "#A5BAC6" : "#DEE8EC")).ToArray();
    }
    [RelayCommand] private void NewRule()
    {
        editingRule = Guid.Empty; RuleName = ""; RulePlaylist = Playlists.FirstOrDefault(); RuleStart = "08:00"; RuleEnd = "18:00"; RuleVolume = 35; RulePriority = "0"; RuleEnabled = true;
        foreach (var day in RuleDays) day.Selected = day.Day is not (ScheduleDays.Saturday or ScheduleDays.Sunday);
        ScheduleEditor = true; ScheduleDeleteConfirmation = false; ScheduleWarning = "";
    }
    [RelayCommand] private void EditRule()
    {
        if (SelectedRule is not { } r) return;
        editingRule = r.Id; RuleName = r.Name; RulePlaylist = Playlists.FirstOrDefault(p => p.Id == r.PlaylistId);
        RuleStart = r.StartTime.ToString("HH:mm"); RuleEnd = r.EndTime.ToString("HH:mm"); RuleVolume = r.Volume; RulePriority = r.Priority.ToString(); RuleEnabled = r.IsEnabled;
        foreach (var d in RuleDays) d.Selected = (r.Days & d.Day) != 0;
        ScheduleEditor = true; ScheduleDeleteConfirmation = false;
    }
    [RelayCommand] private void CancelRuleEdit() => ScheduleEditor = false;
    private async Task ScheduleActionAsync(Func<Task> action)
    {
        if (scheduleStore is null || scheduler is null) return;
        try { await action(); await ReloadRulesAsync(); await scheduler.EvaluateAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ScheduleWarning = ex.Message; logger.Warning(ex, "Falha ao alterar programação"); }
    }
    [RelayCommand] private Task SaveRuleAsync() => ScheduleActionAsync(async () =>
    {
        if (!TimeOnly.TryParseExact(RuleStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(RuleEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) throw new ArgumentException("Use horários no formato HH:mm.");
        if (!int.TryParse(RulePriority, out var priority)) throw new ArgumentException("A prioridade deve ser um número inteiro.");
        var days = RuleDays.Where(d => d.Selected).Aggregate(ScheduleDays.None, (a, d) => a | d.Day);
        var saved = await scheduleStore!.SaveAsync(new(editingRule, RuleName, RulePlaylist?.Id, days, start, end, RuleVolume, priority, RuleEnabled, default, default), lifetime.Token);
        SelectedRule = saved; ScheduleEditor = false;
    });
    [RelayCommand] private Task ToggleRuleAsync() => ScheduleActionAsync(async () => { if (SelectedRule is { } rule) await scheduleStore!.SetEnabledAsync(rule.Id, !rule.IsEnabled, lifetime.Token); });
    [RelayCommand] private void RequestDeleteRule() { if (SelectedRule is not null) ScheduleDeleteConfirmation = true; }
    [RelayCommand] private void CancelDeleteRule() => ScheduleDeleteConfirmation = false;
    [RelayCommand] private Task ConfirmDeleteRuleAsync() => ScheduleActionAsync(async () => { if (!ScheduleDeleteConfirmation) return; if (SelectedRule is { } rule) await scheduleStore!.DeleteAsync(rule.Id, lifetime.Token); ScheduleDeleteConfirmation = false; SelectedRule = null; });
    [RelayCommand] private Task ManualModeAsync() => ScheduleActionAsync(() => scheduler!.SetModeAsync(OperationMode.Manual, lifetime.Token));
    [RelayCommand] private Task AutomaticModeAsync() => ScheduleActionAsync(() => scheduler!.SetModeAsync(OperationMode.Automatic, lifetime.Token));
    [RelayCommand] private void SuspendAutomation() { ManualIntervention(); playback.Pause(); }
}
