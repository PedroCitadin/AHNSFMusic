using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;
public partial class MainViewModel
{
    private Func<Task>? pendingDestructive;
    private Func<Task>? pendingSecondary;
    private Action? pendingCancel;
    [ObservableProperty] private bool destructiveConfirmation;
    [ObservableProperty] private string destructivePrompt = "";
    [ObservableProperty] private string dialogTitle = "Confirmar alteração";
    [ObservableProperty] private string dialogConfirmLabel = "Confirmar";
    [ObservableProperty] private string dialogSecondaryLabel = "";
    [ObservableProperty] private bool dialogIsDanger = true;
    /// <summary>A modal dialog is open: the background is disabled, so keyboard focus cannot leave the dialog.</summary>
    public bool IsModalOpen => DestructiveConfirmation;
    public bool IsBackgroundInteractive => !DestructiveConfirmation;
    public bool HasDialogSecondary => DialogSecondaryLabel.Length > 0;
    partial void OnDestructiveConfirmationChanged(bool value) { OnPropertyChanged(nameof(IsModalOpen)); OnPropertyChanged(nameof(IsBackgroundInteractive)); }
    partial void OnDialogSecondaryLabelChanged(string value) => OnPropertyChanged(nameof(HasDialogSecondary));
    private void RequestConfirmation(string message, Func<Task> action) =>
        RequestDialog("Confirmar alteração", message, "Confirmar", action, danger: true);
    /// <summary>Opens the single modal dialog. A dialog already open is cancelled first (cancel is always the safe outcome).</summary>
    public void RequestDialog(string title, string message, string confirmLabel, Func<Task> action, bool danger = false,
        string? secondaryLabel = null, Func<Task>? secondary = null, Action? cancelled = null)
    {
        if (DestructiveConfirmation) CancelDestructive();
        DialogTitle = title; DestructivePrompt = message; DialogConfirmLabel = confirmLabel; DialogIsDanger = danger;
        DialogSecondaryLabel = secondary is null ? "" : secondaryLabel ?? ""; pendingSecondary = secondary; pendingCancel = cancelled;
        pendingDestructive = action; DestructiveConfirmation = true;
    }
    private void CloseDialog() { pendingDestructive = null; pendingSecondary = null; pendingCancel = null; DestructiveConfirmation = false; }
    [RelayCommand] private void CancelDestructive()
    {
        var cancelled = DestructiveConfirmation ? pendingCancel : null;
        CloseDialog(); cancelled?.Invoke();
    }
    [RelayCommand] private Task ConfirmDestructiveAsync() => RunDialogAction(pendingDestructive);
    [RelayCommand] private Task DialogSecondaryAsync() => RunDialogAction(pendingSecondary);
    private async Task RunDialogAction(Func<Task>? chosen)
    {
        if (!DestructiveConfirmation || chosen is null) return;
        CloseDialog();
        try { await chosen(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex, "Não foi possível concluir a alteração. Os arquivos originais foram preservados."); }
    }
    [RelayCommand] private void RequestRemovePlaylistItems()
    {
        if (playlists is null || SelectedPlaylist is null) return;
        var ids = PlaylistSelection(); if (ids.Length == 0) { Status = "Selecione itens para remover."; return; }
        var id = SelectedPlaylist.Id; var name = SelectedPlaylist.Name;
        RequestConfirmation($"Remover {ids.Length} ocorrência(s) de “{name}”? As músicas da biblioteca e a fila ativa serão preservadas.", async () =>
        { await playlists.RemoveAsync(id, ids, lifetime.Token); await ReloadPlaylistDetailAsync(lifetime.Token); Status = "Itens removidos. Biblioteca e fila ativa preservadas."; });
    }
    [RelayCommand] private void RequestClearQueue()
    {
        if (queue.Items.Count == 0) { Status = "A fila já está vazia."; return; }
        var snapshot = queue.Items.Select(i => i.Id).ToArray();
        RequestConfirmation("Limpar a fila e parar a reprodução? Playlists e arquivos salvos serão preservados.", () =>
        { if (!queue.Items.Select(i => i.Id).SequenceEqual(snapshot)) { Status = "A fila mudou. Revise e solicite a alteração novamente."; return Task.CompletedTask; } ClearQueue(); return Task.CompletedTask; });
    }
    [RelayCommand] private void RequestRemoveQueueItems()
    {
        var ids = SelectedQueueRows.Cast<MusicRow>().Select(r => r.Id).ToArray();
        if (ids.Length == 0 && SelectedQueueRow is not null) ids = [SelectedQueueRow.Id];
        if (ids.Length == 0) { Status = "Selecione itens na fila."; return; }
        RequestConfirmation($"Remover {ids.Length} ocorrência(s) da fila? Se a atual estiver selecionada, a reprodução será interrompida.", () =>
        { ManualIntervention(); playback.Remove(ids); return Task.CompletedTask; });
    }
}
