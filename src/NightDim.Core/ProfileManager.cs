using System.Collections.ObjectModel;
using System.Text.Json;

namespace NightDim.Core;

public sealed class Preferences
{
    public bool Enabled { get; set; } = true;
    public bool OverlayOnly { get; set; }
    public bool GammaOnly { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DimmingMode Mode => GammaOnly ? DimmingMode.GammaOnly
        : OverlayOnly ? DimmingMode.OverlayOnly : DimmingMode.Hybrid;
    public string Language { get; set; } = "ru";
    public bool Synchronize { get; set; }
    public string? SelectedDisplayId { get; set; }
    public EffectSettings Synchronized { get; set; } = EffectSettings.Neutral;
    public Dictionary<string, EffectSettings> Displays { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, EffectSettings> Profiles { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProfileManager(string path)
{
    public static IReadOnlyDictionary<string, EffectSettings> DefaultProfiles { get; } =
        new ReadOnlyDictionary<string, EffectSettings>(new Dictionary<string, EffectSettings>(StringComparer.Ordinal)
        {
            ["neutral"] = EffectSettings.Neutral,
            ["day"] = new EffectSettings(85),
            ["evening"] = new EffectSettings(40, 4500),
            ["night"] = new EffectSettings(10, 3200),
            ["custom"] = EffectSettings.Neutral
        });

    public Preferences Load()
    {
        try
        {
            using FileStream input = File.OpenRead(path);
            return Normalize(JsonSerializer.Deserialize<Preferences>(input) ?? new Preferences());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Normalize(new Preferences());
        }
    }

    public bool Save(Preferences preferences) => AtomicJsonFile.TryWrite(path, Normalize(preferences));

    private static Preferences Normalize(Preferences source)
    {
        var result = new Preferences
        {
            Enabled = source.Enabled,
            OverlayOnly = !source.GammaOnly && source.OverlayOnly,
            GammaOnly = source.GammaOnly,
            Language = source.Language == "en" ? "en" : "ru",
            Synchronize = source.Synchronize,
            SelectedDisplayId = IsPersistentDisplayId(source.SelectedDisplayId) ? source.SelectedDisplayId : null,
            Synchronized = source.Synchronized?.Normalize() ?? EffectSettings.Neutral
        };

        if (source.Displays is not null)
        {
            foreach (var (displayId, settings) in source.Displays)
            {
                if (IsPersistentDisplayId(displayId) && settings is not null)
                    result.Displays[displayId] = settings.Normalize();
            }
        }

        foreach (var (id, defaultSettings) in DefaultProfiles)
        {
            EffectSettings? saved = source.Profiles?.GetValueOrDefault(id);
            result.Profiles[id] = id == "neutral" ? EffectSettings.Neutral : saved?.Normalize() ?? defaultSettings;
        }

        return result;
    }

    private static bool IsPersistentDisplayId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.StartsWith("session:", StringComparison.Ordinal);
}

internal static class AtomicJsonFile
{
    public static bool TryWrite<T>(string path, T value)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
