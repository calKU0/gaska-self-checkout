using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Infrastructure.Settings;

/// <summary>
/// Workstation settings edited in the configuration screen. They are stored in <c>data/station.json</c>, which is loaded
/// as the last configuration source with reload-on-change, so services reading <c>IOptionsMonitor</c> pick them up live.
/// </summary>
public sealed class StationSettingsStore(ILogger<StationSettingsStore> logger)
{
    public const string FileName = "station.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _sync = new();

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", FileName);

    /// <summary>
    /// Saves an options object under a configuration path (e.g. "Polcard" or "Erp:Registry"). With
    /// <paramref name="properties"/> only those properties are stored (the section is replaced) – the others keep coming
    /// from appsettings.json instead of being frozen at their current values.
    /// </summary>
    public void Save<T>(string sectionPath, T value, params string[] properties)
    {
        var node = JsonSerializer.SerializeToNode(value, Json);
        if (properties.Length > 0 && node is JsonObject section)
            foreach (var name in section.Select(p => p.Key).Except(properties).ToList())
                section.Remove(name);
        SaveNode(sectionPath, node);
    }

    private void SaveNode(string sectionPath, JsonNode? value)
    {
        lock (_sync)
        {
            var root = Load();
            var node = root;
            var parts = sectionPath.Split(':');
            foreach (var part in parts[..^1])
            {
                if (node[part] is not JsonObject child)
                    node[part] = child = new JsonObject();
                node = child;
            }

            node[parts[^1]] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, FilePath, overwrite: true);
            logger.LogInformation("Workstation settings saved: {Section}", sectionPath);
        }
    }

    private static JsonObject Load()
    {
        try
        {
            return File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o ? o : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
