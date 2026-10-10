using AHNSF.Music.Core;

namespace AHNSF.Music.Desktop.Shell;

public enum CloseDecision { HideToTray, ShowFirstCloseNotice, ConfirmExit, Exit }
/// <summary>Window/tray decisions, free of Avalonia so they can be tested directly.</summary>
public static class ShellPolicy
{
    /// <summary>Start hidden only for Windows auto-start with the option on, and only when a tray icon exists to reopen the window.</summary>
    public static bool StartHidden(bool autoStartLaunch, AppSettings settings, bool trayAvailable) => autoStartLaunch && settings.StartMinimized && trayAvailable;
    public static CloseDecision OnClose(AppSettings settings, bool trayAvailable, bool systemShutdown, bool musicAtRisk)
    {
        if (systemShutdown) return CloseDecision.Exit;
        // Without a tray the window must never become invisible and unreachable.
        if (settings.CloseBehavior == CloseBehavior.MinimizeToTray && trayAvailable)
            return settings.CloseNoticeShown ? CloseDecision.HideToTray : CloseDecision.ShowFirstCloseNotice;
        return OnExitRequest(musicAtRisk);
    }
    /// <summary>Explicit exit asks for confirmation whenever it would silence the building or stop the schedule.</summary>
    public static CloseDecision OnExitRequest(bool musicAtRisk) => musicAtRisk ? CloseDecision.ConfirmExit : CloseDecision.Exit;
}
