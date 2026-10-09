namespace AHNSF.Music.Core;

[Flags] public enum ScheduleDays { None = 0, Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64, All = 127 }
public enum OperationMode { Manual, Automatic }
public sealed record ScheduleRule(Guid Id, string Name, Guid? PlaylistId, ScheduleDays Days, TimeOnly StartTime,
    TimeOnly EndTime, double Volume, int Priority, bool IsEnabled, DateTime CreatedAtUtc, DateTime UpdatedAtUtc)
{
    public string EnabledText => IsEnabled ? "Habilitada" : "Desativada";
    public static void Validate(ScheduleRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 200) throw new ArgumentException("Informe um nome de até 200 caracteres.");
        if (rule.PlaylistId is null || rule.PlaylistId == Guid.Empty) throw new ArgumentException("Escolha uma playlist válida.");
        if (rule.Days == ScheduleDays.None || (rule.Days & ~ScheduleDays.All) != 0) throw new ArgumentException("Selecione pelo menos um dia da semana.");
        if (rule.StartTime == rule.EndTime) throw new ArgumentException("Os horários de início e fim devem ser diferentes.");
        if (!double.IsFinite(rule.Volume) || rule.Volume < 0 || rule.Volume > 100) throw new ArgumentException("O volume deve estar entre 0 e 100%.");
        if (rule.Priority < 0 || rule.Priority > 1000) throw new ArgumentException("A prioridade deve estar entre 0 e 1000.");
    }
}
public sealed record ScheduleConfiguration(OperationMode Mode, IReadOnlyList<ScheduleRule> Rules);
public interface IScheduleService
{
    event EventHandler? Changed;
    Task<ScheduleConfiguration> LoadAsync(CancellationToken token = default);
    Task<ScheduleRule> SaveAsync(ScheduleRule rule, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token = default);
    Task SetModeAsync(OperationMode mode, CancellationToken token = default);
}
public interface IScheduleClock { DateTimeOffset UtcNow { get; } TimeZoneInfo TimeZone { get; } }
public sealed record ScheduleOccurrence(ScheduleRule Rule, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public string Key => $"{Rule.Id:N}/{StartUtc.UtcTicks}/{Rule.PlaylistId}";
}
public sealed record ScheduleEvaluation(ScheduleOccurrence? Winner, IReadOnlyList<ScheduleOccurrence> Active,
    DateTimeOffset? NextTransitionUtc, ScheduleOccurrence? NextWinner);
public interface IScheduleEvaluator
{ ScheduleEvaluation Evaluate(IReadOnlyList<ScheduleRule> rules, DateTimeOffset now, TimeZoneInfo zone); }
public sealed record ScheduleStatus(OperationMode Mode, ScheduleEvaluation Evaluation, bool IsSuspended, PlayerState PlaybackState,
    string? PlaylistName, string Warning, string Reason);
public sealed record ScheduleCoordinatorOptions(TimeSpan Interval, TimeSpan RetryDelay)
{ public static ScheduleCoordinatorOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)); }
