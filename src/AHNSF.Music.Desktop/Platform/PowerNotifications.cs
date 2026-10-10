using System.Runtime.InteropServices;

namespace AHNSF.Music.Desktop.Platform;

/// <summary>
/// Suspend/resume notifications without a window (PowerRegisterSuspendResumeNotification, Windows 8+).
/// The schedule coordinator also detects time gaps on its own, so these events are an accelerator, not a dependency.
/// </summary>
public sealed class PowerNotifications : IDisposable
{
    private const uint DeviceNotifyCallback = 2, Suspend = 4, ResumeSuspend = 7, ResumeAutomatic = 18;
    private delegate uint Callback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] private struct SubscribeParameters { public IntPtr Callback; public IntPtr Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref SubscribeParameters recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);
    private readonly Callback callback;
    private IntPtr handle;
    private int suspended;
    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public bool IsRegistered => handle != IntPtr.Zero;
    public PowerNotifications()
    {
        callback = OnPowerEvent; // kept alive for the lifetime of the registration
        var parameters = new SubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref parameters, out handle) != 0) handle = IntPtr.Zero;
    }
    private uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type == Suspend) { Interlocked.Exchange(ref suspended, 1); Suspending?.Invoke(this, EventArgs.Empty); }
            // Windows may send both resume codes; handle the first one after each suspend.
            else if ((type == ResumeAutomatic || type == ResumeSuspend) && Interlocked.Exchange(ref suspended, 0) == 1) Resumed?.Invoke(this, EventArgs.Empty);
        }
        catch { /* never throw into the power subsystem */ }
        return 0;
    }
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        PowerUnregisterSuspendResumeNotification(handle); handle = IntPtr.Zero;
    }
}
