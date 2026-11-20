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
    /// <summary>
    /// Large downloaded files (the speech model). Local, not roaming, and outside the install folder so updates keep it.
    /// Shared by every data folder, so a developer copy does not download it again.
    /// </summary>
    public static string ModelDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eule-models");

    /// <summary>The signed-in account, encrypted for the current Windows user.</summary>
    public string AccountFile => Path.Combine(DataDir, "account.dat");

    /// <summary>Data folder of the earlier "Oturma Sayacı" prototype.</summary>
    public string LegacyDir { get; } = legacyDir;
}
