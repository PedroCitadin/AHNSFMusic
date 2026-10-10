namespace AHNSF.Music.Core;

public enum CookieFileState { NotConfigured, Configured, FileMissing, Invalid }

/// <summary>
/// Path of the operator's own YouTube cookies.txt, passed to spotDL as --cookie-file. Only the path is kept
/// (in the settings JSON); the application never copies, logs, stores or transmits the file contents itself.
/// </summary>
public sealed class YouTubeCookieSetting
{
    public string? FilePath { get; set; }

    /// <summary>Existence, size and the Netscape header line only. A valid file does not prove the session is accepted.</summary>
    public static (CookieFileState State, string Message) Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (CookieFileState.NotConfigured, "Não configurado. Downloads que não exigem autenticação continuam funcionando.");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (CookieFileState.FileMissing, "Arquivo de cookies não encontrado no caminho configurado.");
            if (info.Length == 0) return (CookieFileState.Invalid, "Arquivo de cookies vazio.");
            if (info.Length > 10 * 1024 * 1024) return (CookieFileState.Invalid, "Arquivo grande demais para um cookies.txt.");
            using var reader = new StreamReader(path);
            string? first;
            do first = reader.ReadLine()?.Trim(); while (first is { Length: 0 });
            if (first is null || !(first.StartsWith("# Netscape HTTP Cookie File", StringComparison.Ordinal) || first == "# HTTP Cookie File"))
                return (CookieFileState.Invalid, "Formato inválido: o arquivo não começa com o cabeçalho Netscape (# Netscape HTTP Cookie File).");
            return (CookieFileState.Configured, "Arquivo de cookies configurado. O formato parece válido; só um download mostra se o YouTube aceita a sessão.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (CookieFileState.Invalid, "Não foi possível ler o arquivo de cookies."); }
    }
}
