using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AHNSF.Music.Desktop.Controls;

// Only realized virtualized rows own bitmaps. No library-wide bitmap collection.
public sealed class ArtworkView : Border
{
    private static readonly SemaphoreSlim decoding = new(2, 2);
    public static Func<string?, string?>? ResolvePath { get; set; }
    public static Action<Exception>? LogError { get; set; }
    public static readonly StyledProperty<string?> CacheKeyProperty = AvaloniaProperty.Register<ArtworkView, string?>(nameof(CacheKey));
    public string? CacheKey { get => GetValue(CacheKeyProperty); set => SetValue(CacheKeyProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock placeholder = new() { Text = "♫", FontSize = 22, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private CancellationTokenSource? loading;
    private Bitmap? bitmap;
    private bool attached;
    // Default size as a default value (not a local value) so styles such as rowArt/heroArt can resize it.
    static ArtworkView() { WidthProperty.OverrideDefaultValue<ArtworkView>(46); HeightProperty.OverrideDefaultValue<ArtworkView>(46); }
    public ArtworkView()
    {
        CornerRadius = new CornerRadius(6); ClipToBounds = true;
        // Theme tokens (0.8.3): placeholder follows the active accent and theme.
        this[!BackgroundProperty] = this.GetResourceObservable("AccentSubtleBrush").ToBinding();
        placeholder[!TextBlock.ForegroundProperty] = placeholder.GetResourceObservable("AccentTextBrush").ToBinding();
        SizeChanged += (_, args) => placeholder.FontSize = Math.Clamp(args.NewSize.Height * 0.42, 14, 72);
        var content = new Grid(); content.Children.Add(placeholder); content.Children.Add(image); Child = content;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == CacheKeyProperty && attached) Load(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    { base.OnAttachedToVisualTree(args); attached = true; Load(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    { attached = false; Release(); base.OnDetachedFromVisualTree(args); }
    private void Release()
    { loading?.Cancel(); loading?.Dispose(); loading = null; image.Source = null; bitmap?.Dispose(); bitmap = null; placeholder.IsVisible = true; }
    private async void Load()
    {
        Release();
        var path = ResolvePath?.Invoke(CacheKey); if (path is null) return;
        loading = new CancellationTokenSource(); var token = loading.Token;
        Bitmap? decoded = null;
        try
        {
            await decoding.WaitAsync(token);
            try { decoded = await Task.Run(() => { token.ThrowIfCancellationRequested(); return new Bitmap(path); }, token); }
            finally { decoding.Release(); }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!attached || token.IsCancellationRequested) return;
                bitmap = decoded; decoded = null; image.Source = bitmap; placeholder.IsVisible = false;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogError?.Invoke(ex); }
        finally { decoded?.Dispose(); }
    }
}
