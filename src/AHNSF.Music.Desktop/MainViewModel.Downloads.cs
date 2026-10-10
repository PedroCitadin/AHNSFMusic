using AHNSF.Music.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AHNSF.Music.Desktop;

/// <summary>Configurações → Downloads → Autenticação YouTube. Only the path is stored; the file itself is never opened here beyond its header check.</summary>
public partial class MainViewModel
{
    private readonly YouTubeCookieSetting? cookieSetting;
    public Func<Task<string?>>? PickCookieFile { get; set; }
    [ObservableProperty] private string cookieFilePath = "";
    [ObservableProperty] private string cookieStatus = "Não configurado.";
    [ObservableProperty] private bool cookieNeedsAttention;
    public bool HasCookieFile => CookieFilePath.Length > 0;
    partial void OnCookieFilePathChanged(string value) => OnPropertyChanged(nameof(HasCookieFile));

    private void LoadCookieSetting() => ApplyCookiePath(settings.YouTubeCookieFile);
    private void ApplyCookiePath(string? path)
    {
        var (state, message) = YouTubeCookieSetting.Inspect(path);
        CookieFilePath = path ?? ""; CookieStatus = message;
        CookieNeedsAttention = state is CookieFileState.FileMissing or CookieFileState.Invalid;
        // A missing/invalid configured path is still handed over: the download then stops with this message instead of silently skipping it.
        if (cookieSetting is not null) cookieSetting.FilePath = string.IsNullOrWhiteSpace(path) ? null : path;
    }
    [RelayCommand] private async Task SelectCookieFileAsync()
    {
        if (PickCookieFile is null) return;
        var path = await PickCookieFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        var (state, message) = YouTubeCookieSetting.Inspect(path);
        if (state != CookieFileState.Configured) { CookieStatus = message + " A configuração anterior foi mantida."; CookieNeedsAttention = true; return; }
        settings = settings with { YouTubeCookieFile = Path.GetFullPath(path) }; await SaveAsync();
        ApplyCookiePath(settings.YouTubeCookieFile);
    }
    [RelayCommand] private async Task RemoveCookieFileAsync()
    {
        settings = settings with { YouTubeCookieFile = null }; await SaveAsync();
        ApplyCookiePath(null);
        CookieStatus = "Configuração removida. O arquivo de cookies não foi apagado nem alterado pelo aplicativo.";
    }
    [RelayCommand] private void CheckCookieFile() => ApplyCookiePath(settings.YouTubeCookieFile);
    [RelayCommand] private void OpenDownloadSettings() { if (!IsModalOpen) Section = "Configurações"; }
}
