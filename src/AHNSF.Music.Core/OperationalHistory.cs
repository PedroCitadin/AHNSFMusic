namespace AHNSF.Music.Core;

public enum OperationalEventSeverity { Information, Warning, Error }
public enum OperationalEventType
{
    ApplicationStarted, ApplicationAutoStarted, ExitRequested, ApplicationEnded, PreviousSessionUnconfirmed, ComputerSuspended, ComputerResumed,
    PlaybackStarted, PlaybackPaused, PlaybackResumed, PlaybackStopped, PlaybackEnded, AutomaticAdvance, ManualTrackChange, PlaybackError,
    RuleActivated, RuleEnded, WinnerChanged, AutomaticMode, ManualMode, ManualIntervention, AutomationSuspended, RuleFailed,
    OutputSelected, OutputChanged, DeviceUnavailable, DeviceDisconnected, DeviceReconnected, DeviceOpenFailed,
    RecoveryAttempt, RecoverySucceeded, RecoveryExhausted, IndexingStarted, IndexingCompleted, LibraryUnavailable, LibraryAccessFailed, LibraryReadError, LibraryRecovered,
    HistoryGap, ImportStarted, ImportCompleted, ImportCancelled, ImportFailed
}
public enum OperationalActionOrigin { Application, Manual, Schedule, Recovery, Library, Device }
public sealed record OperationalEvent(Guid Id, DateTimeOffset TimestampUtc, OperationalEventType Type, OperationalEventSeverity Severity,
    string Component, string Description, Guid SessionId, OperationalActionOrigin Origin = OperationalActionOrigin.Application,
    Guid? TrackId = null, Guid? PlaylistId = null, Guid? RuleId = null, string? DeviceId = null, Guid? PlaybackId = null, string DataJson = "{}");
public sealed record OperationalSession(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, double? DurationSeconds,
    string FinalState, int RelevantErrors);
public sealed record PlaybackPeriod(Guid Id, Guid SessionId, Guid? TrackId, Guid? PlaylistId, Guid? RuleId, string? DeviceId,
    string TrackTitle, string PlaylistName, OperationalActionOrigin Origin, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    double? EffectiveSeconds, string EndReason, bool Confirmed = true);
public sealed record OperationalHistoryQuery(DateTimeOffset? FromUtc = null, DateTimeOffset? UntilUtc = null,
    OperationalEventType? Type = null, OperationalEventSeverity? Severity = null, string Search = "", int Page = 0, int PageSize = 50,
    DateTimeOffset? BeforeUtc = null, Guid? BeforeId = null);
public sealed record OperationalHistoryPage(IReadOnlyList<OperationalEvent> Events, int Total);
public sealed record OperationalHistorySummary(int RecentFailures, OperationalEvent? LastError, OperationalEvent? LastRecovery, OperationalEvent? LastDisconnect);
public sealed record OperationalWrite(OperationalEvent Event, OperationalSession? Session = null, PlaybackPeriod? Playback = null);
public interface IOperationalHistoryRepository
{
    Task InitializeAsync(CancellationToken token);
    Task MarkUnconfirmedAsync(Guid currentSession, CancellationToken token);
    Task AppendAsync(IReadOnlyList<OperationalWrite> entries, CancellationToken token);
    Task<OperationalHistoryPage> QueryAsync(OperationalHistoryQuery query, CancellationToken token);
    Task<IReadOnlyList<OperationalSession>> SessionsAsync(int page, CancellationToken token);
    Task<IReadOnlyList<PlaybackPeriod>> PlaybackAsync(OperationalHistoryQuery query, CancellationToken token);
    Task<IReadOnlyList<PlaybackPeriod>> PlaybackByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken token);
    Task<OperationalHistorySummary> SummaryAsync(DateTimeOffset since, CancellationToken token);
    Task<int> RetainAsync(DateTimeOffset cutoff, CancellationToken token);
}
public interface IOperationalHistoryService : IAsyncDisposable
{
    Guid SessionId { get; }
    string? Failure { get; }
    long DroppedEvents { get; }
    void Record(OperationalEventType type, string component, string description, OperationalEventSeverity severity = OperationalEventSeverity.Information,
        OperationalActionOrigin origin = OperationalActionOrigin.Application, Guid? trackId = null, Guid? playlistId = null, Guid? ruleId = null,
        string? deviceId = null, string? dataJson = null);
    void Playback(OperationalEventType type, Track? track, PlaybackOrigin? playlist, OperationalActionOrigin origin, string reason,
        Guid? ruleId = null, string? deviceId = null);
    Task StartAsync(bool autoStart, int retentionDays = 90, CancellationToken token = default);
    void SetRetention(int days);
    Task FlushAsync(CancellationToken token = default);
    Task EndAsync(string finalState, CancellationToken token = default);
    Task<OperationalHistoryPage> QueryAsync(OperationalHistoryQuery query, CancellationToken token = default);
    Task<IReadOnlyList<OperationalSession>> SessionsAsync(int page = 0, CancellationToken token = default);
    Task<IReadOnlyList<PlaybackPeriod>> PlaybackAsync(OperationalHistoryQuery query, CancellationToken token = default);
    Task<OperationalHistorySummary> SummaryAsync(CancellationToken token = default);
    Task ExportAsync(string path, OperationalHistoryQuery query, bool overwrite = false, CancellationToken token = default);
}
