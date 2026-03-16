// SPDX-License-Identifier: GPL-3.0-or-later


using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using EUVA.Core.Parsers;
using EUVA.Core.Detectors;
using EUVA.Core.Detectors.Samples;
using EUVA.Core.Models;
using EUVA.UI.Theming;
using System.Windows.Controls;
using System.Windows.Media;
using EUVA.UI.Controls;
using EUVA.UI.Controls.Decompilation;
using EUVA.UI.Controls.Hex;
using EUVA.UI.Controls.Properties;
using EUVA.UI.Controls.Trees;
using EUVA.UI.Parsers;
using System.Text.RegularExpressions;
using System.Linq;
using System.Diagnostics;

namespace EUVA.UI;

public static class HotkeyManager
{
    private static Dictionary<(ModifierKeys, Key), EUVAAction> _bindings = new();

    public static EUVAAction GetAction(ModifierKeys mod, Key key)
        => _bindings.TryGetValue((mod, key), out var a) ? a : EUVAAction.None;

    public static void LoadDefaults()
    {
        _bindings.Clear();
        _bindings[(ModifierKeys.Alt, Key.D1)] = EUVAAction.NavInspector;
        _bindings[(ModifierKeys.Alt, Key.D2)] = EUVAAction.NavSearch;
        _bindings[(ModifierKeys.Alt, Key.D3)] = EUVAAction.NavDetections;
        _bindings[(ModifierKeys.Alt, Key.D4)] = EUVAAction.NavProperties;
        _bindings[(ModifierKeys.Control, Key.Z)] = EUVAAction.Undo;
        _bindings[(ModifierKeys.Control | ModifierKeys.Shift, Key.Z)] = EUVAAction.FullUndo;
        _bindings[(ModifierKeys.Control, Key.C)] = EUVAAction.CopyHex;
        _bindings[(ModifierKeys.Control | ModifierKeys.Shift, Key.C)] = EUVAAction.CopyCArray;
        MainWindow.Instance?.Log("[System] Default hotkeys loaded.", Brushes.Gray);
    }

    public static void Load(string path)
    {
        _bindings.Clear();
        foreach (var rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Split('#')[0].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            try
            {
                var parts = line.Split('=');
                if (parts.Length != 2) throw new Exception("Format error (Missing '=')");
                if (!Enum.TryParse(parts[0].Trim(), true, out EUVAAction action))
                    throw new Exception($"Unknown action: '{parts[0].Trim()}'");

                var keys = parts[1].Split('+');
                ModifierKeys mods = ModifierKeys.None;
                Key targetKey = Key.None;
                foreach (var k in keys)
                {
                    string token = k.Trim();
                    if (Enum.TryParse(token, true, out ModifierKeys m)) mods |= m;
                    else if (Enum.TryParse(token, true, out Key tk)) targetKey = tk;
                    else throw new Exception($"Unknown token: '{token}'");
                }
                if (targetKey == Key.None) throw new Exception("Base key not found");
                _bindings[(mods, targetKey)] = action;
            }
            catch (Exception ex) { MainWindow.Instance?.Log($"[ERROR] .htk: {ex.Message}", Brushes.Red); }
        }
    }
}

public enum EUVAAction
{
    None,
    NavInspector, NavSearch, NavDetections, NavProperties,
    CopyHex, CopyCArray, CopyPlainText,
    Undo, FullUndo
}
public readonly record struct SearchResult(string Offset, string Size, string Value, string Context);
public readonly record struct InspectorItem(string Name, string Value, string RawHex);

public class MethodContainer
{
    public string Name = "";
    public string Access = "";
    public List<string> Body = new();
    public Dictionary<string, long> Clinks = new();
}
public static class AsmLogic
{
    private static readonly (string Name, byte Idx)[] RegTable =
    {
        ("eax",0), ("ebp",5), ("ebx",3), ("ecx",1),
        ("edi",7), ("edx",2), ("esi",6), ("esp",4)
    };
    private static readonly (string Mnemonic, byte Op)[] OpsTable =
    {
        ("add",0x01), ("and",0x21), ("cmp",0x39), ("jmp",0xE9),
        ("mov_eax",0xB8), ("or",0x09), ("sub",0x29), ("xor",0x31)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFindReg(string name, out byte idx)
    {
        foreach (var (n, i) in RegTable)
            if (n == name) { idx = i; return true; }
        idx = 0; return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFindOp(string mnemonic, out byte op)
    {
        foreach (var (m, o) in OpsTable)
            if (m == mnemonic) { op = o; return true; }
        op = 0; return false;
    }

    public static byte[]? Assemble(string part, long currentAddr)
    {
        var tokens = part.ToLower().Replace(",", " ")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        string mnemonic = tokens[0];
        if (mnemonic == "nop") return new byte[] { 0x90 };
        if (mnemonic == "ret") return new byte[] { 0xC3 };

        if (mnemonic == "jmp" && tokens.Length == 2 &&
            long.TryParse(tokens[1], out long target))
        {
            int rel = (int)(target - (currentAddr + 5));
            var result = new byte[5];
            result[0] = 0xE9;
            WriteLE32(result, 1, rel);
            return result;
        }

        if (tokens.Length == 3 && TryFindOp(mnemonic, out byte opCode) &&
            TryFindReg(tokens[1], out byte dest) && TryFindReg(tokens[2], out byte src))
            return new byte[] { opCode, (byte)(0xC0 + (src << 3) + dest) };

        if (mnemonic == "mov" && tokens.Length == 3 &&
            TryFindReg(tokens[1], out byte regIdx) &&
            int.TryParse(tokens[2], out int val))
        {
            var result = new byte[5];
            result[0] = (byte)(0xB8 + regIdx);
            WriteLE32(result, 1, val);
            return result;
        }

        return null;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLE32(byte[] buf, int off, int v)
    {
        buf[off] = (byte)v;
        buf[off + 1] = (byte)(v >> 8);
        buf[off + 2] = (byte)(v >> 16);
        buf[off + 3] = (byte)(v >> 24);
    }
}
public static class DataParser
{
    public static (long value, int size) ReadLEB128(byte[] data, bool signed)
    {
        if (data.Length == 0) return (0, 0);

        byte b0 = data[0];
        if ((b0 & 0x80) == 0)
        {
            long v = b0 & 0x7F;
            if (signed && (b0 & 0x40) != 0) v |= -1L << 7;
            return (v, 1);
        }
        if (data.Length >= 2)
        {
            byte b1 = data[1];
            if ((b1 & 0x80) == 0)
            {
                long v = (b0 & 0x7FL) | ((b1 & 0x7FL) << 7);
                if (signed && (b1 & 0x40) != 0) v |= -1L << 14;
                return (v, 2);
            }
        }
        long result = 0; int shift = 0; int pos = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (signed && shift < 64 && (b & 0x40) != 0) result |= -(1L << shift);
                break;
            }
        }
        return (result, pos);
    }

    public static string ToDosDate(ushort v) =>
        $"{((v >> 9) & 0x7F) + 1980:D4}-{(v >> 5) & 0x0F:D2}-{v & 0x1F:D2}";
    public static string ToDosTime(ushort v) =>
        $"{v >> 11:D2}:{(v >> 5) & 0x3F:D2}:{(v & 0x1F) * 2:D2}";
}

public static class InspectorHelper
{
    public static (long value, int length) ReadULEB128(byte[] data)
    {
        if (data.Length == 0) return (0, 0);
        byte b0 = data[0];
        if ((b0 & 0x80) == 0) return (b0 & 0x7FL, 1);
        if (data.Length >= 2)
        {
            byte b1 = data[1];
            if ((b1 & 0x80) == 0)
                return ((b0 & 0x7FL) | ((b1 & 0x7FL) << 7), 2);
        }
        long result = 0; int shift = 0; int pos = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return (result, pos);
    }
    public static string ParseDosDate(ushort v) => DataParser.ToDosDate(v);
    public static string ParseDosTime(ushort v) => DataParser.ToDosTime(v);
}
public partial class MainWindow : Window
{
    private PEMapper? _mapper;
    private DetectorManager _detectorManager = new();
    public static MainWindow Instance { get; private set; } = null!;
    private readonly ConcurrentQueue<(string Text, Brush Color)> _logQueue = new();
    private readonly System.Windows.Threading.DispatcherTimer _logFlushTimer;
    private static readonly Regex _whitespaceRegex =
        new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex _clinkBracketRegex =
        new(@"\[(.*?)\]", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly byte[] _inspectorBuf = new byte[16];
    private readonly Stack<(long Offset, byte[] Old, byte[] New)> _undoStack = new();
    private readonly Stack<int> _transactionSteps = new();
    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    private string? _loadedFilePath;

    private FileStream? _rawVideoStream;
    private byte[]? _frameBuffer;
    private readonly int _videoWidth = 24;
    private readonly int _videoHeight = 26;
    private int _videoTotalSize;
    private string? _activeScriptPath;
    private FileSystemWatcher? _scriptWatcher;
    private volatile bool _isProcessingScript = false;
    private string? _lastScriptPath;
    private bool IsLittleEndian = true;


    private readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkey.cfg");

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _logFlushTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(100) };
        _logFlushTimer.Tick += FlushLogBuffer;
        _logFlushTimer.Start();

        InitializeSystemSettings();
        InitializeDetectors();
        InitializeYara();

        HexView.ScrollChanged += (scrollLine, visibleLines, bytesPerLine) =>
            ByteMinimap.UpdateViewport(scrollLine, visibleLines, bytesPerLine);
    }

    public void Log(string message, Brush color)
    {
        if (color.CanFreeze && !color.IsFrozen) color.Freeze();
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        _logQueue.Enqueue((line, color));
    }

    private void SafeLog(string msg, Brush color) => Log(msg, color);
    private void LogMessage(string msg) => Log(msg, Brushes.White);
    private void SafeLogThreadSafe(string msg, Brush c) => Log(msg, c);

    private void FlushLogBuffer(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        while (_logQueue.TryDequeue(out var entry))
            sb.Append(entry.Text);
        ConsoleLog.AppendText(sb.ToString());
        ConsoleLog.ScrollToEnd();
    }

    private async void InitializeSystemSettings()
    {
        LogMessage("[System] Initializing environment...");
        HotkeyManager.LoadDefaults();
        ThemeManager.Instance.ApplyDefaultTheme();

        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "euva.cfg");
        if (File.Exists(configPath))
        {
            try
            {
                var lines = File.ReadAllLines(configPath);
                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]) && File.Exists(lines[0]))
                {
                    HotkeyManager.Load(lines[0]);
                    LogMessage($"[System] Hotkeys restored: {Path.GetFileName(lines[0])}");
                }
                if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) && File.Exists(lines[1]))
                {
                    ThemeManager.Instance.LoadTheme(lines[1]);
                    LogMessage($"[System] Theme restored: {Path.GetFileName(lines[1])}");
                }
            }
            catch (Exception ex) { LogMessage($"[ERROR] Auto-load failed: {ex.Message}"); }
        }
        
        try 
        {
            LogMessage("[System] Loading Glass Engine C# Scripts...");
            await EUVA.Core.Scripting.ScriptLoader.Instance.InitializeAsync();
            LogMessage("[System] Scripts loaded successfully.");
        }
        catch (Exception ex)
        {
            LogMessage($"[ERROR] Script loader failed: {ex.Message}");
        }

        HexView.InvalidateVisual();
    }

    private void InitializeDetectors()
    {
        _detectorManager = new DetectorManager();
        _detectorManager.RegisterDetector(new UPXDetector());
        _detectorManager.RegisterDetector(new ThemidaDetector());

        string pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
        if (!Directory.Exists(pluginsDir))
            Directory.CreateDirectory(pluginsDir);

        _detectorManager.LoadFromDirectory(pluginsDir);

        LogMessage($"Loaded {_detectorManager.Detectors.Count} detectors");
    }

    private void UpdateGlobalConfig(string? htkPath = null, string? themePath = null)
    {
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "euva.cfg");
        string defaultTheme = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Theming", "default.themes");
        string currentHtk = "", currentTheme = defaultTheme, alwaysDefault = defaultTheme;

        if (File.Exists(configPath))
        {
            var lines = File.ReadAllLines(configPath);
            if (lines.Length > 0) currentHtk = lines[0];
            if (lines.Length > 1) currentTheme = lines[1];
            if (lines.Length > 2) alwaysDefault = lines[2];
        }
        File.WriteAllLines(configPath, new[]
        {
            htkPath   ?? currentHtk,
            themePath ?? currentTheme,
            alwaysDefault
        });
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenFile();
    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        { Filter = "Executable Files (*.exe;*.dll)|*.exe;*.dll|All Files (*.*)|*.*", Title = "Select PE File" };
        if (dialog.ShowDialog() == true) LoadFile(dialog.FileName);
    }

    private void LoadFile(string filePath)
    {
        try
        {
            LogMessage($"Loading file: {Path.GetFileName(filePath)}");
            Mouse.OverrideCursor = Cursors.Wait;

            HexView.LoadFile(filePath);
            long fileSize = HexView.FileLength;

            byte[] headerData = new byte[Math.Min(fileSize, 0x10000)];
            HexView.ReadBytes(0, headerData);

            _mapper = new PEMapper();
            var structure = _mapper.Parse(headerData.AsSpan());

            StructureTree.RootStructure = structure;
            HexView.Regions = _mapper.GetRegions().ToList();

try
{
    LogMessage("Parsing IAT for WinAPI symbols...");
    var iatParser = new EUVA.UI.Parsers.PeIatParser();
    
   
    ulong imageBase = 0x400000; 

  
    if (headerData.Length > 0x40)
    {
        
        int peOffset = BitConverter.ToInt32(headerData, 0x3C);
        
        if (peOffset > 0 && peOffset + 26 < headerData.Length)
        {
            ushort peSig = BitConverter.ToUInt16(headerData, peOffset);
            ushort magic = BitConverter.ToUInt16(headerData, peOffset + 24);
            
            LogMessage($"[Debug] PE Offset: 0x{peOffset:X}, Magic: 0x{magic:X}");

            if (magic == 0x20B) 
            {
                imageBase = 0x140000000;
                LogMessage("[System] x64 detected via Magic 0x20B. Switching to x64 Base.");
            }
            else if (magic == 0x10B) 
            {
                imageBase = 0x400000;
                LogMessage("[System] x86 detected via Magic 0x10B.");
            }
        }
    }

    LogMessage($"[System] Final ImageBase for IAT: 0x{imageBase:X}");

    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        if (iatParser.Parse(fs, imageBase))
        {
            _pseudocodeGen.ResolvedImports = iatParser.ParsedImports;
            _pseudocodeGen.SetImports(iatParser.ParsedImports);
            LogMessage($"[IAT] Resolved {_pseudocodeGen.ResolvedImports.Count} imports.");
        }
    }

}

catch (Exception iatEx)
{
    LogMessage($"IAT Error: {iatEx.Message}");
}
            ByteMinimap.SetDataSource((offset, buf, count) =>
            {
                int toRead = (int)Math.Min(count, fileSize - offset);
                if (toRead <= 0) return 0;
                HexView.ReadBytes(offset, buf.AsSpan(0, toRead));
                return toRead;
            }, fileSize);
            
            StatusText.Text = $"Loaded: {Path.GetFileName(filePath)} ({fileSize:N0} bytes)";
            LogMessage("File mapped successfully");

            lock (_undoStack)
            {
                _undoStack.Clear();
                _transactionSteps.Clear();
            }
            
            _loadedFilePath = filePath;
            OnFileLoaded();
            RefreshDisasmOnFileLoad();
            RefreshDecompOnFileLoad(); 
        }
        catch (Exception ex)
        {
            LogMessage($"ERROR: {ex.Message}");
            MessageBox.Show($"Failed to load file: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0) LoadFile(files[0]);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ByteMinimap_NavigateRequested(object? sender, long offset)
    {
        HexView.ScrollToOffset(offset);
    }

    private void HexView_OffsetSelected(object sender, long offset)
    {
        PropertyGrid.SelectedOffset = offset;
        long total = HexView.FileLength;
        if (total == 0) return;

        long remaining = total - offset;
        if (remaining > 0) HexView.ReadBytes(offset, _inspectorBuf);

        var items = new List<InspectorItem>(16);
        try
        {
            if (remaining >= 1)
            {
                byte b = _inspectorBuf[0];
                items.Add(new InspectorItem("Int8 / UInt8", $"{(sbyte)b} | {b}", $"{b:X2}"));
                items.Add(new InspectorItem("binary (8 bit)", Convert.ToString(b, 2).PadLeft(8, '0'), "-"));
            }
            if (remaining >= 2)
            {
                ushort v = IsLittleEndian
                    ? (ushort)(_inspectorBuf[0] | (_inspectorBuf[1] << 8))
                    : (ushort)((_inspectorBuf[0] << 8) | _inspectorBuf[1]);
                items.Add(new InspectorItem("Int16 / UInt16",
                    $"{(short)v} | {v}", $"{_inspectorBuf[0]:X2}-{_inspectorBuf[1]:X2}"));
                items.Add(new InspectorItem("Дата / Время DOS",
                    $"{DataParser.ToDosDate(v)} {DataParser.ToDosTime(v)}", "MS-DOS"));
            }
            if (remaining >= 3)
            {
                int v = IsLittleEndian
                    ? _inspectorBuf[0] | (_inspectorBuf[1] << 8) | (_inspectorBuf[2] << 16)
                    : (_inspectorBuf[0] << 16) | (_inspectorBuf[1] << 8) | _inspectorBuf[2];
                items.Add(new InspectorItem("Int24 / UInt24", v.ToString(),
                    $"{_inspectorBuf[0]:X2}-{_inspectorBuf[1]:X2}-{_inspectorBuf[2]:X2}"));
            }
            if (remaining >= 4)
            {
                var s4 = new ReadOnlySpan<byte>(_inspectorBuf, 0, 4);
                uint uv = IsLittleEndian ? MemoryMarshal.Read<uint>(s4)
                                          : BinaryPrimitives.ReadUInt32BigEndian(s4);
                float fv = MemoryMarshal.Read<float>(s4);
                items.Add(new InspectorItem("Int32 / UInt32", $"{(int)uv} | {uv}",
                    BitConverter.ToString(_inspectorBuf, 0, 4)));
                items.Add(new InspectorItem("Single (float32)", fv.ToString("G6"), "-"));
                items.Add(new InspectorItem("time_t (32 бит)",
                    uv <= int.MaxValue
                        ? DateTimeOffset.FromUnixTimeSeconds(uv).DateTime.ToString()
                        : "Invalid",
                    "Unix"));
            }
            if (remaining >= 8)
            {
                var s8 = new ReadOnlySpan<byte>(_inspectorBuf, 0, 8);
                ulong uv = IsLittleEndian ? MemoryMarshal.Read<ulong>(s8)
                                           : BinaryPrimitives.ReadUInt64BigEndian(s8);
                double dv = MemoryMarshal.Read<double>(s8);
                items.Add(new InspectorItem("Int64 / UInt64", uv.ToString(),
                    BitConverter.ToString(_inspectorBuf, 0, 8)));
                items.Add(new InspectorItem("Double (float64)", dv.ToString("G8"), "-"));
                items.Add(new InspectorItem("FILETIME",
                    uv <= 2_650_467_743_999_999_999UL
                        ? DateTime.FromFileTime((long)uv).ToString()
                        : "Invalid",
                    "Win32"));

                items.Add(new InspectorItem("OLETIME",
                    !double.IsNaN(dv) && dv >= -657434.0 && dv <= 2958465.99999999
                        ? DateTime.FromOADate(dv).ToString()
                        : "Invalid",
                    "OLE"));
            }
            if (remaining >= 16)
                items.Add(new InspectorItem("GUID / UUID",
                    new Guid(_inspectorBuf).ToString("B").ToUpper(), "System"));

            int lebLen = (int)Math.Min(10, remaining);
            if (lebLen > 0)
            {
                var uleb = DataParser.ReadLEB128(_inspectorBuf[..lebLen], false);
                items.Add(new InspectorItem("ULEB128",
                    uleb.value.ToString(), $"Size: {uleb.size}"));
            }
        }
        catch (Exception ex)
        {
            Log($"[Inspector] Decode error at 0x{offset:X8}: {ex.Message}", Brushes.OrangeRed);
        }
        DataInspectorList.ItemsSource = items;
    }

    private void PerformUndo()
    {
        lock (_undoStack)
        {
            if (_undoStack.Count == 0) return;
            var (offset, oldData, _) = _undoStack.Pop();
            for (int i = 0; i < oldData.Length; i++) HexView.WriteByte(offset + i, oldData[i]);
        }
        HexView.InvalidateVisual();
    }

    private void PerformFullUndo()
    {
        lock (_undoStack)
        {
            if (_transactionSteps.Count == 0) return;
            int count = _transactionSteps.Pop();
            for (int i = 0; i < count && _undoStack.Count > 0; i++)
            {
                var (offset, oldData, _) = _undoStack.Pop();
                for (int j = 0; j < oldData.Length; j++) HexView.WriteByte(offset + j, oldData[j]);
            }
        }
        HexView.InvalidateVisual();
    }
    private async void MenuRunDetectors_Click(object sender, RoutedEventArgs e)
    {
        if (_mapper?.RootStructure == null || HexView.FileLength == 0)
        {
            MessageBox.Show("Please load a PE file first.", "No File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LogMessage("Running detection analysis...");
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            int size = (int)Math.Min(HexView.FileLength, 10 * 1024 * 1024);
            byte[] buf = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                HexView.ReadBytes(0, buf.AsSpan(0, size));
                var mem = new ReadOnlyMemory<byte>(buf, 0, size);
                var progress = new Progress<string>(msg => LogMessage(msg));
                var results = await _detectorManager.AnalyzeAsync(
                    mem, _mapper.RootStructure, progress);

                DetectionList.ItemsSource = null;
                DetectionList.Items.Clear();
                DetectionList.ItemsSource = results;
                ResetYaraState();

                LogMessage($"Analysis complete. Found {results.Count} matches.");
                if (results.Count > 0)
                    LogMessage($"Best match: {_detectorManager.GetBestMatch(results)}");
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        catch (Exception ex) { LogMessage($"ERROR: {ex.Message}"); }
        finally { Mouse.OverrideCursor = null; }
    }

    private void MenuCalculateEntropy_Click(object sender, RoutedEventArgs e)
    {
        if (_mapper == null || HexView.FileLength == 0)
        {
            MessageBox.Show("Please load a PE file first.", "No File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LogMessage("Calculating entropy (Top 10MB)...");
        int size = (int)Math.Min(HexView.FileLength, 10 * 1024 * 1024);
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            HexView.ReadBytes(0, buf.AsSpan(0, size));
            var span = buf.AsSpan(0, size);
            var regions = _mapper.GetRegions();
            foreach (var r in SignatureScanner.AnalyzeSectionEntropy(span, regions))
                LogMessage($"  {r.Key}: {r.Value:F3} bits");
            LogMessage($"Overall entropy: {SignatureScanner.CalculateEntropy(span):F3} bits");
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
    private long FindSignature(string pattern)
    {
        var parts = pattern.Split(' ');
        int patLen = parts.Length;
        if (patLen == 0) return -1;

        byte[] pat = new byte[patLen];
        bool[] isMask = new bool[patLen];
        bool anyWild = false;

        for (int i = 0; i < patLen; i++)
        {
            if (parts[i] == "??") { isMask[i] = true; anyWild = true; }
            else pat[i] = FastHexByte(parts[i]);
        }

        long fileLen = HexView.FileLength;
        if (fileLen < patLen) return -1;

        return anyWild
            ? FindBmhWild(pat, isMask, patLen, fileLen)
            : FindExact(pat, patLen, fileLen);
    }
    private long FindExact(byte[] pat, int patLen, long fileLen)
    {
        const int ChunkSize = 256 * 1024;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize + patLen);
        try
        {
            ReadOnlySpan<byte> patSpan = pat.AsSpan(0, patLen);
            long pos = 0;

            while (pos <= fileLen - patLen)
            {
                long avail = fileLen - pos;
                int toRead = (int)Math.Min(ChunkSize, avail);
                int overlap = (int)Math.Min(patLen - 1, avail - toRead);
                int total = toRead + overlap;

                HexView.ReadBytes(pos, chunk.AsSpan(0, total));
                int found = chunk.AsSpan(0, total).IndexOf(patSpan);
                if (found >= 0 && found < toRead) return pos + found;
                pos += toRead;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        return -1;
    }
    private long FindBmhWild(byte[] pat, bool[] isMask, int patLen, long fileLen)
    {
        int[] bad = new int[256];
        int defaultSkip = patLen;
        for (int i = patLen - 1; i >= 0; i--)
        {
            if (isMask[i])
            {
                defaultSkip = patLen - 1 - i;
                if (defaultSkip == 0) defaultSkip = 1; 
                break;
            }
        }
        for (int i = 0; i < 256; i++) bad[i] = defaultSkip;
        for (int i = 0; i < patLen - 1; i++)
            if (!isMask[i]) 
            {
                int skip = patLen - 1 - i;
                if (skip < bad[pat[i]]) bad[pat[i]] = skip;
            }

        const int ChunkSize = 256 * 1024;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize + patLen);
        try
        {
            long pos = 0;
            while (pos <= fileLen - patLen)
            {
                long avail = fileLen - pos;
                int toRead = (int)Math.Min(ChunkSize, avail);
                int overlap = (int)Math.Min(patLen - 1, avail - toRead);
                int total = toRead + overlap;

                int limit = total - patLen + 1;

                HexView.ReadBytes(pos, chunk.AsSpan(0, total));

                int i = 0;
                while (i < limit)
                {
                    bool match = true;
                    if (!isMask[patLen - 1] && chunk[i + patLen - 1] != pat[patLen - 1])
                    {
                        match = false;
                    }
                    else
                    {
                        for (int j = patLen - 2; j >= 0; j--)
                            if (!isMask[j] && chunk[i + j] != pat[j]) { match = false; break; }
                    }

                    if (match) return pos + i;
                    i += bad[chunk[i + patLen - 1]];
                }
                pos += toRead;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        return -1;
    }



    private async Task RunParallelEngine(string scriptPath)
    {
        if (HexView.FileLength == 0) { Log("[Engine] FATAL: No file loaded!", Brushes.Red); return; }

        SafeLog($"[Engine] Starting script: {Path.GetFileName(scriptPath)}", Brushes.White);
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(scriptPath); }
        catch (Exception ex) { Log($"[Engine] IO Error: {ex.Message}", Brushes.Red); return; }

        await Task.Run(() =>
        {
            try
            {
                var interpreter = new DslInterpreter(this, lines);
                interpreter.Execute();
                
                if (interpreter.ChangesCount > 0)
                {
                    lock (_undoStack) { _transactionSteps.Push(interpreter.ChangesCount); }
                    SafeLog($"[Engine] Success. {interpreter.ChangesCount} operations committed.", Brushes.SpringGreen);
                }
            }
            catch (Exception ex)
            {
                SafeLog($"[fatal error] {ex.Message}", Brushes.OrangeRed);
            }
        });
    }

    private class DslInterpreter
    {
        private readonly MainWindow _parent;
        private readonly string[] _lines;
        private readonly Dictionary<string, long> _variables = new();
        private int _currentLine = 0;
        public int ChangesCount { get; private set; } = 0;

        public DslInterpreter(MainWindow parent, string[] lines)
        {
            _parent = parent;
            _lines = lines;
        }

        public void Execute()
        {
            _currentLine = 0;
            ExecuteBlock(0);
        }

        private void ExecuteBlock(int minIndent)
        {
            while (_currentLine < _lines.Length)
            {
                string rawLine = _lines[_currentLine];
                if (string.IsNullOrWhiteSpace(rawLine)) { _currentLine++; continue; }

                int indent = GetIndent(rawLine);
                if (indent < minIndent) return;

                string line = rawLine.Trim();
                if (line.StartsWith("#") || string.IsNullOrEmpty(line)) { _currentLine++; continue; }

                int hashIdx = line.IndexOf('#');
                if (hashIdx >= 0) line = line.Substring(0, hashIdx).Trim();
                if (string.IsNullOrEmpty(line)) { _currentLine++; continue; }

                if (line.StartsWith("if ") && line.EndsWith(":"))
                {
                    string conditionExpr = line.Substring(3, line.Length - 4).Trim();
                    bool condition = EvaluateExpression(conditionExpr) != 0;
                    _currentLine++;

                    if (condition)
                    {
                        ExecuteBlock(indent + 1);
                        SkipOptionalElse(indent);
                    }
                    else
                    {
                        SkipBlock(indent + 1);
                        ExecuteOptionalElse(indent);
                    }
                    continue;
                }
                
                if (line.StartsWith("else:"))
                {
                    _currentLine++;
                    SkipBlock(indent + 1);
                    continue;
                }

                ExecuteStatement(line);
                _currentLine++;
            }
        }

        private void SkipOptionalElse(int indent)
        {
            int savedLine = _currentLine;
            while (_currentLine < _lines.Length)
            {
                string rawLine = _lines[_currentLine];
                if (string.IsNullOrWhiteSpace(rawLine) || rawLine.Trim().StartsWith("#")) { _currentLine++; continue; }
                
                if (GetIndent(rawLine) == indent && rawLine.Trim().StartsWith("else:"))
                {
                    _currentLine++;
                    SkipBlock(indent + 1);
                    return;
                }
                break;
            }
            _currentLine = savedLine; 
        }

        private void ExecuteOptionalElse(int indent)
        {
            while (_currentLine < _lines.Length)
            {
                string rawLine = _lines[_currentLine];
                if (string.IsNullOrWhiteSpace(rawLine) || rawLine.Trim().StartsWith("#")) { _currentLine++; continue; }
                
                if (GetIndent(rawLine) == indent && rawLine.Trim().StartsWith("else:"))
                {
                    _currentLine++;
                    ExecuteBlock(indent + 1);
                    return;
                }
                break;
            }
        }

        private void SkipBlock(int minIndent)
        {
            while (_currentLine < _lines.Length)
            {
                string rawLine = _lines[_currentLine];
                if (string.IsNullOrWhiteSpace(rawLine)) { _currentLine++; continue; }
                int indent = GetIndent(rawLine);
                if (indent < minIndent) return;
                _currentLine++;
            }
        }

        private int GetIndent(string line)
        {
            int count = 0;
            foreach (char c in line)
            {
                if (c == ' ') count++;
                else if (c == '\t') count += 4;
                else break;
            }
            return count;
        }

        private void ExecuteStatement(string line)
        {
            if (line.Contains("="))
            {
                var parts = line.Split('=', 2);
                string varName = parts[0].Trim();
                long val = EvaluateExpression(parts[1].Trim());
                _variables[varName] = val;
                return;
            }

            EvaluateExpression(line);
        }

        private long EvaluateExpression(string expr)
        {
            expr = expr.Trim();
            if (long.TryParse(expr, out long res)) return res;
            if (expr.StartsWith("0x") && long.TryParse(expr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out res)) return res;

            if (expr.Contains("(") && expr.EndsWith(")"))
            {
                int openParen = expr.IndexOf('(');
                string funcName = expr.Substring(0, openParen).Trim().ToLower();
                string argsStr = expr.Substring(openParen + 1, expr.Length - openParen - 2);
                var args = SplitArgs(argsStr);

                return CallFunction(funcName, args);
            }

            if (_variables.TryGetValue(expr, out long varVal)) return varVal;

            if (expr.Contains("+") || expr.Contains("-") || expr.Contains(">") || expr.Contains("<") || expr.Contains("==") || expr.Contains("!="))
            {
                return _parent.EvaluateMathExpression(expr, _variables);
            }

            return 0;
        }

        private List<string> SplitArgs(string argsStr)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(argsStr)) return result;

            int parenDepth = 0;
            bool inQuotes = false;
            int start = 0;
            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];
                if (c == '\"') inQuotes = !inQuotes;
                if (inQuotes) continue;

                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                else if (c == ',' && parenDepth == 0)
                {
                    result.Add(argsStr.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            result.Add(argsStr.Substring(start).Trim());
            return result;
        }

        private long CallFunction(string name, List<string> args)
        {
            switch (name)
            {
                case "find":
                    if (args.Count < 1) return -1;
                    string pattern = args[0].Trim('\"');
                    long found = _parent.FindSignature(pattern);
                    if (found != -1) _parent.SafeLogThreadSafe($"[Search] Signature found: 0x{found:X8}", Brushes.Violet);
                    else _parent.SafeLogThreadSafe($"[Search] Signature NOT found: {pattern}", Brushes.Orange);
                    return found;

                case "offset":
                    if (args.Count < 2) return 0;
                    return EvaluateExpression(args[0]) + EvaluateExpression(args[1]);

                case "write":
                    if (args.Count < 2) return 0;
                    long writeAddr = EvaluateExpression(args[0]);
                    byte[] writeBytes = MainWindow.ParseBytes(args[1].Trim('\"'));
                    Patch(writeAddr, writeBytes);
                    return 1;

                case "nop":
                    if (args.Count < 2) return 0;
                    long nopAddr = EvaluateExpression(args[0]);
                    int nopSize = (int)EvaluateExpression(args[1]);
                    byte[] nops = new byte[nopSize];
                    for (int i = 0; i < nopSize; i++) nops[i] = 0x90;
                    Patch(nopAddr, nops);
                    return 1;

                case "fill":
                    if (args.Count < 3) return 0;
                    long fillAddr = EvaluateExpression(args[0]);
                    int fillSize = (int)EvaluateExpression(args[1]);
                    byte fillByte = (byte)EvaluateExpression(args[2]);
                    byte[] fillBuf = new byte[fillSize];
                    for (int i = 0; i < fillSize; i++) fillBuf[i] = fillByte;
                    Patch(fillAddr, fillBuf);
                    return 1;

                case "write_string":
                    if (args.Count < 2) return 0;
                    long strAddr = EvaluateExpression(args[0]);
                    string text = args[1].Trim('\"');
                    string encName = args.Count > 2 ? args[2].Trim('\"') : "utf8";
                    var encoding = encName.ToLower() == "utf16" ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8;
                    Patch(strAddr, encoding.GetBytes(text));
                    return 1;

                case "assemble":
                    if (args.Count < 2) return 0;
                    long asmAddr = EvaluateExpression(args[0]);
                    string asmCode = args[1].Trim('\"');
                    byte[]? asmBytes = AsmLogic.Assemble(asmCode, asmAddr);
                    if (asmBytes != null) Patch(asmAddr, asmBytes);
                    else _parent.SafeLogThreadSafe($"[Error] Failed to assemble: {asmCode}", Brushes.Red);
                    return 1;

                case "make_jmp":
                    if (args.Count < 2) return 0;
                    long from = EvaluateExpression(args[0]);
                    long to = EvaluateExpression(args[1]);
                    int rel = (int)(to - (from + 5));
                    byte[] jmp = new byte[5];
                    jmp[0] = 0xE9;
                    jmp[1] = (byte)rel;
                    jmp[2] = (byte)(rel >> 8);
                    jmp[3] = (byte)(rel >> 16);
                    jmp[4] = (byte)(rel >> 24);
                    Patch(from, jmp);
                    return 1;

                case "read_byte":
                    if (args.Count < 1) return 0;
                    return _parent.HexView.ReadByte(EvaluateExpression(args[0]));

                case "read_dword":
                    if (args.Count < 1) return 0;
                    long dwAddr = EvaluateExpression(args[0]);
                    byte[] dwBuf = new byte[4];
                    for (int i = 0; i < 4; i++) dwBuf[i] = _parent.HexView.ReadByte(dwAddr + i);
                    if (_parent.IsLittleEndian) return BitConverter.ToUInt32(dwBuf, 0);
                    else return (uint)((dwBuf[0] << 24) | (dwBuf[1] << 16) | (dwBuf[2] << 8) | dwBuf[3]);

                case "check_bytes":
                    if (args.Count < 2) return 0;
                    long chkAddr = EvaluateExpression(args[0]);
                    byte[] chkExpected = MainWindow.ParseBytes(args[1].Trim('\"'));
                    for (int i = 0; i < chkExpected.Length; i++)
                        if (_parent.HexView.ReadByte(chkAddr + i) != chkExpected[i]) return 0;
                    return 1;

                case "label":
                    if (args.Count < 2) return 0;
                    string labelName = args[0].Trim('\"');
                    long labelAddr = EvaluateExpression(args[1]);
                    _variables[labelName] = labelAddr;
                    return labelAddr;

                case "log":
                    if (args.Count == 0) return 0;
                    string logInput = args[0];
                    string logResult = "";
                    
                    int currentPos = 0;
                    while (currentPos < logInput.Length)
                    {
                        while (currentPos < logInput.Length && char.IsWhiteSpace(logInput[currentPos])) currentPos++;
                        if (currentPos >= logInput.Length) break;

                        if (logInput[currentPos] == '\"')
                        {
                            int endQuote = logInput.IndexOf('\"', currentPos + 1);
                            if (endQuote != -1)
                            {
                                logResult += logInput.Substring(currentPos + 1, endQuote - currentPos - 1);
                                currentPos = endQuote + 1;
                            }
                            else
                            {
                                logResult += logInput.Substring(currentPos + 1);
                                currentPos = logInput.Length;
                            }
                        }
                        else
                        {
                            int nextPlus = -1;
                            bool insideInnerQuotes = false;
                            for (int i = currentPos; i < logInput.Length; i++)
                            {
                                if (logInput[i] == '\"') insideInnerQuotes = !insideInnerQuotes;
                                if (!insideInnerQuotes && logInput[i] == '+')
                                {
                                    nextPlus = i;
                                    break;
                                }
                            }

                            string token = (nextPlus == -1) ? logInput.Substring(currentPos) : logInput.Substring(currentPos, nextPlus - currentPos);
                            logResult += EvaluateExpression(token.Trim()).ToString("X");
                            currentPos = (nextPlus == -1) ? logInput.Length : nextPlus;
                        }

                        while (currentPos < logInput.Length && char.IsWhiteSpace(logInput[currentPos])) currentPos++;
                        if (currentPos < logInput.Length && logInput[currentPos] == '+') currentPos++;
                    }

                    _parent.SafeLogThreadSafe($"[Script] {logResult}", Brushes.White);
                    return 1;

                default:
                    return 0;
            }
        }

        private void Patch(long addr, byte[] bytes)
        {
            if (addr < 0 || addr + bytes.Length > _parent.HexView.FileLength) return;

            byte[] oldBytes = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                oldBytes[i] = _parent.HexView.ReadByte(addr + i);

            _parent.SafeLogThreadSafe($"[Patch] 0x{addr:X8}: {BitConverter.ToString(oldBytes).Replace("-", " ")} -> {BitConverter.ToString(bytes).Replace("-", " ")}", Brushes.YellowGreen);

            lock (_parent._undoStack)
            {
                _parent._undoStack.Push((addr, oldBytes, bytes));
                ChangesCount++;
            }

            _parent.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < bytes.Length; i++)
                    _parent.HexView.WriteByte(addr + i, bytes[i]);
                _parent.HexView.InvalidateVisual();
            });
        }
    }

    private long EvaluateMathExpression(string expr, Dictionary<string, long> variables)
    {
        try
        {
            string op = "";
            if (expr.Contains("==")) op = "==";
            else if (expr.Contains("!=")) op = "!=";
            else if (expr.Contains(">=")) op = ">=";
            else if (expr.Contains("<=")) op = "<=";
            else if (expr.Contains(">")) op = ">";
            else if (expr.Contains("<")) op = "<";
            else if (expr.Contains("+")) op = "+";
            else if (expr.Contains("-")) op = "-";

            if (string.IsNullOrEmpty(op)) return 0;

            var parts = expr.Split(new[] { op }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return 0;

            long left = ParseValue(parts[0].Trim(), variables);
            long right = ParseValue(parts[1].Trim(), variables);

            switch (op)
            {
                case "+": return left + right;
                case "-": return left - right;
                case ">": return left > right ? 1 : 0;
                case "<": return left < right ? 1 : 0;
                case ">=": return left >= right ? 1 : 0;
                case "<=": return left <= right ? 1 : 0;
                case "==": return left == right ? 1 : 0;
                case "!=": return left != right ? 1 : 0;
                default: return 0;
            }
        }
        catch { return 0; }
    }

    private long ParseValue(string p, Dictionary<string, long> variables)
    {
        if (variables.TryGetValue(p, out long v)) return v;
        if (p.StartsWith("0x")) return long.Parse(p.Substring(2), System.Globalization.NumberStyles.HexNumber);
        if (long.TryParse(p, out long l)) return l;
        return 0;
    }
    private static byte[] ParseBytes(string s) => ParseBytes(s.AsSpan());
    private static byte[] ParseBytes(ReadOnlySpan<char> input)
    {
        int count = 0;
        for (int i = 0; i + 1 < input.Length;)
        {
            if (IsHexChar(input[i]) && IsHexChar(input[i + 1])) { count++; i += 2; }
            else i++;
        }
        if (count == 0) return Array.Empty<byte>();

        byte[] result = new byte[count];
        int ri = 0;
        for (int i = 0; i + 1 < input.Length && ri < count;)
        {
            if (IsHexChar(input[i]) && IsHexChar(input[i + 1]))
            {
                result[ri++] = (byte)((HexNibble(input[i]) << 4) | HexNibble(input[i + 1]));
                i += 2;
            }
            else i++;
        }
        return result;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte FastHexByte(ReadOnlySpan<char> s)
        => (byte)((HexNibble(s[0]) << 4) | HexNibble(s[1]));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexNibble(char c)
        => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHexChar(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private string ExtractInsideBrackets(string input)
    {
        int s = input.IndexOf('('), e = input.LastIndexOf(')');
        return (s != -1 && e != -1 && e > s)
            ? input.Substring(s + 1, e - s - 1) : input;
    }

    private string GetHexPreview(long offset)
    {
        try { HexView.ReadBytes(offset, _inspectorBuf); return BitConverter.ToString(_inspectorBuf).Replace("-", " "); }
        catch { return "?? ?? ?? ??"; }
    }

    private static string IdentifyRegion(long offset, long totalSize)
    {
        double p = (double)offset / totalSize;
        if (offset < 0x400) return "PE Header (Metadata)";
        if (p > 0.95) return "EOF / Overlay Data";
        return $"Data Region ({p * 100:F1}%)";
    }

    private void StructureTree_StructureSelected(object sender, BinaryStructure structure)
    {
        PropertyGrid.SelectedStructure = structure;
        if (structure.Offset.HasValue)
        {
            HexView.SelectedOffset = structure.Offset.Value;
            LogMessage($"Navigated to: {structure.Name} at 0x{structure.Offset:X8}");
        }
    }

    private void BtnEndian_Click(object sender, RoutedEventArgs e)
    {
        IsLittleEndian = !IsLittleEndian;
        if (sender is MenuItem mi) mi.Header = IsLittleEndian ? "Endian: LE" : "Endian: BE";
        HexView_OffsetSelected(HexView, HexView.SelectedOffset);
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        string input = SearchInput.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;
        try
        {
            string hexClean = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? input[2..] : input;
            long targetOffset = long.Parse(hexClean, System.Globalization.NumberStyles.HexNumber);
            if (targetOffset < 0 || targetOffset >= HexView.FileLength)
            {
                ConsoleLog.AppendText($"\n[Error] Offset 0x{targetOffset:X} out of bounds.");
                return;
            }
            HexView.SelectedOffset = targetOffset;
            HexView.ScrollToOffset(targetOffset);
            string region = IdentifyRegion(targetOffset, HexView.FileLength);
            SearchResultsGrid.Items.Clear();
            SearchResultsGrid.Items.Add(new SearchResult(
                $"0x{targetOffset:X8}", "16 bytes", GetHexPreview(targetOffset), region));
            ConsoleLog.AppendText($"\n[Jump] Moved to {region} at 0x{targetOffset:X8}");
        }
        catch (FormatException) { ConsoleLog.AppendText($"\n[Search Error] '{input}' is not a valid HEX string."); }
        catch (Exception ex) { ConsoleLog.AppendText($"\n[Error] {ex.Message}"); }
    }

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSearch_Click(sender, e);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var action = HotkeyManager.GetAction(Keyboard.Modifiers, key);

        if (action == EUVAAction.Undo) { PerformUndo(); e.Handled = true; return; }
        if (action == EUVAAction.FullUndo) { PerformFullUndo(); e.Handled = true; return; }

        if (key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MenuDisassembler_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MenuDecompiler_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (action >= EUVAAction.NavInspector && action <= EUVAAction.NavProperties)
        {
            int index = (int)action - (int)EUVAAction.NavInspector;
            if (index < RightTabControl.Items.Count)
            {
                RightTabControl.SelectedIndex = index;
                if (index == 1)
                    Dispatcher.BeginInvoke(new Action(() => SearchInput.Focus()),
                        System.Windows.Threading.DispatcherPriority.Input);
                ConsoleLog.AppendText($"\n[UI] Jump to {((TabItem)RightTabControl.SelectedItem).Header}");
                e.Handled = true;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F5 && !string.IsNullOrEmpty(_activeScriptPath))
        {
            Log("[Manual] F5 Pressed. Forcing engine...", Brushes.DeepSkyBlue);
            _ = RunParallelEngine(_activeScriptPath);
        }
    }

    private void MenuThemeSelect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "EUVA Theme Files (*.themes)|*.themes|All Files (*.*)|*.*",
            Title = "Select Theme File"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ThemeManager.Instance.LoadTheme(dialog.FileName);
            UpdateGlobalConfig(themePath: dialog.FileName);
            LogMessage($"[THEME ENGINE] Theme applied: {Path.GetFileName(dialog.FileName)}");
            HexView.RefreshBrushCache();
            HexView.InvalidateVisual();
        }
        catch (Exception ex)
        {
            LogMessage($"[ERROR] Theme load failed: {ex.Message}");
            MessageBox.Show($"Error loading theme: {ex.Message}", "Theme Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void _ApplyThemeFile(string path, bool save)
    {
        try
        {
            ThemeManager.Instance.LoadTheme(path);
            if (save) ThemeManager.Instance.SaveThemePath(path);
            LogMessage($"[THEME ENGINE] Loaded theme: {Path.GetFileName(path)}");
            HexView.RefreshBrushCache();
            HexView.InvalidateVisual();
        }
        catch (Exception ex)
        {
            LogMessage($"[ERROR] Failed to load theme: {ex.Message}");
            MessageBox.Show($"Failed to load theme file:\n{ex.Message}", "Theme Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChangeEncoding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag != null)
            if (int.TryParse(mi.Tag.ToString(), out int cp))
                HexView.ChangeEncoding(cp);
    }

    private void MenuHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "EUVA Hotkeys Files (*.htk)|*.htk|All Files (*.*)|*.*",
            Title = "Select Hotkeys File"
        };
        if (dialog.ShowDialog() != true) return;
        HotkeyManager.Load(dialog.FileName);
        UpdateGlobalConfig(htkPath: dialog.FileName);
        ConsoleLog.AppendText($"\n[System] Hotkeys updated from: {Path.GetFileName(dialog.FileName)}");
        ConsoleLog.ScrollToEnd();
    }

    private void OnMediaHexClick(object sender, RoutedEventArgs e)
    {
        if (MagicModeMenuItem.IsChecked)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            { Filter = "Raw Video|*.raw;*.bin|All Files|*.*" };
            if (dialog.ShowDialog() == true) StartRawVideo(dialog.FileName);
            else MagicModeMenuItem.IsChecked = false;
        }
        else StopRawVideo();
    }

    private void StartRawVideo(string path)
    {
        _videoTotalSize = _videoWidth * _videoHeight;
        _frameBuffer = new byte[_videoTotalSize];
        _rawVideoStream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 8192, FileOptions.SequentialScan);
        HexView.IsMediaMode = true;
        CompositionTarget.Rendering += RenderLoop;
        LogMessage("[MediaHex] MediaHex Engine Started.");
    }

    private void RenderLoop(object? sender, EventArgs e)
    {
        if (_rawVideoStream == null || !HexView.IsMediaMode || _frameBuffer == null) return;
        int read = _rawVideoStream.Read(_frameBuffer, 0, _videoTotalSize);
        if (read < _videoTotalSize) { _rawVideoStream.Position = 0; return; }
        HexView.SetMediaFrame(_frameBuffer);
    }

    private void StopRawVideo()
    {
        CompositionTarget.Rendering -= RenderLoop;
        HexView.IsMediaMode = false;
        _rawVideoStream?.Dispose();
        _rawVideoStream = null;
        _frameBuffer = null;
        HexView.InvalidateVisual();
        LogMessage("[MediaHex] Stopped.");
    }

    private void MenuWatchScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Euva Scripts (*.euv)|*.euv|All Files (*.*)|*.*",
            Title = "Select Script to Watch"
        };
        if (dialog.ShowDialog() != true) return;
        _lastScriptPath = dialog.FileName;
        StartScriptWatcher(dialog.FileName);
        if (sender is MenuItem mi) mi.Header = $"Watching: {Path.GetFileName(dialog.FileName)}";
        Log($"[UI] Target script set to: {Path.GetFileName(dialog.FileName)}", Brushes.Cyan);
    }

    private void StartScriptWatcher(string path)
    {
        _scriptWatcher?.Dispose();
        _activeScriptPath = path;
        _scriptWatcher = new FileSystemWatcher(Path.GetDirectoryName(path)!)
        {
            Filter = Path.GetFileName(path),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                                  NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _scriptWatcher.Changed += OnScriptUpdated;
        _scriptWatcher.Created += OnScriptUpdated;
        _scriptWatcher.Renamed += OnScriptUpdated;
    }

    private async void OnScriptUpdated(object sender, FileSystemEventArgs e)
    {
        if (_isProcessingScript) return;
        _isProcessingScript = true;
        await Task.Delay(400);
        await Dispatcher.InvokeAsync(async () =>
        {
            Log($"[Debug] Script change detected...", Brushes.Yellow);
            try { await RunParallelEngine(e.FullPath); }
            catch (Exception ex) { Log($"[Error] {ex.Message}", Brushes.Red); }
            finally { _isProcessingScript = false; }
        });
    }

    public void ToggleRightPanel(bool visible)
    {
        if (MainEditorGrid == null || MainEditorGrid.ColumnDefinitions.Count < 5) return;

        if (visible)
        {
            MainEditorGrid.ColumnDefinitions[3].Width = new GridLength(1);
            MainEditorGrid.ColumnDefinitions[4].Width = new GridLength(310);
            MainEditorGrid.ColumnDefinitions[4].MinWidth = 160;
            
            foreach (UIElement child in MainEditorGrid.Children)
            {
                int col = Grid.GetColumn(child);
                if (col == 3 || col == 4) child.Visibility = Visibility.Visible;
            }
        }
        else
        {
            MainEditorGrid.ColumnDefinitions[3].Width = new GridLength(0);
            MainEditorGrid.ColumnDefinitions[4].Width = new GridLength(0);
            MainEditorGrid.ColumnDefinitions[4].MinWidth = 0;
            
            foreach (UIElement child in MainEditorGrid.Children)
            {
                int col = Grid.GetColumn(child);
                if (col == 3 || col == 4) child.Visibility = Visibility.Collapsed;
            }
        }
    }
}