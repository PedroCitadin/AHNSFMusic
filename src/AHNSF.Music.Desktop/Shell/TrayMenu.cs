using System.ComponentModel;
using AHNSF.Music.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AHNSF.Music.Desktop.Shell;

/// <summary>Tray actions. Every playback/mode action goes through the single MainViewModel/PlaybackService/ScheduleCoordinator.</summary>
public sealed class TrayActions(MainViewModel viewModel, Action open, Action exit)
{
    public MainViewModel ViewModel { get; } = viewModel;
    public void Open() => open();
    public void TogglePlayback() => ViewModel.TrayPlayPauseCommand.Execute(null);
    public void Next() { if (!ViewModel.IsModalOpen) ViewModel.NextCommand.Execute(null); }
    public Task SetModeAsync(OperationMode mode) => ViewModel.IsModalOpen ? Task.CompletedTask : ViewModel.SetModeFromTrayAsync(mode);
    public void Exit() => exit();
}

/// <summary>Native Windows tray icon (Avalonia TrayIcon). Creation failures leave IsAvailable false so the window is never hidden.</summary>
public sealed class TrayMenu : IDisposable
{
    private readonly TrayActions actions;
    private readonly TrayIcon? icon;
    private readonly NativeMenuItem status = new() { IsEnabled = false };
    private readonly NativeMenuItem playPause = new();
    private readonly NativeMenuItem manual = new("Manual") { ToggleType = NativeMenuItemToggleType.Radio };
    private readonly NativeMenuItem automatic = new("Automático") { ToggleType = NativeMenuItemToggleType.Radio };
    public bool IsAvailable { get; }
    public Exception? Failure { get; }
    public TrayMenu(Application application, TrayActions actions, WindowIcon windowIcon)
    {
        this.actions = actions;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Área de notificação suportada somente no Windows.");
            var open = new NativeMenuItem("Abrir AHNSF Music"); open.Click += (_, _) => actions.Open();
            playPause.Click += (_, _) => actions.TogglePlayback();
            var next = new NativeMenuItem("Próxima música"); next.Click += (_, _) => actions.Next();
            manual.Click += (_, _) => _ = actions.SetModeAsync(OperationMode.Manual);
            automatic.Click += (_, _) => _ = actions.SetModeAsync(OperationMode.Automatic);
            var mode = new NativeMenuItem("Modo de operação") { Menu = new NativeMenu { Items = { manual, automatic } } };
            var exit = new NativeMenuItem("Sair"); exit.Click += (_, _) => actions.Exit();
            icon = new TrayIcon
            {
                Icon = windowIcon, ToolTipText = "AHNSF Music",
                Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), status, playPause, next, new NativeMenuItemSeparator(), mode, new NativeMenuItemSeparator(), exit } }
            };
            icon.Clicked += (_, _) => actions.Open();
            TrayIcon.SetIcons(application, new TrayIcons { icon });
            icon.IsVisible = true;
            IsAvailable = true;
            actions.ViewModel.PropertyChanged += OnViewModelChanged;
            Refresh();
        }
        catch (Exception ex) { Failure = ex; IsAvailable = false; icon = null; }
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.TrayStatus) or nameof(MainViewModel.TrayPlayPauseLabel) or nameof(MainViewModel.IsAutomaticMode))
            Dispatcher.UIThread.Post(Refresh);
    }
    private void Refresh()
    {
        if (icon is null) return;
        var vm = actions.ViewModel;
        status.Header = vm.TrayStatus; playPause.Header = vm.TrayPlayPauseLabel;
        manual.IsChecked = !vm.IsAutomaticMode; automatic.IsChecked = vm.IsAutomaticMode;
        var tip = $"AHNSF Music — {vm.TrayStatus}";
        icon.ToolTipText = tip.Length > 120 ? tip[..119] + "…" : tip; // Windows limits tray tooltips to 127 characters
    }
    public void Dispose()
    {
        actions.ViewModel.PropertyChanged -= OnViewModelChanged;
        if (icon is null) return;
        icon.IsVisible = false; icon.Dispose();
    }
    /// <summary>Application icon drawn at runtime (no binary asset): accent tile with a music note.</summary>
    public static WindowIcon CreateIcon()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#2E8A80")), null, new Rect(0, 0, 64, 64), 14, 14);
            var note = new FormattedText("♪", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Symbol", FontStyle.Normal, FontWeight.Bold), 46, Brushes.White);
            context.DrawText(note, new Point((64 - note.Width) / 2, (64 - note.Height) / 2));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return new WindowIcon(new MemoryStream(stream.ToArray()));
    }
}
