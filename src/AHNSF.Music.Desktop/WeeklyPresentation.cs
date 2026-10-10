using AHNSF.Music.Core;

namespace AHNSF.Music.Desktop;
/// <summary>Color is a semantic tone name (see ToneBrushConverter). StartHour/EndHour place the block on the 0–24 h timeline.</summary>
public sealed record WeeklyInterval(string Hours, string Name, string Details, string State, string Color, bool IsGap, bool HasConflict, double StartHour = 0, double EndHour = 0)
{
    public bool IsWinner => Color == "AccentText";
    public bool IsDisabled => Color == "TextSecondary" && !IsGap;
    public string Tip => $"{Hours} • {Name}\n{Details}\n{State}";
}
public sealed record WeeklyDay(string Name, string Summary, bool IsToday, IReadOnlyList<WeeklyInterval> Intervals)
{
    public string ShortName => Name[..3];
    public IReadOnlyList<WeeklyInterval> Blocks => Intervals.Where(i => !i.IsGap).ToArray();
}
public sealed record ScheduleCard(ScheduleRule Rule, string Playlist, string Days, string Hours, string Details, string State, string Color);

/// <summary>Weekly display only; conflict decisions remain in ScheduleEvaluator.</summary>
public static class WeeklyPresentation
{
    private sealed record Segment(ScheduleRule Rule, long Start, long End, bool Continuation);
    private static readonly string[] names = ["Domingo", "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado"];
    public static string DaysLabel(ScheduleDays days) => string.Join(" · ", Enumerable.Range(0, 7).Where(i => (days & (ScheduleDays)(1 << i)) != 0).Select(i => names[i][..3]));
    private static double Hour(long ticks) => (double)ticks / TimeSpan.TicksPerHour;
    private static string Time(long ticks) => ticks == TimeSpan.TicksPerDay ? "24:00" : new TimeOnly(ticks).ToString("HH:mm");
    public static IReadOnlyList<WeeklyDay> Build(IReadOnlyList<ScheduleRule> rules, IReadOnlyList<Playlist> playlists, ScheduleEvaluation evaluation, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var result = new List<WeeklyDay>();
        foreach (var day in new[] { 1, 2, 3, 4, 5, 6, 0 })
        {
            var date = monday.AddDays((day + 6) % 7);
            var segments = new List<Segment>();
            foreach (var rule in rules)
            {
                bool overnight = rule.EndTime < rule.StartTime;
                if ((rule.Days & (ScheduleDays)(1 << day)) != 0) segments.Add(new(rule, rule.StartTime.Ticks, overnight ? TimeSpan.TicksPerDay : rule.EndTime.Ticks, false));
                if (overnight && (rule.Days & (ScheduleDays)(1 << ((day + 6) % 7))) != 0) segments.Add(new(rule, 0, rule.EndTime.Ticks, true));
            }
            var intervals = new List<WeeklyInterval>();
            foreach (var s in segments.OrderBy(s => s.Start).ThenByDescending(s => s.Rule.Priority))
            {
                bool IsHere(ScheduleOccurrence o) => o.Rule.Id == s.Rule.Id &&
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Continuation ? o.EndUtc.AddTicks(-1) : o.StartUtc, zone).DateTime) == date;
                var live = evaluation.Active.Any(IsHere);
                var winner = evaluation.Winner is { } w && IsHere(w);
                var conflict = s.Rule.IsEnabled && segments.Any(other => other.Rule.Id != s.Rule.Id && other.Rule.IsEnabled && other.Start < s.End && s.Start < other.End);
                var future = date > today || date == today && s.Start > local.TimeOfDay.Ticks;
                var state = !s.Rule.IsEnabled ? "○ Desativada" : winner ? "▶ Vigente · vencedora" : live ? "⚠ Ativa · não vence" : future ? "◷ Futuro" : "✓ Programado";
                var color = !s.Rule.IsEnabled ? "TextSecondary" : winner ? "AccentText" : live || conflict ? "Warning" : "TextPrimary";
                var playlist = playlists.FirstOrDefault(p => p.Id == s.Rule.PlaylistId)?.Name ?? "Playlist indisponível";
                intervals.Add(new($"{Time(s.Start)} – {Time(s.End)}", s.Rule.Name + (s.Continuation ? " · continuação" : ""),
                    $"{playlist} · {s.Rule.Volume:0}% · prioridade {s.Rule.Priority}", state, color, false, conflict, Hour(s.Start), Hour(s.End)));
            }
            long cursor = 0;
            foreach (var s in segments.Where(s => s.Rule.IsEnabled).OrderBy(s => s.Start))
            { if (s.Start > cursor) AddGap(cursor, s.Start); cursor = Math.Max(cursor, s.End); }
            if (cursor < TimeSpan.TicksPerDay) AddGap(cursor, TimeSpan.TicksPerDay);
            result.Add(new(names[day], $"{date:dd/MM} · {segments.Count(s => s.Rule.IsEnabled)} intervalo(s) habilitado(s)", date == today,
                intervals.OrderBy(i => i.Hours, StringComparer.Ordinal).ThenBy(i => i.IsGap).ToArray()));
            void AddGap(long start, long end) => intervals.Add(new($"{Time(start)} – {Time(end)}", "Sem programação", "No Automático, o player permanece parado.", "— Sem programação", "TextSecondary", true, false, Hour(start), Hour(end)));
        }
        return result;
    }
}
