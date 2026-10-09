using AHNSF.Music.Core;

namespace AHNSF.Music.Infrastructure;
public sealed class WindowsScheduleClock : IScheduleClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo TimeZone
    {
        get { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; }
    }
}
