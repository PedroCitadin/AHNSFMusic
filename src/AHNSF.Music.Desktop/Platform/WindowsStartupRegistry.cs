using AHNSF.Music.Core;
using Microsoft.Win32;

namespace AHNSF.Music.Desktop.Platform;

/// <summary>HKCU Run key (per user, no elevation). StartupApproved is only read: Windows owns that decision.</summary>
public sealed class WindowsStartupRegistry : IStartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public IReadOnlyDictionary<string, string> ReadEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key is null) return entries;
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value) entries[name] = value;
        return entries;
    }
    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.SetValue(name, command, RegistryValueKind.String);
    }
    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(name, false);
    }
    public bool? IsApprovedByWindows(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false);
        // First byte: even = enabled, odd = disabled by the user in Task Manager/Settings.
        return key?.GetValue(name) is byte[] { Length: > 0 } data ? (data[0] & 1) == 0 : null;
    }
}
