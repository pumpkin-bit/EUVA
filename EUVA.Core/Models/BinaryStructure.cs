// SPDX-License-Identifier: GPL-3.0-or-later


namespace EUVA.Core.Models;


public class BinaryStructure
{
   
    public string Name { get; set; } = string.Empty;

   
    public string Type { get; set; } = string.Empty;

   
    public long? Offset { get; set; }

    
    public long? Size { get; set; }

   
    public object? Value { get; set; }

    
    public string? DisplayValue { get; set; }

   
    public List<BinaryStructure> Children { get; set; } = new();

   
    public BinaryStructure? Parent { get; set; }

   
    public DataRegion? Region { get; set; }

    
    public Dictionary<string, object> Metadata { get; set; } = new();

    
    public void AddChild(BinaryStructure child)
    {
        child.Parent = this;
        Children.Add(child);
    }

   
    public BinaryStructure? FindByPath(params string[] path)
    {
        var current = this;
        foreach (var segment in path)
        {
            current = current.Children.FirstOrDefault(c => c.Name == segment);
            if (current == null) return null;
        }
        return current;
    }

    public override string ToString() => DisplayValue ?? Value?.ToString() ?? Name;
}
