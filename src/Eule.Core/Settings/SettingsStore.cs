using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eule.Core.Settings;

/// <summary>Loads and saves <see cref="EuleSettings"/> as JSON. Writes are atomic; a corrupt file is kept aside and defaults are used.</summary>
public sealed class SettingsStore(string path)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = path;
    public bool Exists => File.Exists(Path);

    /// <summary>Set when the last <see cref="Load"/> found an unreadable file and fell back to defaults.</summary>
    public string? LastLoadError { get; private set; }

    public EuleSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(Path)) return new EuleSettings();
        try
        {
            return Deserialize(File.ReadAllText(Path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            LastLoadError = ex.Message;
            try { File.Copy(Path, Path + ".bozuk", overwrite: true); } catch (IOException) { }
            return new EuleSettings();
        }
    }

    public void Save(EuleSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, Serialize(settings));
        File.Move(tmp, Path, overwrite: true);
    }

    public static string Serialize(EuleSettings s) => JsonSerializer.Serialize(s, Options);

    public static EuleSettings Deserialize(string json) =>
        (JsonSerializer.Deserialize<EuleSettings>(json, Options) ?? new EuleSettings()).Normalize();
}
