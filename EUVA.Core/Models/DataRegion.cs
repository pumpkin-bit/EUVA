// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Media;
namespace EUVA.Core.Models;

public class DataRegion
{
    public long Offset { get; init; }
    public long Size { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public RegionType Type { get; init; }
    public Color HighlightColor { get; init; }
    public int Layer { get; init; }
    public BinaryStructure? LinkedStructure { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
    public long EndOffset => Offset + Size;
    public bool Contains(long offset) => offset >= Offset && offset < EndOffset;
    public override string ToString() => $"{Name} [0x{Offset:X8}-0x{EndOffset:X8}]";
}


public enum RegionType
{
    Header,
    Code,
    Data,
    Import,
    Export,
    Resource,
    Relocation,
    Debug,
    Overlay,
    Signature,
    Unknown
}
