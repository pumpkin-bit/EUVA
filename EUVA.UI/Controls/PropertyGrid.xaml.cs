// SPDX-License-Identifier: GPL-3.0-or-later


using System.Windows;
using System.Windows.Controls;
using EUVA.Core.Models;

namespace EUVA.UI.Controls;

public partial class PropertyGrid : UserControl
{
    public static readonly DependencyProperty SelectedStructureProperty =
        DependencyProperty.Register(nameof(SelectedStructure), typeof(BinaryStructure),
            typeof(PropertyGrid), new PropertyMetadata(null, OnSelectedStructureChanged));

    public static readonly DependencyProperty SelectedOffsetProperty =
        DependencyProperty.Register(nameof(SelectedOffset), typeof(long),
            typeof(PropertyGrid), new PropertyMetadata(-1L, OnSelectedOffsetChanged));

    public BinaryStructure? SelectedStructure
    {
        get => (BinaryStructure?)GetValue(SelectedStructureProperty);
        set => SetValue(SelectedStructureProperty, value);
    }

    public long SelectedOffset
    {
        get => (long)GetValue(SelectedOffsetProperty);
        set => SetValue(SelectedOffsetProperty, value);
    }

    public PropertyGrid()
    {
        InitializeComponent();
    }

    private static void OnSelectedStructureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PropertyGrid grid && e.NewValue is BinaryStructure structure)
        {
            grid.UpdateProperties(structure);
        }
    }

    private static void OnSelectedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PropertyGrid grid)
        {
            grid.UpdateOffsetProperties((long)e.NewValue);
        }
    }

    private void UpdateProperties(BinaryStructure structure)
    {
        var properties = new List<KeyValuePair<string, string>>
        {
            new("Name", structure.Name),
            new("Type", structure.Type)
        };

        if (structure.Offset.HasValue)
            properties.Add(new("Offset", $"0x{structure.Offset:X8}"));

        if (structure.Size.HasValue)
            properties.Add(new("Size", $"{structure.Size} bytes (0x{structure.Size:X})"));

        if (structure.Value != null)
            properties.Add(new("Value", structure.Value.ToString() ?? ""));

        if (structure.DisplayValue != null)
            properties.Add(new("Display", structure.DisplayValue));

      
        foreach (var meta in structure.Metadata)
        {
            properties.Add(new(meta.Key, meta.Value?.ToString() ?? ""));
        }

        PropertiesControl.ItemsSource = properties;
    }

    private void UpdateOffsetProperties(long offset)
    {
        if (offset < 0)
        {
            PropertiesControl.ItemsSource = null;
            return;
        }

        var properties = new List<KeyValuePair<string, string>>
        {
            new("Offset", $"0x{offset:X8} ({offset})"),
            new("Offset (Dec)", offset.ToString()),
            new("Offset (Hex)", $"0x{offset:X8}"),
            new("Offset (Bin)", Convert.ToString(offset, 2).PadLeft(32, '0'))
        };

        PropertiesControl.ItemsSource = properties;
    }
}
