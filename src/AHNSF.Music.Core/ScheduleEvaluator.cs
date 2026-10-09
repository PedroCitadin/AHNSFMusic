namespace AHNSF.Music.Core;

public sealed class ScheduleEvaluator : IScheduleEvaluator
{
    public ScheduleEvaluation Evaluate(IReadOnlyList<ScheduleRule> rules, DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var occurrences = new List<ScheduleOccurrence>();
        // Include yesterday for overnight rules; eight future days cover every weekly transition.
        for (int d = -1; d <= 8; d++)
        {
            var startDate = date.AddDays(d); var day = (ScheduleDays)(1 << (int)startDate.DayOfWeek);
            foreach (var rule in rules.Where(r => r.IsEnabled && (r.Days & day) != 0))
            {
                if (rule.StartTime == rule.EndTime) continue;
                var endDate = rule.EndTime < rule.StartTime ? startDate.AddDays(1) : startDate;
                var start = Resolve(startDate.ToDateTime(rule.StartTime, DateTimeKind.Unspecified), zone, false);
                var end = Resolve(endDate.ToDateTime(rule.EndTime, DateTimeKind.Unspecified), zone, true);
                if (end > start) occurrences.Add(new(rule, start, end));
            }
        }
        var active = ActiveAt(now); var winner = active.FirstOrDefault();
        DateTimeOffset? transition = null; ScheduleOccurrence? nextWinner = null;
        foreach (var boundary in occurrences.SelectMany(o => new[] { o.StartUtc, o.EndUtc }).Where(t => t > now).Distinct().Order())
        {
            var next = ActiveAt(boundary).FirstOrDefault();
            if (next?.Key == winner?.Key) continue;
            transition = boundary; nextWinner = next; break;
        }
        return new(winner, active, transition, nextWinner);
        IReadOnlyList<ScheduleOccurrence> ActiveAt(DateTimeOffset time) => occurrences.Where(o => o.StartUtc <= time && time < o.EndUtc)
            .OrderByDescending(o => o.Rule.Priority).ThenByDescending(o => o.StartUtc).ThenBy(o => o.Rule.Id.ToString("N"), StringComparer.Ordinal).ToArray();
    }
    // Gap boundaries advance to the first valid minute. Repeated hours start at the first and end at the last occurrence.
    public static DateTimeOffset Resolve(DateTime local, TimeZoneInfo zone, bool end)
    {
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? (end ? zone.GetAmbiguousTimeOffsets(local).Min() : zone.GetAmbiguousTimeOffsets(local).Max()) : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
