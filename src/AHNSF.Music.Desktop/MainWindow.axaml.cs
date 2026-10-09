using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace AHNSF.Music.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = null!;
    private bool closingSaved;
    private bool savingClose;
    private Point dragStart;
    private (Guid Playlist, Guid Item)? dragCandidate;
    private ListBoxItem? dropTarget;
    public MainWindow() => InitializeComponent();
    public MainWindow(MainViewModel viewModel) : this()
    {
        this.viewModel = viewModel; DataContext = viewModel;
        SizeChanged += (_, _) =>
        {
            if (ClientSize.Width < 1080) Classes.Add("compact"); else Classes.Remove("compact");
            // 1366×768 leaves about 690 px of client height once the taskbar and title bar are removed.
            if (ClientSize.Height < 800) Classes.Add("short"); else Classes.Remove("short");
        };
        KeyDown += (_, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.F)
            { viewModel.NavigateCommand.Execute("Biblioteca"); Avalonia.Threading.Dispatcher.UIThread.Post(() => LibraryPage.FindControl<TextBox>("LibrarySearch")?.Focus()); args.Handled = true; }
            else if (args.Key == Key.Escape)
            { viewModel.CancelDestructiveCommand.Execute(null); viewModel.CancelDeletePlaylistCommand.Execute(null); viewModel.CancelDeleteRuleCommand.Execute(null); args.Handled = true; }
        };
        LibraryPage.FindControl<ListBox>("MusicList")!.DoubleTapped += (_, _) => viewModel.PlaySelectedCommand.Execute(null);
        PlaylistsPage.FindControl<ListBox>("PlaylistList")!.DoubleTapped += (_, _) => viewModel.PlayPlaylistItemCommand.Execute(null);
        QueuePage.FindControl<ListBox>("QueueList")!.DoubleTapped += (_, _) => viewModel.PlayQueueItemCommand.Execute(null);
        var playlistList = PlaylistsPage.FindControl<ListBox>("PlaylistList")!;
        DragDrop.SetAllowDrop(playlistList, true);
        playlistList.AddHandler(PointerPressedEvent, (_, args) =>
        {
            dragCandidate = null;
            if (!args.GetCurrentPoint(playlistList).Properties.IsLeftButtonPressed || viewModel.SelectedPlaylist is null) return;
            if (FindRow(args.Source) is { } row)
            { dragStart = args.GetPosition(playlistList); dragCandidate = (viewModel.SelectedPlaylist.Id, row.Id); }
        }, RoutingStrategies.Tunnel);
        playlistList.PointerReleased += (_, _) => dragCandidate = null;
        DragDrop.AddDragLeaveHandler(playlistList, (_, _) => SetDropTarget(null));
        playlistList.PointerMoved += async (_, args) =>
        {
            var pointer = args.GetPosition(playlistList);
            if (dragCandidate is not { } candidate || !args.GetCurrentPoint(playlistList).Properties.IsLeftButtonPressed || Math.Abs(pointer.X - dragStart.X) + Math.Abs(pointer.Y - dragStart.Y) < 6) return;
            dragCandidate = null;
            try
            {
                var data = new DataTransfer(); data.Add(DataTransferItem.CreateText($"AHNSF:{candidate.Playlist}:{candidate.Item}"));
                await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
            }
            catch (Exception ex) { viewModel.ReportPlaylistInteractionError(ex); }
            finally { SetDropTarget(null); }
        };
        DragDrop.AddDragOverHandler(playlistList, (sender, args) =>
        {
            var valid = TryDragItem(args, out _, out _) && FindRow(args.Source) is not null;
            args.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
            SetDropTarget(valid ? FindItem(args.Source) : null);
            args.Handled = true;
        });
        DragDrop.AddDropHandler(playlistList, async (_, args) =>
        {
            SetDropTarget(null);
            if (!TryDragItem(args, out var playlistId, out var source) || FindRow(args.Source) is not { } target) return;
            args.Handled = true;
            await viewModel.MovePlaylistItemToAsync(playlistId, source, target.Id);
        });
        LibraryPage.FindControl<ListBox>("MusicList")!.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                new MenuItem { Header = "Adicionar à playlist escolhida", Command = viewModel.AddToPlaylistCommand },
                new MenuItem { Header = "Tocar a seguir", Command = viewModel.AddAsNextCommand },
                new MenuItem { Header = "Adicionar ao final da fila", Command = viewModel.AddToQueueCommand }
            }
        };
        playlistList.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                new MenuItem { Header = "Reproduzir ocorrência", Command = viewModel.PlayPlaylistItemCommand },
                new MenuItem { Header = "Mover para cima", Command = viewModel.MovePlaylistUpCommand },
                new MenuItem { Header = "Mover para baixo", Command = viewModel.MovePlaylistDownCommand },
                new MenuItem { Header = "Remover selecionadas…", Command = viewModel.RequestRemovePlaylistItemsCommand }
            }
        };
        QueuePage.FindControl<ListBox>("QueueList")!.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                new MenuItem { Header = "Reproduzir ocorrência", Command = viewModel.PlayQueueItemCommand },
                new MenuItem { Header = "Remover selecionadas…", Command = viewModel.RequestRemoveQueueItemsCommand },
                new MenuItem { Header = "Limpar fila…", Command = viewModel.RequestClearQueueCommand }
            }
        };
        viewModel.PickDirectory = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Selecionar pasta de músicas", AllowMultiple = false });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };
        Opened += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            var settings = viewModel.Settings;
            Width = Math.Clamp(settings.Width, 900, 3000); Height = Math.Clamp(settings.Height, 620, 2000);
            if (settings.X is int x && settings.Y is int y)
            {
                var point = new PixelPoint(x, y);
                if (Screens.All.Any(screen => screen.WorkingArea.Contains(point))) Position = point;
            }
        };
        Closing += async (_, args) =>
        {
            if (closingSaved) return;
            args.Cancel = true;
            if (savingClose) return;
            savingClose = true;
            viewModel.StopCommand.Execute(null);
            await viewModel.SaveWindowAsync(Width, Height, Position.X, Position.Y);
            await viewModel.DisposeAsync(); closingSaved = true; Close();
        };
    }
    private static ListBoxItem? FindItem(object? source) =>
        source is Control control ? control as ListBoxItem ?? control.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() : null;
    private static MainViewModel.MusicRow? FindRow(object? source) => FindItem(source)?.DataContext as MainViewModel.MusicRow;
    // Visual drop indicator only; reordering is still persisted by MovePlaylistItemToAsync.
    private void SetDropTarget(ListBoxItem? item)
    {
        if (ReferenceEquals(dropTarget, item)) return;
        dropTarget?.Classes.Remove("dropTarget"); dropTarget = item; dropTarget?.Classes.Add("dropTarget");
    }
    private bool TryDragItem(DragEventArgs args, out Guid playlistId, out Guid itemId)
    {
        playlistId = itemId = Guid.Empty;
        var parts = args.DataTransfer.TryGetText()?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != "AHNSF" || !Guid.TryParse(parts[1], out playlistId) || !Guid.TryParse(parts[2], out itemId) || viewModel.SelectedPlaylist?.Id != playlistId) return false;
        var candidate = itemId;
        return viewModel.PlaylistRows.Any(row => row.Id == candidate);
    }
}
