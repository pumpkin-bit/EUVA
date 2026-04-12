// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media;
using AsmResolver.PE;
using AsmResolver.PE.File;
using Iced.Intel;
using EUVA.Core.Disassembly;
using EUVA.UI.Controls;
using EUVA.UI.Controls.Decompilation;
using EUVA.UI.Controls.Hex;
using static EUVA.UI.Controls.Hex.DisassemblerHexView;
using System.Windows.Input;
using System.Collections.Generic;
using EUVA.Core.Disassembly.Analysis;
using EUVA.Core.Services;
using EUVA.UI.Windows;
using EUVA.UI.Theming;
using EUVA.UI.Helpers;


namespace EUVA.UI;

public partial class MainWindow
{
    public static bool ShowPostDecompilerOutput = true; 

    private TabItem? _decompTabItem;
    private DisassemblerHexView? _decompDisasmView;
    private DecompilerGraphView? _decompGraphView;
    private DecompilerTextView? _decompTextView;
    private readonly DecompilerEngine _decompEngine = new();
    private readonly PseudocodeGenerator _pseudocodeGen = new();
    private long _currentFunctionOffset = -1;
    private bool _textModeActive;
    private Grid? _decompRightPanel; 
    private readonly Stack<Dictionary<string, VariableSymbol>> _aiRenameHistory = new();
    private ExecutableRange[]? _executableRanges;
    private TreeView? _importTreeView;
    private List<DllImportInfo>? _importedDlls;
    private Grid? _sidePanelContent;
    private int _peBitness = 64;
    private readonly XrefManager _xrefManager = new();
    private List<Function> _allFunctions = new();

    private void MenuDecompiler_Click(object sender, RoutedEventArgs e)
    {
        if (HexView.FileLength == 0)
        {
            MessageBox.Show("Please load a PE file first.", "No File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EnsureCenterTabControl();

        if (_decompTabItem != null)
        {
            if (_centerTabControl!.SelectedItem == _decompTabItem) return;
            
            _centerTabControl!.SelectedItem = _decompTabItem;
            _decompDisasmView!.ScrollToOffset(HexView.CurrentScrollLine * HexView.BytesPerLine);
            LogMessage("[Decomp] Switched to Disasm+Decompiler tab.");
            return;
        }

        
        _decompDisasmView = new DisassemblerHexView();
        _decompDisasmView.DisplayMode = DisasmDisplayMode.DisasmOnly;
        var mmf = HexView.GetMemoryMappedFile();
        if (mmf != null)
        {
            var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _decompDisasmView.SetDataSource(mmf, accessor, HexView.FileLength);
        }
        _decompDisasmView.OffsetSelected += DecompDisasmView_OffsetSelected;
        _decompDisasmView.FindParentFunctionRequested += (_, off) => HandleFindParentFunction(off);
        _decompDisasmView.XrefsRequested += (_, off) => HandleFindXrefs(off);

        
        SetDecompPeInfo(_decompDisasmView);

        
        _decompGraphView = new DecompilerGraphView();
        _decompGraphView.SetPseudocodeGenerator(_pseudocodeGen);
        if (mmf != null)
        {
            var accessor2 = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _decompGraphView.SetDataSource(mmf, accessor2, HexView.FileLength);
        }
        _decompGraphView.BlockSelected += DecompGraphView_BlockSelected;

        
        
        _decompTextView = new DecompilerTextView();
        _decompTextView.SetPseudocodeGenerator(_pseudocodeGen);
        _decompTextView.RenameApplied += (_, _) => 
        {
            if (_currentFunctionOffset >= 0)
            {
                RefreshDecompiledOutput();
                LogMessage("[Decomp] UI fast-refreshed based on global rename.");
            }
        };

        _decompTextView.JumpRequest += (_, addr) =>
        {
            _decompDisasmView?.ScrollToOffset(addr);
            AnalyzeFunction(addr);
        };
        _decompTextView.Visibility = Visibility.Collapsed;
        _textModeActive = false;

        
        _decompRightPanel = new Grid();
        _decompRightPanel.Children.Add(_decompGraphView);
        _decompRightPanel.Children.Add(_decompTextView);

        
        _functionListView = new ListView
        {
            Background = (Brush)FindResource("Sidebar"),
            Foreground = (Brush)FindResource("ForegroundPrimary"),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(0),
            Margin = new Thickness(0)
        };


        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BackgroundProperty, (Brush)FindResource("Surface0")));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.ForegroundProperty, (Brush)FindResource("ForegroundSecondary")));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderBrushProperty, (Brush)FindResource("Border")));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.PaddingProperty, new Thickness(8, 4, 8, 4)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        

        var headerTemplate = new ControlTemplate(typeof(GridViewColumnHeader));
        var headerBorder = new FrameworkElementFactory(typeof(Border));
        headerBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(GridViewColumnHeader.BackgroundProperty));
        headerBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(GridViewColumnHeader.BorderBrushProperty));
        headerBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(GridViewColumnHeader.BorderThicknessProperty));
        headerBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(GridViewColumnHeader.PaddingProperty));
        
        var headerContent = new FrameworkElementFactory(typeof(ContentPresenter));
        headerContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        headerBorder.AppendChild(headerContent);
        headerTemplate.VisualTree = headerBorder;
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.TemplateProperty, headerTemplate));


        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
        
        var itemTemplate = new ControlTemplate(typeof(ListViewItem));
        var itemBorder = new FrameworkElementFactory(typeof(Border));
        itemBorder.Name = "Bd";
        itemBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ListViewItem.BackgroundProperty));
        itemBorder.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));
        itemBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);
        
        var itemPresenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
        itemPresenter.SetValue(GridViewRowPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemBorder.AppendChild(itemPresenter);
        itemTemplate.VisualTree = itemBorder;
        
        var selectedTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, (Brush)FindResource("Surface0"), "Bd"));
        selectedTrigger.Setters.Add(new Setter(ListViewItem.ForegroundProperty, (Brush)FindResource("Accent")));
        itemTemplate.Triggers.Add(selectedTrigger);
        
        var hoverTrigger = new Trigger { Property = ListViewItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, 0x58, 0x5B, 0x70)), "Bd"));
        itemTemplate.Triggers.Add(hoverTrigger);
        
        itemStyle.Setters.Add(new Setter(ListViewItem.TemplateProperty, itemTemplate));

        _functionListView.ItemContainerStyle = itemStyle;

        var gv = new GridView();
        gv.ColumnHeaderContainerStyle = headerStyle;
        gv.Columns.Add(new GridViewColumn { Header = "Name", DisplayMemberBinding = new Binding("Name"), Width = 180 });
        gv.Columns.Add(new GridViewColumn { Header = "Address", DisplayMemberBinding = new Binding("AddressHex"), Width = 90 });
        gv.Columns.Add(new GridViewColumn { Header = "Type", DisplayMemberBinding = new Binding("Type"), Width = 60 });
        _functionListView.View = gv;
        _functionListView.MouseDoubleClick += (_, _) =>
        {
            if (_functionListView.SelectedItem is FunctionItem item)
            {
                _decompDisasmView?.ScrollToOffset(item.Address);
                AnalyzeFunction(item.Address);
            }
        };


        
        var splitGrid = new Grid();
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) }); 
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); 
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) }); 

        Grid.SetColumn(_decompDisasmView, 0);
        splitGrid.Children.Add(_decompDisasmView);

        var splitter1 = new GridSplitter { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)), Cursor = System.Windows.Input.Cursors.SizeWE };
        Grid.SetColumn(splitter1, 1);
        splitGrid.Children.Add(splitter1);

        Grid.SetColumn(_decompRightPanel, 2);
        splitGrid.Children.Add(_decompRightPanel);

        var splitter2 = new GridSplitter { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)), Cursor = System.Windows.Input.Cursors.SizeWE };
        Grid.SetColumn(splitter2, 3);
        splitGrid.Children.Add(splitter2);

        _sidePanelContent = new Grid();
        
        _importTreeView = new TreeView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
        };
        _importTreeView.MouseDoubleClick += ImportTreeView_MouseDoubleClick;

        _sidePanelContent.Children.Add(_functionListView!);

        Grid.SetColumn(_sidePanelContent, 4);
        splitGrid.Children.Add(_sidePanelContent);

        
        var dock = new DockPanel();
        dock.PreviewKeyDown += DecompDock_PreviewKeyDown;
        var toolbar = BuildDecompToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(splitGrid);

        _decompTabItem = new TabItem
        {
            Header = "Disasm + Decompiler",
            Content = dock
        };

        _centerTabControl!.Items.Add(_decompTabItem);
        _centerTabControl.SelectedItem = _decompTabItem;

        
        TryAutoAnalyzeEntryPoint();
        PopulateFunctionList();

        LogMessage("[Decomp] Decompiler tab created.");
    }

    private ListView? _functionListView;

    private void PopulateFunctionList()
    {
        if (_functionListView == null || _decompDisasmView == null) return;
        _functionListView.Items.Clear();

        var sections = _decompDisasmView.PeSections;
        if (sections.Length == 0) return;

        var functions = new SortedDictionary<long, FunctionItem>();
        long ep = _decompDisasmView.EntryPointFileOffset;

        if (ep >= 0 && ep < HexView.FileLength) 
            functions[ep] = new FunctionItem { Name = "EntryPoint", Address = ep, Type = "Export" };

        unsafe
        {
            var mmf = HexView.GetMemoryMappedFile();
            if (mmf != null)
            {
                using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* ptr = null;
                acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var roots = new List<long>();
                    if (ep >= 0) roots.Add(ep);

                    if (_mapper?.RootStructure != null)
                    {
                        var expDir = _mapper.RootStructure.FindByPath("Data Directories", "Export Directory");
                        if (expDir != null)
                        {
                        }
                    }

                    var discoverer = new FunctionDiscoverer();
                    _allFunctions = discoverer.Discover(ptr, HexView.FileLength, ep, _executableRanges, _peBitness);

                    foreach (var fn in _allFunctions)
                    {
                        if (!functions.ContainsKey(fn.FileOffset))
                        {
                            functions[fn.FileOffset] = new FunctionItem { Name = fn.Name, Address = fn.FileOffset };
                        }
                    }


                    BuildGlobalXrefs(ptr);

                    if (functions.Count < 5)
                    {
                        foreach (var sec in sections)
                        {
                            if ((sec.Characteristics & 0x20000000) != 0 || sec.Name.Contains(".text"))
                                ScanSectionForFunctions(ptr, sec, functions);
                        }
                    }
                }
                finally { acc.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }

        foreach (var fn in functions.Values.OrderBy(x => x.Address)) 
            _functionListView.Items.Add(fn);
    }


    private unsafe void ScanSectionForFunctions(byte* ptr, PeSectionInfo sec, SortedDictionary<long, FunctionItem> functions)
    {
        long start = sec.FileOffset;
        long end = start + sec.Size;
        if (end > HexView.FileLength) end = HexView.FileLength;

        for (long i = start; i < end - 5; i++)
        {
            
            bool isProlog = false;
            if (ptr[i] == 0x55 && ptr[i+1] == 0x8B && ptr[i+2] == 0xEC) isProlog = true; 
            else if (ptr[i] == 0x8B && ptr[i+1] == 0xFF && ptr[i+2] == 0x55 && ptr[i+3] == 0x8B && ptr[i+4] == 0xEC) isProlog = true; 
            if (isProlog && !functions.ContainsKey(i))
            {
                functions[i] = new FunctionItem { Name = $"sub_{i:X}", Address = i };
            }

            
            if (ptr[i] == 0xE8)
            {
                int rel = *(int*)(ptr + i + 1);
                long target = i + 5 + rel; 
                if (target >= start && target < end && !functions.ContainsKey(target))
                {
                    
                    if (ptr[target] == 0x55 || ptr[target] == 0x8B || ptr[target] == 0x33) 
                    {
                        functions[target] = new FunctionItem { Name = $"sub_{target:X}", Address = target };
                    }
                }
            }
        }
    }


    private void PopulateImportTreeView()
    {
        if (_importTreeView == null) return;
        _importTreeView.Items.Clear();

        if (_importedDlls != null && _importedDlls.Count > 0)
        {
            foreach (var dll in _importedDlls.OrderBy(x => x.DllName))
            {
                var dllNode = new TreeViewItem
                {
                    Header = dll.DllName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)), 
                    FontWeight = FontWeights.Bold
                };

                foreach (var entry in dll.Entries.OrderBy(x => x.Name))
                {
                    var entryNode = new TreeViewItem
                    {
                        Header = entry.Name,
                        Tag = entry,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
                    };
                    dllNode.Items.Add(entryNode);
                }
                _importTreeView.Items.Add(dllNode);
            }
            return;
        }

        var resolved = _pseudocodeGen.ResolvedImports;
        if (resolved != null && resolved.Count > 0)
        {
            var groups = resolved.Values
                .Select(v => v.Split("::", 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0])
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var dllNode = new TreeViewItem
                {
                    Header = group.Key,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
                    FontWeight = FontWeights.Bold
                };

                foreach (var funcName in group.Select(x => x[1]).OrderBy(x => x))
                {
                    var entryNode = new TreeViewItem
                    {
                        Header = funcName,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
                    };
                    dllNode.Items.Add(entryNode);
                }
                _importTreeView.Items.Add(dllNode);
            }
        }
    }

    private void ImportTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_importTreeView?.SelectedItem is TreeViewItem item && item.Tag is ImportEntry import)
        {
            FindAndJumpToXrefs(import);
        }
    }

    private uint FileOffsetToRva(long fileOffset)
    {
        if (_decompDisasmView == null) return 0;
    
        var sections = _decompDisasmView.PeSections;
        foreach (var sec in sections)
        {
            if (fileOffset >= sec.FileOffset && fileOffset < sec.FileOffset + sec.Size)
            {
                return (uint)(fileOffset - sec.FileOffset + sec.VirtualAddress);
            }
        }
        return 0;
    }

    private unsafe void BuildGlobalXrefs(byte* ptr)
    {
        if (_executableRanges == null) return;
        var allInstrs = new List<Instruction>();
        var reader = new UnsafePointerCodeReader();
        
        foreach (var range in _executableRanges)
        {
            reader.Reset(ptr + range.Start, (int)(range.End - range.Start));
            var decoder = Decoder.Create(_peBitness, reader, (ulong)FileOffsetToRva(range.Start));
            while (decoder.IP < (ulong)FileOffsetToRva(range.End))
            {
                decoder.Decode(out var instr);
                if (instr.IsInvalid) break;
                allInstrs.Add(instr);
            }
        }
        _xrefManager.BuildXrefs(allInstrs);
        LogMessage($"[Xref] Build complete: {allInstrs.Count} instructions analyzed.");
    }

    private void HandleFindParentFunction(long fileOffset)
    {
        uint rva = FileOffsetToRva(fileOffset);
        if (rva == 0) return;

        var discoverer = new FunctionDiscoverer();
        var parent = discoverer.GetParentFunction(_allFunctions, rva != 0 ? rva : fileOffset); 
        
        if (parent == null)
            parent = _allFunctions.FirstOrDefault(f => fileOffset >= f.FileOffset && fileOffset < f.EndRva);

        if (parent != null)
        {
            LogMessage($"[Decomp] Parent function found: {parent.Name} at 0x{parent.FileOffset:X8}");
            _decompDisasmView?.ScrollToOffset(parent.FileOffset);
            AnalyzeFunction(parent.FileOffset, fileOffset);
            if (!_textModeActive) ToggleDecompTextMode();
        }
        else
        {
            LogMessage("[Decomp] No parent function found for this address.");
        }
    }

    private static readonly SolidColorBrush _xMenuBg      = FreezeB(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)));
    private static readonly SolidColorBrush _xMenuBorder  = FreezeB(new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)));
    private static readonly SolidColorBrush _xMenuFg      = FreezeB(new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)));
    private static readonly SolidColorBrush _xMenuHoverBg = FreezeB(new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)));
    private static readonly SolidColorBrush _xMenuHoverFg = FreezeB(new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)));
    private static SolidColorBrush FreezeB(SolidColorBrush b) { b.Freeze(); return b; }

    private void HandleFindXrefs(long fileOffset)
    {
        uint rva = FileOffsetToRva(fileOffset);
        if (rva == 0) rva = (uint)fileOffset; 

        var sources = _xrefManager.GetXrefs(rva);
        if (sources.Count == 0)
        {
            LogMessage($"[Xref] No references found for 0x{rva:X8}");
            return;
        }

        var menu = new ContextMenu
        {
            Background = _xMenuBg,
            Foreground = _xMenuFg,
            BorderBrush = _xMenuBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 4, 0, 4),
            HasDropShadow = true
        };

        var cmTemplate = new ControlTemplate(typeof(ContextMenu));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, _xMenuBg);
        borderFactory.SetValue(Border.BorderBrushProperty, _xMenuBorder);
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(0, 4, 0, 4));
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var presenterFactory = new FrameworkElementFactory(typeof(ItemsPresenter));
        presenterFactory.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
        borderFactory.AppendChild(presenterFactory);
        cmTemplate.VisualTree = borderFactory;
        menu.Template = cmTemplate;

        foreach (var srcRva in sources)
        {
            long srcOff = RvaToFileOffset(srcRva);
            if (srcOff == -1) continue;

            var mi = new MenuItem
            {
                Header = $"0x{srcRva:X8} (Offset: 0x{srcOff:X8})",
                Foreground = _xMenuFg,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };

            var miTemplate = new ControlTemplate(typeof(MenuItem));
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 4));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            bd.AppendChild(cp);
            miTemplate.VisualTree = bd;

            var ht = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            ht.Setters.Add(new Setter(Border.BackgroundProperty, _xMenuHoverBg, "Bd"));
            ht.Setters.Add(new Setter(MenuItem.ForegroundProperty, _xMenuHoverFg));
            miTemplate.Triggers.Add(ht);
            mi.Template = miTemplate;

            mi.Click += (s, e) => {
                _decompDisasmView?.ScrollToOffset(srcOff);
                LogMessage($"[Xref] Jumped to reference at 0x{srcRva:X8}");
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private long RvaToFileOffset(long rva)
    {
        if (_decompDisasmView == null) return -1;
        
        var sections = _decompDisasmView.PeSections;
        foreach (var sec in sections)
        {
            if (rva >= sec.VirtualAddress && rva < (long)sec.VirtualAddress + sec.Size)
            {
                return (long)(rva - sec.VirtualAddress + sec.FileOffset);
            }
        }
        return rva; 
    }

    private void FindAndJumpToXrefs(ImportEntry import)
    {
        if (_executableRanges == null || _executableRanges.Length == 0) return;

        var xrefs = new List<long>();
        uint importRva = (uint)import.IatAddress;

        unsafe
        {
            var mmf = HexView.GetMemoryMappedFile();
            if (mmf == null) return;

            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            
            try
            {
                uint imageBase32 = 0;
                if (_peBitness == 32)
                {
                    int e_lfanew = *(int*)(ptr + 0x3C);
                    if (*(ushort*)(ptr + e_lfanew + 24) == 0x10B)
                        imageBase32 = *(uint*)(ptr + e_lfanew + 52);
                }

                foreach (var range in _executableRanges)
                {
                    for (long i = range.Start + 2; i < range.End - 4; i++)
                    {
                        uint currentRva = FileOffsetToRva(i);
                        if (currentRva == 0) continue; 
                        
                        bool isMatch = false;
                        int actualDisplacement = *(int*)(ptr + i);

                        if (_peBitness == 64)
                        {
                            int expectedDisplacement = (int)((long)importRva - (long)currentRva - 4);
                            if (expectedDisplacement == actualDisplacement) isMatch = true;
                        }
                        else
                        {
                            uint expectedAbsolute = imageBase32 + importRva;
                            if (expectedAbsolute == (uint)actualDisplacement) isMatch = true;
                        }

                        if (isMatch)
                        {
                            byte b1 = ptr[i - 2];
                            byte b2 = ptr[i - 1];
                            
                            LogMessage($"[XREF] Found in {range.Name} at file offset {i-2:X}! Opcode: {b1:X2} {b2:X2}");
                            xrefs.Add(i - 2); 
                        }
                    }
                }
            }
            finally { acc.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }

        if (xrefs.Count > 1)
        {
            var menu = new ContextMenu();
            foreach (var xref in xrefs)
            {
                var mi = new MenuItem { Header = $"Jump to 0x{xref:X8}" };
                mi.Click += (s, e) => JumpToXref(xref, import.Name);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }
        else if (xrefs.Count == 1)
        {
            JumpToXref(xrefs[0], import.Name);
        }
        else
        {
            LogMessage($"[Decomp] No code references found for {import.Name}.");
        }
    }
    
    private unsafe long FindPrologueBackwards(long offset)
    {
        var mmf = HexView.GetMemoryMappedFile();
        if (mmf == null) return -1;

        using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            long startLimit = Math.Max(0, offset - 0x1000);
            
            for (long i = offset; i > startLimit; i--)
            {
                if (ptr[i - 1] == 0xCC && ptr[i] != 0xCC && ptr[i] != 0x00)
                {
                    return i;
                }

                if (ptr[i] == 0x48 && ptr[i + 1] == 0x89 && ptr[i + 2] == 0x5C && ptr[i + 3] == 0x24) return i;
                
                if (ptr[i] == 0x40 && ptr[i + 1] == 0x53 && ptr[i + 2] == 0x48 && ptr[i + 3] == 0x83 && ptr[i + 4] == 0xEC) return i;
                
                if (ptr[i] == 0x48 && ptr[i + 1] == 0x83 && ptr[i + 2] == 0xEC) return i;
            }
        }
        finally { acc.SafeMemoryMappedViewHandle.ReleasePointer(); }

        return -1; 
    }


    private void JumpToXref(long address, string importName)
    {
        LogMessage($"[Decomp] Jumping to Xref at 0x{address:X8} for {importName}...");

        _decompDisasmView?.ScrollToOffset(address);

        long parentAddr = -1;
        if (_functionListView != null)
        {
            var items = _functionListView.Items.Cast<FunctionItem>().OrderBy(x => x.Address).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                long start = items[i].Address;
                
                long maxEnd = start + 0x8000; 
                long nextStart = (i + 1 < items.Count) ? items[i + 1].Address : HexView.FileLength;
                
                long end = Math.Min(maxEnd, nextStart);
                
                if (address >= start && address < end)
                {
                    parentAddr = start;
                    break;
                }
            }
        }

        if (parentAddr != -1)
        {
            LogMessage($"[Decomp] Smart Jump: Found {importName} at 0x{address:X8} in sub_{parentAddr:X}.");
            AnalyzeFunction(parentAddr, address);
            SwitchSidePanel(false);
            if (!_textModeActive) ToggleDecompTextMode();
        }
        else
        {
            LogMessage($"[Decomp] Function not in list. Scanning backwards for prologue from 0x{address:X8}...");
            
           
            long hiddenFunctionStart = FindPrologueBackwards(address);
            
            if (hiddenFunctionStart != -1)
            {
                LogMessage($"[Decomp] Found hidden function start at 0x{hiddenFunctionStart:X8}!");
                AnalyzeFunction(hiddenFunctionStart, address);
                SwitchSidePanel(false);
                if (!_textModeActive) ToggleDecompTextMode();
            }
            else
            {
                LogMessage($"[Decomp] Could not find prologue. Showing raw disassembly.");
                _decompDisasmView?.ScrollToOffset(address);
                SwitchSidePanel(false);
              
                if (_textModeActive) ToggleDecompTextMode(); 
            }
        }
    }

    public class FunctionItem
    {
        public string Name { get; set; } = "";
        public long Address { get; set; }
        public string Type { get; set; } = "Normal";
        public string AddressHex => $"0x{Address:X8}";
    }

    private Border BuildDecompToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 2, 4, 2)
        };

        
        var epBtn = new Button
        {
            Content = "⏎ Entry Point",
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
        };
        epBtn.Click += (_, _) =>
        {
            if (_decompDisasmView == null) return;
            long ep = _decompDisasmView.EntryPointFileOffset;
            if (ep >= 0 && ep < _decompDisasmView.FileLength)
            {
                _decompDisasmView.ScrollToOffset(ep);
                AnalyzeFunction(ep);
                LogMessage($"[Decomp] Analyzed entry point at 0x{ep:X8}.");
            }
            else
                LogMessage("[Decomp] Entry Point not available.");
        };
        panel.Children.Add(epBtn);

        
        var analyzeBtn = new Button
        {
            Content = "▶ Analyze Here",
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
        };
        analyzeBtn.Click += (_, _) =>
        {
            long offset = HexView.SelectedOffset;
            if (offset >= 0 && offset < HexView.FileLength)
            {
                _decompDisasmView?.ScrollToOffset(offset);
                AnalyzeFunction(offset);
                LogMessage($"[Decomp] Analyzing function at 0x{offset:X8}.");
            }
        };
        panel.Children.Add(analyzeBtn);

        var rejectAiBtn = new Button
        {
            Content = "↩ Reject AI",
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            Visibility = Visibility.Collapsed
        };
        
        var jumpAiBtn = new Button
        {
            Content = "🔍 Jump AI",
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xDC, 0xEB)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            Visibility = Visibility.Collapsed
        };

        var aiExplainBtn = new Button
        {
            Content = "✨ AI Explain",
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC1, 0x97, 0xF6)), 
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            Visibility = Visibility.Collapsed,
        };
        aiExplainBtn.Click += async (_, _) =>
        {
            if (_currentFunctionOffset < 0 || _decompTextView == null) return;

            string apiKey = AiSecurityHelper.Decrypt(EuvaSettings.Default.AiApiKeyEncrypted);

            aiExplainBtn.IsEnabled = false;
            aiExplainBtn.Content = "⏳ AI Thinking...";
            try
            {
                var blocks = _pseudocodeGen.LastBlocks;
                if (blocks == null) return;

                string miniIr = AiContextGenerator.Generate(
                    blocks,
                    _pseudocodeGen.ResolvedImports,
                    _pseudocodeGen.ResolvedStrings,
                    _pseudocodeGen.Pipeline?.LastSignature,
                    _pseudocodeGen.UserRenames);

                string dumpPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps", $"func_{_currentFunctionOffset:X}.dump");
                string cxxContext = System.IO.File.Exists(dumpPath) ? System.IO.File.ReadAllText(dumpPath) : "";
                string combinedContext = $"-- IR Context --\n{miniIr}\n\n-- C++ Final Dump --\n{cxxContext}";

                using var client = new AiClient();
                const string explainPrompt = "Analyze this decompiled C/C++ code. Provide a concise, high-level summary of what this function does. Focus on its purpose, key WinAPI calls, and logic flow. Return ONLY the summary text. Do NOT use markdown or intros.";
                
                string response = await client.RequestRenamesAsync(
                    apiKey,
                    explainPrompt,
                    combinedContext,
                    EuvaSettings.Default.AiBaseUrl,
                    EuvaSettings.Default.AiModelName);

                string summary = response.Trim();
                
                string currentDump = System.IO.File.Exists(dumpPath) ? System.IO.File.ReadAllText(dumpPath) : "";
                string newDump = $"/*\n[AI SUMMARY]\n{summary}\n*/\n\n" + currentDump;
                System.IO.File.WriteAllText(dumpPath, newDump);

                var newLines = System.IO.File.ReadAllLines(dumpPath);
                var pcLines = new System.Collections.Generic.List<EUVA.Core.Disassembly.PseudocodeLine>();
                foreach (var l in newLines)
                {
                    pcLines.Add(new EUVA.Core.Disassembly.PseudocodeLine(l, BuildSpansFast(l)));
                }
                
                _decompTextView?.OverrideText(pcLines.ToArray());
                _decompTextView?.RefreshView();

                LogMessage("[Decomp] AI Function summary generated and injected into dump.");
                rejectAiBtn.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogMessage($"[Decomp] AI Explain failed: {ex.Message}");
                MessageBox.Show($"AI Explain failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                aiExplainBtn.IsEnabled = true;
                aiExplainBtn.Content = "✨ AI Explain";
            }
        };

        rejectAiBtn.Click += (_, _) =>
        {
            _pseudocodeGen.ClearAiRenames();
            _aiRenameHistory.Clear();
            
            if (_currentFunctionOffset >= 0)
            {
                AnalyzeFunction(_currentFunctionOffset);
                LogMessage("[Decomp] All AI renames rejected and purged.");
            }
            
            rejectAiBtn.Visibility = Visibility.Collapsed;
            jumpAiBtn.Visibility = Visibility.Collapsed;
            aiExplainBtn.Visibility = Visibility.Collapsed;
        };
        panel.Children.Add(rejectAiBtn);

        jumpAiBtn.Click += (_, _) =>
        {
            _decompTextView?.JumpNextAiChange();
        };
        panel.Children.Add(jumpAiBtn);

        var aiRefactorBtn = new Button
        {
            Content = "✨ AI Refactor",
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xC2, 0xE7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
        };
        aiRefactorBtn.Click += async (_, _) =>
        {
            MessageBox.Show("Attention, this is LLM. By clicking this button, you agree to accept the losses associated with refactoring the decompiled code.\nThe purpose of this button is to simplify and adapt the reading experience for the user.\nThis button is solely for \"rough\" understanding of the code.\n\nAI - Makes mistakes, double-check its answers.");
            if (_currentFunctionOffset < 0 || _decompTextView == null) return;
            
            string apiKey = AiSecurityHelper.Decrypt(EuvaSettings.Default.AiApiKeyEncrypted);
            

            aiRefactorBtn.IsEnabled = false;
            aiRefactorBtn.Content = "⏳ AI Working...";
            try
            {
               
                var blocks = _pseudocodeGen.LastBlocks;
                if (blocks == null) return;

               
                string miniIr = AiContextGenerator.Generate(
                    blocks, 
                    _pseudocodeGen.ResolvedImports, 
                    _pseudocodeGen.ResolvedStrings,
                    _pseudocodeGen.Pipeline?.LastSignature,
                    _pseudocodeGen.UserRenames);

                string dumpPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps", $"func_{_currentFunctionOffset:X}.dump");
                string cxxContext = System.IO.File.Exists(dumpPath) ? System.IO.File.ReadAllText(dumpPath) : "";
                string combinedContext = $"-- IR Context (Structural clues) --\n{miniIr}\n\n-- Finished C++ (Target to rewrite) --\n{cxxContext}";

                using var client = new AiClient();
                
                string aiText = await client.RequestRenamesAsync(
                    apiKey,
                    EuvaSettings.Default.AiCustomPrompt,
                    combinedContext,
                    EuvaSettings.Default.AiBaseUrl,
                    EuvaSettings.Default.AiModelName);

                var match = System.Text.RegularExpressions.Regex.Match(aiText, @"```(?:cpp|c|c\+\+)?\s*\n(.*?)\n```", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (match.Success)
                {
                    aiText = match.Groups[1].Value.Trim();
                }
                else
                {
                    aiText = aiText.Trim();
                }

                System.IO.File.WriteAllText(dumpPath, aiText);

                var newLines = System.IO.File.ReadAllLines(dumpPath);
                var pcLines = new System.Collections.Generic.List<EUVA.Core.Disassembly.PseudocodeLine>();
                foreach (var l in newLines)
                {
                    pcLines.Add(new EUVA.Core.Disassembly.PseudocodeLine(l, BuildSpansFast(l)));
                }
                
                _decompTextView?.OverrideText(pcLines.ToArray());
                _decompTextView?.RefreshView();

                LogMessage($"[Decomp] AI Refactoring complete: Full C++ rewritten.");
                rejectAiBtn.Visibility = Visibility.Visible;
                jumpAiBtn.Visibility = Visibility.Visible;
                aiExplainBtn.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogMessage($"[Decomp] AI Refactoring failed: {ex.Message}");
                MessageBox.Show($"AI Request failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                aiRefactorBtn.IsEnabled = true;
                aiRefactorBtn.Content = "✨ AI Refactor";
            }
        };
        panel.Children.Add(aiRefactorBtn);

        panel.Children.Add(aiExplainBtn);

        var aiSettingsBtn = new Button
        {
            Content = "⚙ AI Settings",
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xE2, 0xD5)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
        };
        aiSettingsBtn.Click += (_, _) =>
        {
            new AiSettingsWindow { Owner = this }.ShowDialog();
        };
        panel.Children.Add(aiSettingsBtn);

        _pseudocodeGen.RenameApplied += (s, e) => 
        {
            if (e is VariableSymbol sym && sym.IsAiGenerated)
            {
                rejectAiBtn.Visibility = Visibility.Visible;
                jumpAiBtn.Visibility = Visibility.Visible;
            }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2),
            Child = panel
        };
    }

    

    private async void AnalyzeFunction(long fileOffset, long targetXref = -1)
    {
        _currentFunctionOffset = fileOffset;
        if (_decompGraphView == null || HexView.FileLength == 0) return;

        try
        {
            LayoutResult result;
            unsafe
            {
                var mmf = HexView.GetMemoryMappedFile();
                if (mmf == null) return;
                using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                byte* ptr = null;
                acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var length = HexView.FileLength - fileOffset;
                    if (length <= 0) return;

                    var maxLen = (int)Math.Min(16384, length);
                    if (maxLen <= 0) return;

                    result = _decompEngine.BuildFunctionGraph(
                        ptr + fileOffset, maxLen, fileOffset, _peBitness, _pseudocodeGen, ptr, HexView.FileLength, _executableRanges);
                }
                finally
                {
                    acc.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            Dispatcher.Invoke(() =>
            {
                _decompGraphView.SetGraphData(result);
                _decompTextView?.SetGraphData(result);
                LogMessage($"[Decomp] Graph: {result.Nodes.Length} blocks, {result.Edges.Length} edges.");
            });

            if (result.FullText != null && result.FullText.Length > 0)
            {
                string linearSource = string.Join("\n", result.FullText.Select(line => line.Text));
                
                var admin = new EUVA.Core.Robots.ProcessAdmin();
                admin.InitializeFleet();

                var annPath = await Task.Run(() => admin.RunPipelineAsync(fileOffset, linearSource));
                string dumpPath = annPath.Replace(".annotations", ".dump");

                var annLines = EUVA.Core.Robots.WorkspaceManager.ReadAnnotations(dumpPath);
                LogMessage($"[Decomp] Non-Linear Pipeline finished. Annotations file: {annPath} ({annLines.Length} entries)");

                if (ShowPostDecompilerOutput)
                {
                    try
                    {
                        var newLines = System.IO.File.ReadAllLines(dumpPath);
                        var pcLines = new System.Collections.Generic.List<EUVA.Core.Disassembly.PseudocodeLine>();

                        foreach (var l in newLines)
                        {
                            pcLines.Add(new EUVA.Core.Disassembly.PseudocodeLine(l, BuildSpansFast(l)));
                        }
                        result.FullText = pcLines.ToArray();

                        Dispatcher.Invoke(() =>
                        {
                            _decompTextView?.OverrideText(result.FullText);
                        });
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"[Decomp] Failed to format Post-Decompiler output: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"[Decomp] Analysis failed: {ex.ToString()}");
        }
    }

    private static EUVA.Core.Disassembly.PseudocodeSpan[] BuildSpansFast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<EUVA.Core.Disassembly.PseudocodeSpan>();
        if (text.TrimStart().StartsWith("//"))
            return new[] { new EUVA.Core.Disassembly.PseudocodeSpan(0, text.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Comment) };

        var spans = new System.Collections.Generic.List<EUVA.Core.Disassembly.PseudocodeSpan>();
        var regex = new System.Text.RegularExpressions.Regex(
            @"(?<String>""[^""]*"")|(?<Comment>//.*)|(?<Number>\b0x[0-9a-fA-F]+\b|\b\d+\b)|(?<Keyword>\b(if|else|while|do|for|switch|case|default|break|continue|return|goto|alloca|sizeof|reinterpret_cast|static_cast|std|filesystem|remove|try|catch)\b)|(?<Type>\b(int8_t|uint8_t|int16_t|uint16_t|int32_t|uint32_t|int64_t|uint64_t|float|double|bool|void|int|unsigned|struct|char)\b)|(?<Method>\b[a-zA-Z_]\w*(?=\s*\())|(?<Var>\b[a-zA-Z_]\w*\b)|(?<Punct>[{}()\[\].,;])|(?<Op>[+\-*/%&|^~<>=!?:]+)");

        foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
        {
            if (m.Groups["Comment"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Comment));
            else if (m.Groups["String"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.String));
            else if (m.Groups["Number"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Number));
            else if (m.Groups["Keyword"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Keyword));
            else if (m.Groups["Type"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Type));
            else if (m.Groups["Method"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Function));
            else if (m.Groups["Var"].Success)
            {
                string val = m.Groups["Var"].Value;
                if (val.StartsWith("loc_") || val.Contains("sub_") || val.StartsWith("block_") || val.StartsWith("g_0x") || val.StartsWith("spill_"))
                    spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Address));
                else
                    spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Variable));
            }
            else if (m.Groups["Op"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Operator));
            else if (m.Groups["Punct"].Success) spans.Add(new EUVA.Core.Disassembly.PseudocodeSpan(m.Index, m.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Punctuation));
        }

        if (spans.Count == 0) return new[] { new EUVA.Core.Disassembly.PseudocodeSpan(0, text.Length, EUVA.Core.Disassembly.PseudocodeSyntax.Text) };
        return spans.ToArray();
    }

    private void TryAutoAnalyzeEntryPoint()
    {
        if (_decompDisasmView == null) return;

        
        SetDecompPeInfo(_decompDisasmView);

        long ep = _decompDisasmView.EntryPointFileOffset;
        if (ep >= 0 && ep < HexView.FileLength)
        {
            _decompDisasmView.ScrollToOffset(ep);
            AnalyzeFunction(ep);
        }
        else
        {
            
            _decompDisasmView.ScrollToOffset(0);
        }
    }

    private void SetDecompPeInfo(DisassemblerHexView view)
    {
        if (_mapper?.RootStructure == null) return;
        try
        {
            var root = _mapper.RootStructure;
            var sectionsNode = root.FindByPath("Sections");
            long entryPointFileOffset = -1;
            var secList = new PeSectionInfo[sectionsNode?.Children.Count ?? 0];
            int secCount = 0;

            if (sectionsNode != null)
            {
                foreach (var secNode in sectionsNode.Children)
                {
                    long ptrRawData = secNode.Offset ?? 0;
                    long secSize = secNode.Size ?? 0;
                    uint virtualAddr = 0;
                    uint characteristics = 0;
                    uint virtualSize = 0;
                    foreach (var field in secNode.Children)
                    {
                        if (field.Name == "VirtualAddress" && field.Value != null)
                        {
                            try { virtualAddr = Convert.ToUInt32(field.Value); } catch { }
                        }
                        else if (field.Name == "VirtualSize" && field.Value != null)
                        {
                            try { virtualSize = Convert.ToUInt32(field.Value); } catch { }
                        }
                        else if (field.Name == "Characteristics" && field.Value != null)
                        {
                            try { characteristics = Convert.ToUInt32(field.Value); } catch { }
                        }
                    }
                    if (ptrRawData < 0 || ptrRawData >= HexView.FileLength) ptrRawData = 0;
                    if (secCount < secList.Length)
                    {
                        secList[secCount] = new PeSectionInfo(
                            secNode.Name, ptrRawData, virtualSize > 0 ? virtualSize : (uint)secSize, virtualAddr, characteristics);
                        secCount++;
                    }
                }
            }

            var optHeader = root.FindByPath("NT Headers", "Optional Header");
            if (optHeader != null)
            {
                foreach (var field in optHeader.Children)
                {
                    if (field.Name == "AddressOfEntryPoint" && field.Value != null)
                    {
                        uint epRva = Convert.ToUInt32(field.Value);
                        entryPointFileOffset = RvaToFileOffset(epRva);
                        break;
                    }
                }
            }

            var execRanges = new List<ExecutableRange>();
            for (int i = 0; i < secCount; i++)
            {
                var s = secList[i];
                if ((s.Characteristics & 0x20000000) != 0 || 
                    (s.Characteristics & 0x00000020) != 0 ||
                    s.Name.Contains(".text", StringComparison.OrdinalIgnoreCase))
                {
                    execRanges.Add(new ExecutableRange { Start = s.FileOffset, End = s.FileOffset + s.Size, Name = s.Name });
                }
            }
            _executableRanges = execRanges.ToArray();

            uint importRva = 0;
            optHeader = root.FindByPath("NT Headers", "Optional Header");
            if (optHeader != null)
            {
                var dataDirs = optHeader.Children.FirstOrDefault(c => c.Name == "Data Directories");
                if (dataDirs != null)
                {
                    var importDir = dataDirs.Children.ElementAtOrDefault(1);
                    if (importDir != null)
                    {
                        var rvaField = importDir.Children.FirstOrDefault(f => f.Name == "RVA") 
                                     ?? importDir.Children.FirstOrDefault(f => f.Name == "VirtualAddress");
                        if (rvaField?.Value != null) importRva = Convert.ToUInt32(rvaField.Value);
                    }
                }
            }

            unsafe
            {
                var mmf = HexView.GetMemoryMappedFile();
                if (mmf != null)
                {
                    using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    byte* ptr = null;
                    acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    try
                    {
                        int e_lfanew = *(int*)(ptr + 0x3C);
                        
                        ushort magic = *(ushort*)(ptr + e_lfanew + 24);
                        _peBitness = (magic == 0x10B) ? 32 : 64;

                        int dataDirOffset = e_lfanew + 24 + (_peBitness == 32 ? 96 : 112);
                        int importDirOffset = dataDirOffset + 8; 
                        importRva = *(uint*)(ptr + importDirOffset);

                        if (importRva != 0)
                        {
                            var scanner = new PeImportScanner();
                            _importedDlls = scanner.Scan(ptr, HexView.FileLength, secList, secCount, importRva, _peBitness);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"[Decomp] Native Header parsing crashed: {ex.Message}");
                    }
                    finally { acc.SafeMemoryMappedViewHandle.ReleasePointer(); }
                }
            }
            PopulateImportTreeView();
            view.SetPeInfo(entryPointFileOffset, secList, secCount);
        }
        catch (Exception ex)
        {
            LogMessage($"[Decomp] PE info extraction failed: {ex.Message}");
        }
    }
       
    private void DecompDock_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5)
        {
            ToggleDecompTextMode();
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            if (e.SystemKey == Key.E)
            {
                SwitchSidePanel(true);
                e.Handled = true;
            }
            else if (e.SystemKey == Key.R)
            {
                SwitchSidePanel(false);
                e.Handled = true;
            }
        }
    }

    private void SwitchSidePanel(bool showImports)
    {
        if (_sidePanelContent == null || _functionListView == null || _importTreeView == null) return;
        
        _sidePanelContent.Children.Clear();
        if (showImports)
        {
            PopulateImportTreeView();
            _sidePanelContent.Children.Add(_importTreeView);
            LogMessage("[Decomp] Side Panel: Switched to Imports (IAT).");
        }
        else
        {
            _sidePanelContent.Children.Add(_functionListView);
            LogMessage("[Decomp] Side Panel: Switched to Functions (.text).");
        }
    }

    private void ToggleDecompTextMode()
    {
        if (_decompGraphView == null || _decompTextView == null) return;

        _textModeActive = !_textModeActive;

        if (_textModeActive)
        {
            _decompGraphView.Visibility = Visibility.Collapsed;
            _decompTextView.Visibility = Visibility.Visible;
            _decompTextView.RefreshView();
            if (_decompTabItem != null)
                _decompTabItem.Header = "Disasm + Pseudocode";
            LogMessage("[Decomp] Switched to TEXT mode (F5 to toggle back).");
        }
        else
        {
            _decompTextView.Visibility = Visibility.Collapsed;
            _decompGraphView.Visibility = Visibility.Visible;
            _decompGraphView.RefreshView();
            if (_decompTabItem != null)
                _decompTabItem.Header = "Disasm + Decompiler";
            LogMessage("[Decomp] Switched to GRAPH mode (F5 to toggle back).");
        }
    }


    private void DecompDisasmView_OffsetSelected(object? sender, long offset)
    {
        HexView.SelectedOffset = offset;
        HexView_OffsetSelected(HexView, offset);
    }

    private void DecompGraphView_BlockSelected(object? sender, long offset)
    {
        _decompDisasmView?.ScrollToOffset(offset);
        LogMessage($"[Decomp] Graph block selected at 0x{offset:X8}.");
    }

    private void RefreshDecompOnFileLoad()
    {
        if (_decompDisasmView == null) return;

        var mmf = HexView.GetMemoryMappedFile();
        if (mmf != null)
        {
            var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _decompDisasmView.SetDataSource(mmf, acc, HexView.FileLength);
        }
        SetDecompPeInfo(_decompDisasmView);
        _decompDisasmView.RefreshView();

        if (_decompGraphView != null && mmf != null)
        {
            var acc2 = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _decompGraphView.SetDataSource(mmf, acc2, HexView.FileLength);
        }

        TryAutoAnalyzeEntryPoint();
        PopulateFunctionList();
    }
    private void RefreshDecompiledOutput()
    {
        _decompTextView?.RefreshView();
        _decompGraphView?.RefreshView();
    }
}
