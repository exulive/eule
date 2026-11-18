namespace Eule.Core;

/// <summary>Where Eule keeps its files. The base folder is injectable so tests can use a temp folder.</summary>
public sealed class AppPaths(string dataDir, string legacyDir)
{
    public static AppPaths Default
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // EULE_DATA_DIR lets developers run against a throw-away folder (demo data, screenshots).
            string? overrideDir = Environment.GetEnvironmentVariable("EULE_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDir)
                ? new AppPaths(Path.Combine(appData, "Eule"), Path.Combine(appData, "OturmaSayaci"))
                : new AppPaths(overrideDir, Path.Combine(overrideDir, "legacy-none"));
        }
    }

    public string DataDir { get; } = dataDir;
    public bool IsOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EULE_DATA_DIR"));
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string DatabaseFile => Path.Combine(DataDir, "eule.db");
    public string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Data folder of the earlier "Oturma Sayacı" prototype.</summary>
    public string LegacyDir { get; } = legacyDir;
}
