using System.Text.Json;
using System.Text.Json.Serialization;
using Dotnetumentation.Core;
using Dotnetumentation.Core.Model;

namespace Dotnetumentation.App;

/// <summary>The last used solution and generator options, saved to <c>~/.config/dotnetumentation/settings.json</c>.</summary>
public sealed class AppSettings
{
    public const string AppId = "dotnetumentation";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId, "settings.json");

    public string? InputPath { get; set; }
    public DiagramKinds Diagrams { get; set; } = DiagramKinds.All;
    public Visibility MinVisibility { get; set; } = Visibility.Internal;
    /// <summary>Null: follow the calls to the end of the own code.</summary>
    public int? SequenceDepth { get; set; }
    public int MaxSequenceMessages { get; set; } = GeneratorOptions.DefaultMaxSequenceMessages;
    public string Include { get; set; } = "";
    public string Exclude { get; set; } = "";
    public string OutputDirectoryName { get; set; } = GeneratorOptions.DefaultOutputDirectoryName;
    public bool Incremental { get; set; }
    public bool Graph { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file falls back to the defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saving the settings is not worth bothering the user.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
