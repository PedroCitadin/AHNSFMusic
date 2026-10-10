using AHNSF.Music.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

public partial class MainViewModel
{
    private readonly IPlaylistImportService? importService;
    private CancellationTokenSource? importCancellation;
    private ImportPreview? importPreview;
    public bool ShowImport => Section == "Importar músicas";
    public Func<Task<IReadOnlyList<string>>>? PickImportFiles { get; set; }
    public sealed partial class ImportRow : ObservableObject
    {
        public ImportRow(ImportTrack track) { Track = track; isSelected = track.Available || track.Downloadable; }
        public ImportTrack Track { get; }
        [ObservableProperty] private bool isSelected;
        public string Title => $"{Track.Position + 1}. {Track.Title}";
        public string Detail => $"{Track.Artist} • {(Track.Duration > TimeSpan.Zero ? Track.Duration.ToString(@"mm\:ss") : "duração desconhecida")}";
        public string Availability => Track.ExistingTrackId is not null ? "Já na biblioteca • não será baixada novamente" : Track.LocalPath is not null ? "Arquivo local pronto"
            : Track.Downloadable ? "Será baixada" : Track.Issue ?? "Áudio indisponível";
    }
    [ObservableProperty] private string importUrl = "";
    [ObservableProperty] private string importPlaylistName = "";
    [ObservableProperty] private string importToolMessage = "Verificando ferramentas internas…";
    [ObservableProperty] private string importMessage = "Cole o link de uma playlist do Spotify e clique em Analisar.";
    [ObservableProperty] private string importSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ImportCanStart))] private bool importBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ImportCanStart))] private bool importRightsConfirmed;
    [ObservableProperty] private IReadOnlyList<ImportRow> importRows = [];
    [ObservableProperty] private IReadOnlyList<ImportItemResult> importResults = [];
    [ObservableProperty] private double importProgressValue;
    [ObservableProperty] private double importProgressMaximum = 1;
    [ObservableProperty] private bool importProgressKnown;
    [ObservableProperty] private string importProgressText = "";
    public int ImportTrackCount => ImportRows.Count;
    public bool ImportHasPreview => ImportRows.Count > 0;
    public bool ImportIsOnline => importPreview?.Platform is not null;
    public string ImportStartText => ImportIsOnline ? "Baixar" : "Importar selecionadas";
    // Online audio needs the operator's explicit statement of rights; a playlist link is not a license.
    public bool ImportCanStart => ImportHasPreview && !ImportBusy && (!ImportIsOnline || ImportRightsConfirmed);
    partial void OnImportRowsChanged(IReadOnlyList<ImportRow> value)
    {
        OnPropertyChanged(nameof(ImportTrackCount)); OnPropertyChanged(nameof(ImportHasPreview)); OnPropertyChanged(nameof(ImportIsOnline));
        OnPropertyChanged(nameof(ImportStartText)); OnPropertyChanged(nameof(ImportCanStart));
    }

    public async Task RefreshImportToolsAsync()
    {
        if (importService is null) { ImportToolMessage = "Importador não configurado nesta execução."; return; }
        try { ImportToolMessage = (await importService.DiagnoseAsync(lifetime.Token)).Message; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ImportToolMessage = "Falha ao verificar ferramentas internas."; logger.Warning(ex, "Diagnóstico do importador"); }
    }
    [RelayCommand] private async Task PreviewImportUrlAsync()
    {
        if (importService is null || ImportBusy) return;
        ImportBusy = true; ImportResults = []; ImportProgressKnown = false; ImportProgressText = "";
        importCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ImportMessage = "Analisando a playlist… Playlists grandes podem levar alguns minutos; Cancelar interrompe.";
        try
        {
            var preview = await importService.PreviewUrlAsync(ImportUrl, importCancellation.Token);
            SetImportPreview(preview);
            ImportMessage = $"{preview.Tracks.Count} música(s) encontrada(s). Confira a lista e clique em Baixar.";
        }
        catch (OperationCanceledException) { ImportMessage = "Análise cancelada."; }
        catch (Exception ex) { ImportMessage = ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or InvalidDataException or TimeoutException ? ex.Message : "Falha ao analisar a playlist. Consulte os logs."; logger.Warning(ex, "Prévia online indisponível"); }
        finally { ImportBusy = false; importCancellation.Dispose(); importCancellation = null; }
    }
    [RelayCommand] private async Task PreviewImportLocalAsync()
    {
        if (importService is null || PickImportFiles is null || ImportBusy) return;
        ImportBusy = true; ImportResults = []; ImportProgressKnown = false; ImportProgressText = "";
        try
        {
            var paths = await PickImportFiles(); if (paths.Count == 0) return;
            var preview = await importService.PreviewLocalAsync(paths, lifetime.Token);
            SetImportPreview(preview);
            ImportMessage = $"{preview.Tracks.Count} faixa(s) na prévia; selecione as desejadas.";
        }
        catch (OperationCanceledException) { ImportMessage = "Seleção cancelada."; }
        catch (Exception ex) { ImportMessage = "Falha ao ler MP3 selecionados. Consulte os logs."; logger.Warning(ex, "Prévia local indisponível"); }
        finally { ImportBusy = false; }
    }
    private void SetImportPreview(ImportPreview preview)
    {
        importPreview = preview; ImportRightsConfirmed = false;
        ImportRows = preview.Tracks.Select(t => new ImportRow(t)).ToArray();
        ImportPlaylistName = preview.Name;
        ImportSummary = preview.Platform is null
            ? $"{preview.Tracks.Count} faixa(s) • {preview.Tracks.Count(t => t.ExistingTrackId is not null)} já na biblioteca • {preview.Tracks.Count(t => !t.Available)} indisponível(is)."
            : $"{preview.Tracks.Count} música(s) • {preview.Tracks.Count(t => t.Downloadable)} a baixar • {preview.Tracks.Count(t => t.ExistingTrackId is not null)} já na biblioteca.";
    }
    [RelayCommand] private async Task StartImportAsync()
    {
        if (importService is null || importPreview is null || !ImportCanStart) return;
        ImportBusy = true; importCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ImportProgressValue = 0; ImportProgressMaximum = 1; ImportProgressKnown = false; ImportProgressText = "";
        try
        {
            var progress = new Progress<ImportProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                ImportMessage = p.Message;
                if (p.Total <= 0) return;
                ImportProgressKnown = true; ImportProgressMaximum = p.Total; ImportProgressValue = Math.Min(p.Completed, p.Total);
                ImportProgressText = $"{Math.Min(p.Completed, p.Total)} de {p.Total}";
            }));
            var selected = ImportRows.Where(r => r.IsSelected).Select(r => r.Track.Id).ToHashSet();
            var result = await importService.ImportAsync(importPreview, selected, settings.MusicDirectory ?? "", ImportPlaylistName, progress, importCancellation.Token);
            ImportResults = result.Items;
            ImportMessage = result.Cancelled ? $"Cancelado. {result.Imported} música(s) concluída(s) foram preservadas na pasta musical, sem playlist." :
                result.Unavailable > 0 ? $"Concluído com pendências: {result.Imported} nova(s), {result.Duplicates} já existente(s), {result.Unavailable} não disponível(is)"
                    + (result.FailureBreakdown.Length > 0 ? $" ({result.FailureBreakdown})" : "") + (result.PlaylistId is null ? ". Nenhuma playlist foi criada." : ". A playlist contém apenas as obtidas.")
                    + (result.Items.Where(i => i.Failure is not null).GroupBy(i => i.Failure!.Value).OrderByDescending(g => g.Count()).FirstOrDefault() is { } main
                        ? " " + ImportFailures.Describe(main.Key) : "") :
                $"Concluído: {result.Imported} nova(s), {result.Duplicates} já existente(s). Playlist “{ImportPlaylistName.Trim()}” criada.";
            if (result.Imported > 0 || result.PlaylistId is not null) { await ReloadPlaylistsAsync(); if (library is not null) await LoadCatalogAsync(true); }
        }
        catch (OperationCanceledException) { ImportMessage = "Importação cancelada."; }
        catch (Exception ex) { ImportMessage = ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException or NotSupportedException ? ex.Message : "A importação falhou; arquivos concluídos foram preservados."; logger.Warning(ex, "Falha de importação"); }
        finally { ImportBusy = false; importCancellation.Dispose(); importCancellation = null; }
    }
    [RelayCommand] private void CancelImport() => importCancellation?.Cancel();
}
