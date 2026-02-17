// SPDX-License-Identifier: GPL-3.0-or-later


using System.Windows;
using System.Windows.Controls;
using EUVA.Core.Models;

namespace EUVA.UI.Controls;

public partial class StructureTreeView : UserControl
{
    public static readonly DependencyProperty RootStructureProperty =
        DependencyProperty.Register(nameof(RootStructure), typeof(BinaryStructure), 
            typeof(StructureTreeView), new PropertyMetadata(null, OnRootStructureChanged));

    public BinaryStructure? RootStructure
    {
        get => (BinaryStructure?)GetValue(RootStructureProperty);
        set => SetValue(RootStructureProperty, value);
    }

    public event EventHandler<BinaryStructure>? StructureSelected;

    public StructureTreeView()
    {
        InitializeComponent();
    }

    private static void OnRootStructureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StructureTreeView treeView && e.NewValue is BinaryStructure structure)
        {
            treeView.TreeViewControl.Items.Clear();
            treeView.TreeViewControl.Items.Add(structure);
        }
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BinaryStructure structure)
        {
            StructureSelected?.Invoke(this, structure);
        }
    }
}
