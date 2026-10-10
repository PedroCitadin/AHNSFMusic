using AHNSF.Music.Desktop.Platform;
using Avalonia;

namespace AHNSF.Music.Desktop;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new("pt-BR");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new("pt-BR");
        // A second process must never create a second output device: it only asks the running one to show its window.
        var mutexName = SingleInstance.DefaultMutexName; var pipeName = SingleInstance.DefaultPipeName;
        var instance = SingleInstance.TryAcquire(mutexName, pipeName);
        if (instance is null)
        {
            if (SingleInstance.SignalExisting(pipeName, TimeSpan.FromSeconds(3))) return;
            // The owner did not answer: it may be finishing its shutdown. Wait briefly, then give up without a second player.
            instance = SingleInstance.TryAcquire(mutexName, pipeName, TimeSpan.FromSeconds(10));
            if (instance is null) return;
        }
        App.Instance = instance;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { instance.Dispose(); }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
