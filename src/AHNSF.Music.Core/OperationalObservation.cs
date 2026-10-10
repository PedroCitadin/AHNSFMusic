namespace AHNSF.Music.Core;
public static class OperationalObservation
{
    public static void Observe(this IOperationalHistoryService? history, OperationalEventType type, string component, string description,
        OperationalEventSeverity severity = OperationalEventSeverity.Information, OperationalActionOrigin origin = OperationalActionOrigin.Application,
        Guid? trackId = null, Guid? playlistId = null, Guid? ruleId = null, string? deviceId = null, string? dataJson = null)
    { try { history?.Record(type, component, description, severity, origin, trackId, playlistId, ruleId, deviceId, dataJson); } catch { /* Optional observation cannot alter operational behavior. */ } }
}
