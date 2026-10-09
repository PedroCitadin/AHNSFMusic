using AHNSF.Music.Audio;
using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AHNSF.Music.Desktop;
public partial class App : Application
{
    private ServiceProvider? services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AHNSF Music");
            Directory.CreateDirectory(data);
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(data, "logs", "music-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14).CreateLogger();
            services = new ServiceCollection().AddSingleton<ILogger>(Log.Logger)
                .AddSingleton<IAudioEngine, NAudioEngine>().AddSingleton<ITrackDurationReader, Mp3DurationReader>().AddSingleton<IMusicScanner, MusicScanner>()
                .AddSingleton<ISettingsStore>(new SqliteSettingsStore(Path.Combine(data, "settings.db")))
                .AddSingleton<SqliteLibraryRepository>(new SqliteLibraryRepository(Path.Combine(data, "settings.db")))
                .AddSingleton<ILibraryRepository>(provider => provider.GetRequiredService<SqliteLibraryRepository>())
                .AddSingleton<SqlitePlaylistService>(provider => new SqlitePlaylistService(Path.Combine(data, "settings.db"), provider.GetRequiredService<SqliteLibraryRepository>()))
                .AddSingleton<IPlaylistService>(provider => provider.GetRequiredService<SqlitePlaylistService>())
                .AddSingleton<IScheduleService>(provider => new SqliteScheduleService(Path.Combine(data, "settings.db"), provider.GetRequiredService<SqlitePlaylistService>()))
                .AddSingleton<IScheduleClock, WindowsScheduleClock>().AddSingleton<IScheduleEvaluator, ScheduleEvaluator>()
                .AddSingleton<ScheduleCoordinator>(provider => new ScheduleCoordinator(provider.GetRequiredService<IScheduleService>(), provider.GetRequiredService<IPlaylistService>(), provider.GetRequiredService<IPlaybackService>(), provider.GetRequiredService<IScheduleClock>(), provider.GetRequiredService<IScheduleEvaluator>(), (message, error) => { if (error is null) Log.Information("{Schedule}", message); else Log.Warning(error, "{Schedule}", message); }))
                .AddSingleton<PlaybackService>(provider => new PlaybackService(provider.GetRequiredService<IAudioEngine>(), provider.GetRequiredService<IPlaylistService>(), action => Avalonia.Threading.Dispatcher.UIThread.Post(action)))
                .AddSingleton<IPlaybackService>(provider => provider.GetRequiredService<PlaybackService>())
                .AddSingleton<IArtworkCache>(new ArtworkCache(Path.Combine(data, "artwork"), Log.Logger))
                .AddSingleton<ITrackMetadataReader, TrackMetadataReader>()
                .AddSingleton<ILibraryService, LibraryService>()
                .AddSingleton<MainViewModel>().BuildServiceProvider();
            Controls.ArtworkView.ResolvePath = services.GetRequiredService<IArtworkCache>().GetPath;
            Controls.ArtworkView.LogError = ex => Log.Warning(ex, "Falha ao carregar miniatura na interface");
            desktop.MainWindow = new MainWindow(services.GetRequiredService<MainViewModel>());
            desktop.Exit += (_, _) => { services.DisposeAsync().AsTask().GetAwaiter().GetResult(); Log.CloseAndFlush(); };
            Log.Information("AHNSF Music iniciado");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
