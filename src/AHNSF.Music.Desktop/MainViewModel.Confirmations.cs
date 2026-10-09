using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;
public partial class MainViewModel
{
    private Func<Task>? pendingDestructive;
    [ObservableProperty] private bool destructiveConfirmation;
    [ObservableProperty] private string destructivePrompt = "";
    private void RequestConfirmation(string message, Func<Task> action)
    { DestructivePrompt = message; pendingDestructive = action; DestructiveConfirmation = true; }
    [RelayCommand] private void CancelDestructive() { pendingDestructive = null; DestructiveConfirmation = false; }
    [RelayCommand] private async Task ConfirmDestructiveAsync()
    {
        if (!DestructiveConfirmation || pendingDestructive is null) return;
        var action = pendingDestructive; CancelDestructive();
        try { await action(); }
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
