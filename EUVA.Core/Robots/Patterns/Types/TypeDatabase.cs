// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EUVA.Core.Robots.Patterns.Types;

public static class TypeDatabase
{
    private static Dictionary<string, StructDefinition> _structs = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, StructDefinition> Structs => _structs;

    public static void Load(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath)) return;

        try
        {
            var json = File.ReadAllText(jsonFilePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<Dictionary<string, StructDefinition>>(json, opts);
            if (parsed != null)
            {
                foreach (var kvp in parsed)
                {
                    kvp.Value.Name = kvp.Key;
                    _structs[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TypeDatabase] Error loading {jsonFilePath}: {ex.Message}");
        }
    }

    public static StructDefinition? GetStruct(string name)
    {
        return _structs.TryGetValue(name, out var def) ? def : null;
    }

    public static void RegisterDynamicStruct(StructDefinition def)
    {
        _structs[def.Name] = def;
    }

    public static string GetDefaultStructsFile()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(exeDir, "Robots", "Patterns", "Types", "structs.json");
    }
}
