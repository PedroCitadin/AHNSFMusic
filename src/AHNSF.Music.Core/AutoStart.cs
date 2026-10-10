namespace AHNSF.Music.Core;

/// <summary>Per-user Windows start-up entries (HKCU Run). Abstracted so tests never touch the real registry.</summary>
public interface IStartupRegistry
{
    IReadOnlyDictionary<string, string> ReadEntries();
    void Write(string name, string command);
    void Delete(string name);
    /// <summary>False when Windows (Task Manager/Settings) disabled the entry; null when no decision is recorded.</summary>
    bool? IsApprovedByWindows(string name);
}
public enum AutoStartState { Disabled, Enabled, Missing, PointsElsewhere, Duplicated, DisabledByWindows, PresentWhileDisabled, Unsupported, Error }
public sealed record AutoStartStatus(AutoStartState State, string Message, string? RegisteredCommand = null, Exception? Error = null)
{
    public bool NeedsAttention => State is not (AutoStartState.Disabled or AutoStartState.Enabled);
}
public sealed class AutoStartService(IStartupRegistry registry, string? executablePath)
{
    public const string EntryName = "AHNSF Music";
    public const string Argument = "--autostart";
    public string? ExecutablePath { get; } = executablePath;
    public static string BuildCommand(string executable) => $"\"{executable}\" {Argument}";
    public static bool IsAutoStartLaunch(IEnumerable<string> args) => args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
    /// <summary>Returns why the executable cannot be registered, or null when it can.</summary>
    public string? ValidateExecutable()
    {
        var path = ExecutablePath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('"'))
            return "Não foi possível identificar o caminho completo do executável.";
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return "A inicialização automática exige o executável compilado AHNSF.Music.Desktop.exe (não funciona via dotnet run).";
        return null;
    }
    public AutoStartStatus Inspect(bool desired)
    {
        try
        {
            var entries = registry.ReadEntries();
            entries.TryGetValue(EntryName, out var own);
            var duplicates = ExecutablePath is null ? [] : entries.Where(e => e.Key != EntryName && PointsTo(e.Value, ExecutablePath)).Select(e => e.Key).ToArray();
            if (!desired)
                return own is null && duplicates.Length == 0 ? new(AutoStartState.Disabled, "Inicialização com o Windows desativada.")
                    : new(AutoStartState.PresentWhileDisabled, "Existe uma entrada de inicialização do AHNSF Music, mas a opção está desativada. Use “Aplicar novamente” para removê-la.", own);
            if (ValidateExecutable() is { } invalid) return new(AutoStartState.Unsupported, invalid, own);
            if (own is null)
                return new(AutoStartState.Missing, "A entrada de inicialização foi removida fora do aplicativo. Use “Aplicar novamente” para recriá-la.");
            if (!string.Equals(own.Trim(), BuildCommand(ExecutablePath!), StringComparison.OrdinalIgnoreCase))
                return new(AutoStartState.PointsElsewhere, "A entrada de inicialização aponta para outro local ou foi alterada. Use “Aplicar novamente” para corrigi-la.", own);
            if (duplicates.Length > 0)
                return new(AutoStartState.Duplicated, $"Há entradas duplicadas para este executável ({string.Join(", ", duplicates)}). Use “Aplicar novamente”.", own);
            if (registry.IsApprovedByWindows(EntryName) == false)
                return new(AutoStartState.DisabledByWindows, "A inicialização foi desativada pelo Windows (Gerenciador de Tarefas › Aplicativos de inicialização). Reative-a por lá.", own);
            return new(AutoStartState.Enabled, "O AHNSF Music será iniciado quando este usuário entrar no Windows.", own);
        }
        catch (Exception ex) { return new(AutoStartState.Error, "Não foi possível consultar a inicialização automática do Windows.", null, ex); }
    }
    /// <summary>Writes or removes the single entry; also removes other entries pointing to this same executable.</summary>
    public AutoStartStatus Apply(bool enabled)
    {
        try
        {
            if (enabled && ValidateExecutable() is { } invalid) return new(AutoStartState.Unsupported, invalid);
            if (enabled) registry.Write(EntryName, BuildCommand(ExecutablePath!));
            else registry.Delete(EntryName);
            if (ExecutablePath is not null)
                foreach (var duplicate in registry.ReadEntries().Where(e => e.Key != EntryName && PointsTo(e.Value, ExecutablePath)).Select(e => e.Key).ToArray())
                    registry.Delete(duplicate);
            return Inspect(enabled);
        }
        catch (Exception ex) { return new(AutoStartState.Error, "Não foi possível alterar a inicialização automática do Windows.", null, ex); }
    }
    /// <summary>True when the command line's program (quoted or not) is the given executable.</summary>
    public static bool PointsTo(string command, string executable)
    {
        var text = command.Trim();
        string program;
        if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); if (end < 0) return false; program = text[1..end]; }
        else
        {
            // Unquoted paths with spaces are ambiguous; accept the executable when the command starts with it.
            if (text.StartsWith(executable, StringComparison.OrdinalIgnoreCase) && (text.Length == executable.Length || char.IsWhiteSpace(text[executable.Length]))) return true;
            program = text.Split(' ', 2)[0];
        }
        try { return string.Equals(Path.GetFullPath(program), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
