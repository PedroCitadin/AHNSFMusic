using AHNSF.Music.Audio;
using AHNSF.Music.Core;
using AHNSF.Music.Desktop.Platform;
using AHNSF.Music.Desktop.Shell;
using AHNSF.Music.Desktop.Theming;
using AHNSF.Music.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AHNSF.Music.Desktop;
public partial class App : Application
{
    private const string ExitPrompt = "A reprodução será interrompida e a programação automática deixará de funcionar até o aplicativo ser aberto novamente.";
    private ServiceProvider? services;
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private MainViewModel? viewModel;
    private MainWindow? window;
    private TrayMenu? tray;
    private IOperationalHistoryService? history;
    private PowerNotifications? power;
    private bool exiting, servicesDisposed;
    /// <summary>Single-instance owner created by Program before Avalonia starts.</summary>
    internal static SingleInstance? Instance { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            // The tray keeps the process alive while the window is hidden; exit is always explicit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AHNSF Music");
            Directory.CreateDirectory(data);
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(data, "logs", "music-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true).CreateLogger();
            services = new ServiceCollection().AddSingleton<ILogger>(Log.Logger)
                .AddSingleton<NAudioEngine>().AddSingleton<IAudioEngine>(provider => provider.GetRequiredService<NAudioEngine>())
                .AddSingleton<IOperationalHistoryRepository>(provider => new SqliteOperationalHistoryRepository(Path.Combine(data, "settings.db"), provider.GetRequiredService<SqliteLibraryRepository>()))
                .AddSingleton<IOperationalHistoryService>(provider => new OperationalHistoryService(provider.GetRequiredService<IOperationalHistoryRepository>(), (message, error) => { if (error is null) Log.Warning("{History}", message); else Log.Warning(error, "{History}", message); }))
                .AddSingleton<IAudioDeviceDiscovery, WindowsAudioDeviceDiscovery>()
                .AddSingleton<IAudioDeviceService>(provider => new AudioDeviceService(provider.GetRequiredService<IAudioDeviceDiscovery>(), provider.GetRequiredService<NAudioEngine>(), (message, error) => { if (error is null) Log.Information("{AudioDevice}", message); else Log.Warning(error, "{AudioDevice}", message); }, history: provider.GetRequiredService<IOperationalHistoryService>()))
                .AddSingleton<ITrackDurationReader, Mp3DurationReader>().AddSingleton<IMusicScanner, MusicScanner>()
                .AddSingleton<ISettingsStore>(new SqliteSettingsStore(Path.Combine(data, "settings.db")))
                .AddSingleton<SqliteLibraryRepository>(new SqliteLibraryRepository(Path.Combine(data, "settings.db")))
                .AddSingleton<ILibraryRepository>(provider => provider.GetRequiredService<SqliteLibraryRepository>())
                .AddSingleton<SqlitePlaylistService>(provider => new SqlitePlaylistService(Path.Combine(data, "settings.db"), provider.GetRequiredService<SqliteLibraryRepository>()))
                .AddSingleton<IPlaylistService>(provider => provider.GetRequiredService<SqlitePlaylistService>())
                .AddSingleton<IScheduleService>(provider => new SqliteScheduleService(Path.Combine(data, "settings.db"), provider.GetRequiredService<SqlitePlaylistService>()))
                .AddSingleton<IScheduleClock, WindowsScheduleClock>().AddSingleton<IScheduleEvaluator, ScheduleEvaluator>()
                .AddSingleton<ScheduleCoordinator>(provider => new ScheduleCoordinator(provider.GetRequiredService<IScheduleService>(), provider.GetRequiredService<IPlaylistService>(), provider.GetRequiredService<IPlaybackService>(), provider.GetRequiredService<IScheduleClock>(), provider.GetRequiredService<IScheduleEvaluator>(), (message, error) => { if (error is null) Log.Information("{Schedule}", message); else Log.Warning(error, "{Schedule}", message); }, devices: provider.GetRequiredService<IAudioDeviceService>(), history: provider.GetRequiredService<IOperationalHistoryService>()))
                .AddSingleton<PlaybackService>(provider => new PlaybackService(provider.GetRequiredService<IAudioEngine>(), provider.GetRequiredService<IPlaylistService>(), action => Dispatcher.UIThread.Post(action), provider.GetRequiredService<IOperationalHistoryService>()))
                .AddSingleton<IPlaybackService>(provider => provider.GetRequiredService<PlaybackService>())
                .AddSingleton<IArtworkCache>(new ArtworkCache(Path.Combine(data, "artwork"), Log.Logger))
                .AddSingleton<ITrackMetadataReader, TrackMetadataReader>()
                .AddSingleton<ILibraryService, LibraryService>()
                .AddSingleton<YouTubeCookieSetting>()
                .AddSingleton<IImportMetadataProvider>(provider => new SpotDlProcessAdapter(AppContext.BaseDirectory, Log.Logger, provider.GetRequiredService<YouTubeCookieSetting>()))
                .AddSingleton<IPlaylistImportService, PlaylistImportService>()
                .AddSingleton(new AutoStartService(new WindowsStartupRegistry(), Environment.ProcessPath))
                .AddSingleton(new ThemeService(new ApplicationThemeTarget(this), new PlatformSystemThemeSource(PlatformSettings)))
                .AddSingleton<MainViewModel>().BuildServiceProvider();
            Controls.ArtworkView.ResolvePath = services.GetRequiredService<IArtworkCache>().GetPath;
            Controls.ArtworkView.LogError = ex => Log.Warning(ex, "Falha ao carregar miniatura na interface");
            var autoLaunch = AutoStartService.IsAutoStartLaunch(desktop.Args ?? []);
            Log.Information(autoLaunch ? "AHNSF Music iniciado automaticamente pelo Windows" : "AHNSF Music iniciado");
            if (Instance?.RecoveredAbandoned == true) Log.Warning("A execução anterior terminou de forma inesperada; o controle de instância única foi recuperado");
            var icon = TrayMenu.CreateIcon();
            history = services.GetRequiredService<IOperationalHistoryService>();
            var theme = services.GetRequiredService<ThemeService>();
            theme.Apply(new AppearanceSettings());
            viewModel = services.GetRequiredService<MainViewModel>();
            viewModel.AttachTheme(theme);
            viewModel.LaunchedByWindows = autoLaunch;
            window = new MainWindow(viewModel) { Icon = icon };
            window.CloseRequested += OnWindowCloseRequested;
            tray = new TrayMenu(this, new TrayActions(viewModel, ShowMainWindow, RequestExit), icon);
            if (!tray.IsAvailable) Log.Warning(tray.Failure, "Área de notificação indisponível; fechar a janela encerrará o aplicativo");
            viewModel.TrayAvailable = tray.IsAvailable;
            StartPowerNotifications();
            Instance?.Listen(() => Dispatcher.UIThread.Post(() => { Log.Information("Nova execução solicitada: restaurando a janela existente"); ShowMainWindow(); }),
                ex => Log.Warning(ex, "Falha no canal local de instância única"));
            desktop.ShutdownRequested += (_, args) => { if (!exiting) { args.Cancel = true; _ = ExitAsync("Sessão do Windows encerrando"); } };
            desktop.Exit += (_, _) =>
            {
                // Fallback only: the normal path already disposed everything in ExitAsync.
                if (!servicesDisposed && services is not null) { servicesDisposed = true; services.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                Log.Information("AHNSF Music encerrado");
                Log.CloseAndFlush();
            };
            _ = StartAsync(autoLaunch);
        }
        base.OnFrameworkInitializationCompleted();
    }
    private async Task StartAsync(bool autoLaunch)
    {
        var hidden = false;
        AppSettings? stored = null;
        try { stored = await services!.GetRequiredService<ISettingsStore>().LoadAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Preferências não puderam ser lidas antes de exibir a janela; serão usados os padrões"); }
        // Theme first, so the window never flashes the default appearance.
        if (stored?.Appearance is { } appearance) services!.GetRequiredService<ThemeService>().Apply(appearance);
        if (autoLaunch && tray?.IsAvailable == true)
        {
            if (stored is not null) hidden = ShellPolicy.StartHidden(true, stored, true);
            else Log.Warning("Preferência de inicialização minimizada não pôde ser lida; a janela será exibida");
        }
        if (hidden) Log.Information("Iniciado minimizado na bandeja");
        else ShowMainWindow();
        // Services, library and schedule start whether or not the window is visible.
        try { await viewModel!.InitializeAsync(); }
        catch (Exception ex) { Log.Error(ex, "Falha na inicialização dos serviços"); }
    }
    private void StartPowerNotifications()
    {
        try
        {
            power = new PowerNotifications();
            if (!power.IsRegistered) Log.Warning("Notificações de suspensão/retomada indisponíveis; a programação continua detectando saltos de relógio");
            power.Suspending += (_, _) => Dispatcher.UIThread.Post(() => { history.Observe(OperationalEventType.ComputerSuspended, "Aplicativo", "Suspensão do computador observada."); Log.Information("Suspensão do Windows"); viewModel?.OnSystemSuspending(); });
            power.Resumed += (_, _) => Dispatcher.UIThread.Post(async () => { history.Observe(OperationalEventType.ComputerResumed, "Aplicativo", "Retomada do computador observada."); Log.Information("Retomada do Windows"); if (viewModel is not null) await viewModel.OnSystemResumedAsync(); });
        }
        catch (Exception ex) { Log.Warning(ex, "Notificações de suspensão/retomada indisponíveis"); }
    }
    private void ShowMainWindow()
    {
        if (window is null || desktop is null || exiting) return;
        desktop.MainWindow ??= window;
        var restoring = window.HasOpened && !window.IsVisible;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (restoring) Log.Information("Janela restaurada a partir da bandeja");
    }
    private void HideToTray()
    {
        if (window is null || tray?.IsAvailable != true) { ShowMainWindow(); return; }
        window.Hide();
        Log.Information("Janela minimizada para a bandeja");
    }
    private void OnWindowCloseRequested(WindowCloseReason reason)
    {
        if (viewModel is null || exiting) return;
        var system = reason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;
        switch (ShellPolicy.OnClose(viewModel.Settings, tray?.IsAvailable == true, system, viewModel.MusicAtRisk))
        {
            case CloseDecision.HideToTray: HideToTray(); break;
            case CloseDecision.ShowFirstCloseNotice:
                viewModel.RequestDialog("Continuar na área de notificação?",
                    "O AHNSF Music continuará em execução no ícone ao lado do relógio do Windows, e a programação seguirá ativa. Para encerrar de fato, use “Sair” no menu desse ícone. Você pode mudar este comportamento em Configurações.",
                    "Minimizar para a bandeja", async () => { await viewModel.MarkCloseNoticeShownAsync(); HideToTray(); }, danger: false,
                    "Encerrar o aplicativo", async () => { await viewModel.MarkCloseNoticeShownAsync(); RequestExit(); });
                break;
            case CloseDecision.ConfirmExit: ConfirmExit("Janela fechada"); break;
            default: _ = ExitAsync(system ? "Encerramento do Windows" : "Janela fechada"); break;
        }
    }
    private void RequestExit()
    {
        if (viewModel is null || exiting) return;
        if (ShellPolicy.OnExitRequest(viewModel.MusicAtRisk) == CloseDecision.Exit) _ = ExitAsync("Sair");
        else ConfirmExit("Sair");
    }
    private void ConfirmExit(string reason)
    {
        ShowMainWindow();
        viewModel!.RequestDialog("Encerrar o AHNSF Music?", ExitPrompt, "Encerrar", () => ExitAsync(reason), danger: true);
    }
    /// <summary>Stops scheduling, cancels work, stops audio, saves, disposes and ends the process. A timeout prevents a hung exit.</summary>
    private async Task ExitAsync(string reason)
    {
        if (exiting) return;
        exiting = true;
        history.Observe(OperationalEventType.ExitRequested, "Aplicativo", "Encerramento solicitado: " + reason, origin: reason is "Sair" or "Janela fechada" ? OperationalActionOrigin.Manual : OperationalActionOrigin.Application);
        Log.Information("Encerramento solicitado: {Reason}", reason);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await ShutdownServicesAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Log.Information("Agendador parado, áudio liberado e configurações salvas em {Elapsed:0} ms", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (TimeoutException) { Log.Warning("O encerramento excedeu 15 s; o processo será finalizado mesmo assim"); }
        catch (Exception ex) { Log.Error(ex, "Falha durante o encerramento"); }
        finally
        {
            tray?.Dispose(); power?.Dispose();
            if (window is not null) { window.AllowClose = true; window.Close(); }
            desktop?.Shutdown();
        }
    }
    private async Task ShutdownServicesAsync()
    {
        if (viewModel is not null) await viewModel.ShutdownAsync(window?.HasOpened == true ? window.CaptureGeometry : null);
        if (history is not null) { using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(3)); try { await history.EndAsync("Player parado; serviços encerrados", flush.Token); } catch (OperationCanceledException) { Log.Warning("Histórico sem confirmação de flush no encerramento"); } }
        if (services is not null && !servicesDisposed) { servicesDisposed = true; await services.DisposeAsync(); }
    }
}
