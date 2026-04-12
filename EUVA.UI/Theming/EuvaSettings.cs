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

    [JsonPropertyName("aiProvider")]
    public string AiProvider { get; set; } = "Custom";

    [JsonPropertyName("aiApiKeyEncrypted")]
    public string AiApiKeyEncrypted { get; set; } = string.Empty;

    [JsonPropertyName("aiBaseUrl")]
    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("aiModelName")]
    public string AiModelName { get; set; } = "gpt-4o";

    [JsonPropertyName("aiCustomPrompt")]
    public string AiCustomPrompt { get; set; } = "You are an expert C++ reverse engineer. Refactor the provided decompiled C++ code. You MUST perfectly preserve every single loop, if-statement, and logical operation! DO NOT summarize or abstract logic away. DO NOT create new helper functions or split the function. Your ONLY job is to: 1) Rename meaningless variables (e.g. spill_1, v2) and dummy functions (e.g. sub_XXXX) to semantic names based on context. 2) Clean up ugly or redundant type casting. 3) Add explanatory comments. RULES: 1) You MUST return the C++ code inside a single ```cpp codeblock with NO conversational text outside it. 2) DO NOT revert structure member accesses back to raw pointer arithmetic. RENAME the structures (e.g. AutoStruct_X -> Node) but KEEP using their field access syntax. 3) For every line where you rename a variable or change casts, you MUST append the comment `// [AI]`. 4) You may remove entirely empty if-blocks or dead logic chunks. 5) Output compilable C++ with generic standard headers.";

    [JsonPropertyName("vtApiKey")]
    public string VtApiKey { get; set; } = string.Empty;



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
