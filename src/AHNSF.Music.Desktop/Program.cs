using Avalonia;

namespace AHNSF.Music.Desktop;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new("pt-BR");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new("pt-BR");
        // A second process must never create a second output device.
        using var mutex = new Mutex(true, @"Local\AHNSF.Music.Desktop", out var first);
        if (!first) return;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { mutex.ReleaseMutex(); }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
