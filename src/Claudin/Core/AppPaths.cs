namespace Claudin.Core;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Claudin. Pode ser trocado pela variável CLAUDIN_HOME (uso em desenvolvimento).</summary>
    public static string Home { get; } = ResolveHome();

    public static string ConfigFile => Path.Combine(Home, "config.json");

    public static string LogsDir => Path.Combine(Home, "logs");

    public static string RegistryBackupFile => Path.Combine(Home, "registry-backup.json");

    private static string ResolveHome()
    {
        var overridden = Environment.GetEnvironmentVariable("CLAUDIN_HOME");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claudin")
            : Path.GetFullPath(overridden);
    }
}
