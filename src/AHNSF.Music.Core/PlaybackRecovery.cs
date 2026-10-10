namespace AHNSF.Music.Core;

public enum FailureKind { Transient, Permanent }
/// <summary>Bounded recovery for scheduled playback. The ScheduleCoordinator is its only user.</summary>
public sealed record RecoveryPolicy(IReadOnlyList<TimeSpan> Delays)
{
    public static RecoveryPolicy Default { get; } = new([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)]);
    public int MaxAttempts => Delays.Count;
    /// <summary>Delay before the recovery attempt that follows the given consecutive failure (1-based); null when exhausted.</summary>
    public TimeSpan? DelayAfter(int failures) => failures >= 1 && failures <= Delays.Count ? Delays[failures - 1] : null;
    /// <summary>Media/file problems repeat on every attempt; device, driver and engine problems may clear by themselves.</summary>
    public static FailureKind Classify(Exception? error) => error switch
    {
        null => FailureKind.Transient,
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => FailureKind.Permanent,
        InvalidDataException or FormatException or NotSupportedException or EndOfStreamException => FailureKind.Permanent,
        _ when error.InnerException is { } inner && error is not ScheduledPlaybackException => Classify(inner),
        _ => FailureKind.Transient
    };
}
public sealed class ScheduledPlaybackException(string message, Exception? audioError) : Exception(message, audioError);
public sealed record RecoveryStatus(int ConsecutiveFailures, bool Exhausted, DateTimeOffset? NextAttemptUtc, int LastEpisodeAttempts,
    DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastRecoveredUtc, string? LastError, DateTimeOffset? LastErrorUtc)
{
    public static RecoveryStatus Idle { get; } = new(0, false, null, 0, null, null, null, null);
    public bool IsRecovering => ConsecutiveFailures > 0 && !Exhausted;
}
