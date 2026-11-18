namespace Eule.Core;

/// <summary>Where Eule keeps its files. The base folder is injectable so tests can use a temp folder.</summary>
public sealed class AppPaths(string dataDir, string legacyDir)
{
    public static AppPaths Default
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new AppPaths(Path.Combine(appData, "Eule"), Path.Combine(appData, "OturmaSayaci"));
        }
    }

    public string DataDir { get; } = dataDir;
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string DatabaseFile => Path.Combine(DataDir, "eule.db");
    public string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Data folder of the earlier "Oturma Sayacı" prototype.</summary>
    public string LegacyDir { get; } = legacyDir;
}
