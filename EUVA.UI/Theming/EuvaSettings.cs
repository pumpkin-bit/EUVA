// SPDX-License-Identifier: GPL-3.0-or-later


using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EUVA.UI.Theming;


public sealed class EuvaSettings
{
 

    private static EuvaSettings? _default;

   
    public static EuvaSettings Default => _default ??= Load();

  
    [JsonPropertyName("lastThemePath")]
    public string LastThemePath { get; set; } = string.Empty;

 

    private static string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EUVA", "settings.json");

  
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EuvaSettings] Save failed: {ex.Message}");
        }
    }

    private static EuvaSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var loaded = JsonSerializer.Deserialize<EuvaSettings>(
                    File.ReadAllText(SettingsFilePath));
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EuvaSettings] Load failed: {ex.Message}");
        }
        return new EuvaSettings();
    }
}
