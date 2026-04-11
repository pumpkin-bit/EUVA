// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Text;

namespace EUVA.Core.Robots.Patterns.Types;

public sealed class StructField
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public sealed class StructDefinition
{
    public string Name { get; set; } = string.Empty;
    public int Size { get; set; }
    public Dictionary<string, StructField> Fields { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    public string EmitSyntax()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"struct {Name}");
        sb.AppendLine("{");
        
        var sortedOffsets = new List<int>();
        foreach (var k in Fields.Keys)
        {
            if (int.TryParse(k, out int num))
                sortedOffsets.Add(num);
        }
        sortedOffsets.Sort();

        foreach (var offset in sortedOffsets)
        {
            var field = Fields[offset.ToString()];
            sb.AppendLine($"    {field.Type} {field.Name}; // 0x{offset:X}");
        }

        sb.AppendLine("};");
        return sb.ToString();
    }
}
