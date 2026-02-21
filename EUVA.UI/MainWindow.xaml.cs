// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.IO;
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
using System.Windows.Documents;
using System.Data;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;

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
            catch (Exception ex)
            {
                MainWindow.Instance?.Log($"[ERROR] .htk: {ex.Message}", Brushes.Red);
            }
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

public class SearchResult
{
    public string Offset { get; set; }
    public string Size { get; set; }
    public string Value { get; set; }
    public string Context { get; set; }
}

public class InspectorItem
{
    public string Name { get; set; }
    public string Value { get; set; }
    public string RawHex { get; set; }
}

public class MethodContainer
{
    public string Name;
    public string Access;
    public List<string> Body = new();
    public Dictionary<string, long> Clinks = new();
}
public static class AsmLogic
{
    private static readonly Dictionary<string, byte> Regs = new()
    {
        {"eax",0},{"ecx",1},{"edx",2},{"ebx",3},
        {"esp",4},{"ebp",5},{"esi",6},{"edi",7}
    };
    private static readonly Dictionary<string, byte> Ops = new()
    {
        {"add",0x01},{"or",0x09},{"and",0x21},
        {"sub",0x29},{"xor",0x31},{"cmp",0x39},
        {"jmp",0xE9},{"mov_eax",0xB8}
    };

    public static byte[]? Assemble(string part, long currentAddr)
    {
        var tokens = part.ToLower().Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        string mnemonic = tokens[0];
        if (mnemonic == "nop") return new byte[] { 0x90 };
        if (mnemonic == "ret") return new byte[] { 0xC3 };

        if (mnemonic == "jmp" && tokens.Length == 2)
        {
            if (long.TryParse(tokens[1], out long targetAddr))
            {
                int rel = (int)(targetAddr - (currentAddr + 5));
                var result = new byte[5];
                result[0] = 0xE9;
                Buffer.BlockCopy(BitConverter.GetBytes(rel), 0, result, 1, 4);
                return result;
            }
        }

        if (Ops.ContainsKey(mnemonic) && tokens.Length == 3)
        {
            if (Regs.TryGetValue(tokens[1], out byte dest) && Regs.TryGetValue(tokens[2], out byte src))
                return new byte[] { Ops[mnemonic], (byte)(0xC0 + (src << 3) + dest) };
        }

        if (mnemonic == "mov" && tokens.Length == 3)
        {
            if (Regs.TryGetValue(tokens[1], out byte regIdx) && int.TryParse(tokens[2], out int val))
            {
                var result = new byte[5];
                result[0] = (byte)(0xB8 + regIdx);
                Buffer.BlockCopy(BitConverter.GetBytes(val), 0, result, 1, 4);
                return result;
            }
        }

        return null;
    }
}
public static class DataParser
{
    public static (long value, int size) ReadLEB128(byte[] data, bool signed)
    {
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

    public static string ToDosDate(ushort v) => $"{((v >> 9) & 0x7F) + 1980:D4}-{(v >> 5) & 0x0F:D2}-{v & 0x1F:D2}";
    public static string ToDosTime(ushort v) => $"{v >> 11:D2}:{(v >> 5) & 0x3F:D2}:{(v & 0x1F) * 2:D2}";
}
public static class InspectorHelper
{
    public static (long value, int length) ReadULEB128(byte[] data)
    {
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
    public static MainWindow Instance { get; private set; }
    private readonly System.Text.StringBuilder _logBuffer = new(4096);
    private readonly object _logLock = new();
    private readonly System.Windows.Threading.DispatcherTimer _logFlushTimer;
    [ThreadStatic] private static DataTable? _threadDataTable;
    private static DataTable GetDataTable() => _threadDataTable ??= new DataTable();
    private static readonly Regex _whitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex _hexLiteralRegex = new(@"0x([0-9A-Fa-f]+)", RegexOptions.Compiled);
    private static readonly Regex _identifierRegex = new(@"[a-zA-Z_]", RegexOptions.Compiled);
    private static readonly Regex _clinkBracketRegex = new(@"\[(.*?)\]", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex _hexByteRegex = new(@"[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private readonly byte[] _inspectorBuf = new byte[16];
    private readonly Stack<(long Offset, byte[] Old, byte[] New)> _undoStack = new();
    private readonly Stack<int> _transactionSteps = new();
    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    private FileStream? _rawVideoStream;
    private byte[]? _frameBuffer;
    private readonly int _videoWidth = 24;
    private readonly int _videoHeight = 26;
    private int _videoTotalSize;
    private string? _activeScriptPath;
    private FileSystemWatcher? _scriptWatcher;
    private bool _isProcessingScript = false;
    private string? _lastScriptPath;
    private bool IsLittleEndian = true;

    private readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkey.cfg");
    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _logFlushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _logFlushTimer.Tick += FlushLogBuffer;
        _logFlushTimer.Start();

        InitializeSystemSettings();
        InitializeDetectors();
    }
    
    public void Log(string message, Brush color)
    {
    
        if (color.CanFreeze) color.Freeze(); 

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timestamp}] {message}\n";

        lock (_logLock)
            _logBuffer.Append(line);
    }
    private void SafeLog(string msg, Brush color) => Log(msg, color);

    private void LogMessage(string message) => Log(message, Brushes.White);

    private void FlushLogBuffer(object? sender, EventArgs e)
    {
        string? pending;
        lock (_logLock)
        {
            if (_logBuffer.Length == 0) return;
            pending = _logBuffer.ToString();
            _logBuffer.Clear();
        }

        ConsoleLog.AppendText(pending);
        ConsoleLog.ScrollToEnd();
    }
    private void InitializeSystemSettings()
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

        HexView.InvalidateVisual();
    }

    private void InitializeDetectors()
    {
        _detectorManager = new DetectorManager();
        _detectorManager.RegisterDetector(new UPXDetector());
        _detectorManager.RegisterDetector(new ThemidaDetector());
        LogMessage($"Loaded {_detectorManager.Detectors.Count} detectors");
    }

    private void UpdateGlobalConfig(string? htkPath = null, string? themePath = null)
    {
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "euva.cfg");
        string defaultThemeBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Theming", "default.themes");

        string currentHtk = "", currentTheme = defaultThemeBase, alwaysDefault = defaultThemeBase;

        if (File.Exists(configPath))
        {
            var lines = File.ReadAllLines(configPath);
            if (lines.Length > 0) currentHtk = lines[0];
            if (lines.Length > 1) currentTheme = lines[1];
            if (lines.Length > 2) alwaysDefault = lines[2];
        }

        File.WriteAllLines(configPath, new[]
        {
            htkPath ?? currentHtk,
            themePath ?? currentTheme,
            alwaysDefault
        });
    }
    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenFile();
    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable Files (*.exe;*.dll)|*.exe;*.dll|All Files (*.*)|*.*",
            Title = "Select PE File"
        };
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
            StatusText.Text = $"Loaded: {Path.GetFileName(filePath)} ({fileSize:N0} bytes)";
            LogMessage("File mapped successfully (MMF mode)");
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void HexView_OffsetSelected(object sender, long offset)
    {
        PropertyGrid.SelectedOffset = offset;
        long total = HexView.FileLength;
        if (total == 0) return;

        long remaining = total - offset;
        int toRead = (int)Math.Min(16, remaining);
        if (toRead > 0)
            HexView.ReadBytes(offset, _inspectorBuf);

        var items = new List<InspectorItem>(16);

        try
        {
            if (remaining >= 1)
            {
                byte b = _inspectorBuf[0];
                items.Add(new InspectorItem { Name = "Int8 / UInt8",       Value = $"{(sbyte)b} | {b}",     RawHex = $"{b:X2}" });
                items.Add(new InspectorItem { Name = "Двоичный (8 бит)",   Value = Convert.ToString(b, 2).PadLeft(8, '0'), RawHex = "-" });
            }
            if (remaining >= 2)
            {
                ushort v = IsLittleEndian
                    ? (ushort)(_inspectorBuf[0] | (_inspectorBuf[1] << 8))
                    : (ushort)((_inspectorBuf[0] << 8) | _inspectorBuf[1]);
                items.Add(new InspectorItem { Name = "Int16 / UInt16",     Value = $"{(short)v} | {v}",     RawHex = $"{_inspectorBuf[0]:X2}-{_inspectorBuf[1]:X2}" });
                items.Add(new InspectorItem { Name = "Дата / Время DOS",   Value = $"{DataParser.ToDosDate(v)} {DataParser.ToDosTime(v)}", RawHex = "MS-DOS" });
            }
            if (remaining >= 3)
            {
                int v = IsLittleEndian
                    ? _inspectorBuf[0] | (_inspectorBuf[1] << 8) | (_inspectorBuf[2] << 16)
                    : (_inspectorBuf[0] << 16) | (_inspectorBuf[1] << 8) | _inspectorBuf[2];
                items.Add(new InspectorItem { Name = "Int24 / UInt24", Value = v.ToString(), RawHex = $"{_inspectorBuf[0]:X2}-{_inspectorBuf[1]:X2}-{_inspectorBuf[2]:X2}" });
            }
            if (remaining >= 4)
            {
                var span4 = new ReadOnlySpan<byte>(_inspectorBuf, 0, 4);
                uint uv = IsLittleEndian
                    ? MemoryMarshal.Read<uint>(span4)
                    : BinaryPrimitives.ReadUInt32BigEndian(span4);
                float fv = MemoryMarshal.Read<float>(span4);
                items.Add(new InspectorItem { Name = "Int32 / UInt32",     Value = $"{(int)uv} | {uv}",  RawHex = BitConverter.ToString(_inspectorBuf, 0, 4) });
                items.Add(new InspectorItem { Name = "Single (float32)",   Value = fv.ToString("G6"),     RawHex = "-" });
                items.Add(new InspectorItem { Name = "time_t (32 бит)",    Value = DateTimeOffset.FromUnixTimeSeconds(uv).DateTime.ToString(), RawHex = "Unix" });
            }
            if (remaining >= 8)
            {
                var span8 = new ReadOnlySpan<byte>(_inspectorBuf, 0, 8);
                ulong uv = IsLittleEndian
                    ? MemoryMarshal.Read<ulong>(span8)
                    : BinaryPrimitives.ReadUInt64BigEndian(span8);
                double dv = MemoryMarshal.Read<double>(span8);
                items.Add(new InspectorItem { Name = "Int64 / UInt64",  Value = uv.ToString(),               RawHex = BitConverter.ToString(_inspectorBuf, 0, 8) });
                items.Add(new InspectorItem { Name = "Double (float64)", Value = dv.ToString("G8"),           RawHex = "-" });
                items.Add(new InspectorItem { Name = "FILETIME",         Value = DateTime.FromFileTime((long)uv).ToString(), RawHex = "Win32" });
                items.Add(new InspectorItem { Name = "OLETIME",          Value = DateTime.FromOADate(dv).ToString(),        RawHex = "OLE" });
            }
            if (remaining >= 16)
            {
                items.Add(new InspectorItem { Name = "GUID / UUID",
                    Value = new Guid(_inspectorBuf).ToString("B").ToUpper(), RawHex = "System" });
            }
            int lebLen = (int)Math.Min(10, remaining);
            if (lebLen > 0)
            {
                var uleb = DataParser.ReadLEB128(_inspectorBuf[..lebLen], false);
                items.Add(new InspectorItem { Name = "ULEB128", Value = uleb.value.ToString(), RawHex = $"Size: {uleb.size}" });
            }
        }
        catch { }

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
            MessageBox.Show("Please load a PE file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogMessage("Running detection analysis...");
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            int analysisSize = (int)Math.Min(HexView.FileLength, 10 * 1024 * 1024);
            byte[] analysisData = new byte[analysisSize];
            HexView.ReadBytes(0, analysisData);

            var progress = new Progress<string>(msg => LogMessage(msg));
            var results = await _detectorManager.AnalyzeAsync(
                new ReadOnlyMemory<byte>(analysisData), _mapper.RootStructure, progress);

            DetectionList.ItemsSource = results;
            LogMessage($"Analysis complete. Found {results.Count} matches.");
            if (results.Count > 0) LogMessage($"Best match: {_detectorManager.GetBestMatch(results)}");
        }
        catch (Exception ex) { LogMessage($"ERROR: {ex.Message}"); }
        finally { Mouse.OverrideCursor = null; }
    }

    private void MenuCalculateEntropy_Click(object sender, RoutedEventArgs e)
    {
        if (_mapper == null || HexView.FileLength == 0)
        {
            MessageBox.Show("Please load a PE file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LogMessage("Calculating entropy (Top 10MB)...");
        int sampleSize = (int)Math.Min(HexView.FileLength, 10 * 1024 * 1024);
        byte[] sampleData = new byte[sampleSize];
        HexView.ReadBytes(0, sampleData);
        var regions = _mapper.GetRegions();
        var entropyResults = SignatureScanner.AnalyzeSectionEntropy(sampleData.AsSpan(), regions);
        foreach (var result in entropyResults) LogMessage($"  {result.Key}: {result.Value:F3} bits");
        LogMessage($"Overall entropy (based on sample): {SignatureScanner.CalculateEntropy(sampleData.AsSpan()):F3} bits");
    }
    private long FindSignature(string pattern)
    {
        var parts = pattern.Split(' ');
        var mask = new bool[parts.Length];  
        var pat  = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "??") { mask[i] = true; }
            else { pat[i] = Convert.ToByte(parts[i], 16); }
        }

        int patLen = pat.Length;
        long fileLen = HexView.FileLength;
        const int ChunkSize = 64 * 1024;
        byte[] chunk = new byte[ChunkSize + patLen]; 

        long searchEnd = fileLen - patLen;
        long pos = 0;

        while (pos <= searchEnd)
        {
            int toRead = (int)Math.Min(ChunkSize, fileLen - pos);
            int totalRead = (int)Math.Min(toRead + patLen - 1, fileLen - pos);
            HexView.ReadBytes(pos, chunk.AsSpan(0, totalRead));

            int chunkSearch = Math.Min(toRead, totalRead - patLen + 1);
            for (int i = 0; i < chunkSearch; i++)
            {
                bool match = true;
                for (int j = 0; j < patLen; j++)
                {
                    if (!mask[j] && chunk[i + j] != pat[j]) { match = false; break; }
                }
                if (match) return pos + i;
            }
            pos += toRead;
        }
        return -1;
    }
    private async Task RunParallelEngine(string scriptPath)
    {
        if (HexView.FileLength == 0) { Log("[Engine] FATAL: No file loaded!", Brushes.Red); return; }

        SafeLog($"[Engine] Starting script: {Path.GetFileName(scriptPath)}", Brushes.White);
        int stepsInThisRun = 0;
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(scriptPath); }
        catch (Exception ex) { Log($"[Engine] IO Error: {ex.Message}", Brushes.Red); return; }

        int totalChanges = 0;
        var globalScope = new Dictionary<string, long>();

        await Task.Run(() =>
        {
            try
            {
                long lastAddress = 0;
                string currentModifier = "default";
                MethodContainer? currentMethod = null;
                bool inScriptBody = false, isTerminated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = _whitespaceRegex.Replace(lines[i].Split('#')[0].Split("//")[0], " ").Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.ToLower() == "start;") { inScriptBody = true; continue; }
                    if (!inScriptBody) continue;
                    if (line.ToLower() == "end;") { isTerminated = true; break; }

                    if (line.EndsWith(":"))
                    {
                        var mod = line.Replace(":", "").ToLower();
                        if (mod == "public" || mod == "private") { currentModifier = mod; continue; }
                    }

                    if (line.StartsWith("_createMethod"))
                    {
                        var mName = Regex.Match(line, @"\((.*?)\)").Groups[1].Value;
                        currentMethod = new MethodContainer { Name = mName, Access = currentModifier };
                        SafeLog($"[Engine] Parsing method: {mName} ({currentModifier})", Brushes.Gray);
                        continue;
                    }

                    if (currentMethod != null)
                    {
                        if (line == "{") continue;
                        if (line == "}")
                        {
                            var localScope = new Dictionary<string, long>();
                            SafeLog($"[Engine] Executing method: {currentMethod.Name}", Brushes.CornflowerBlue);

                            foreach (var cmd in currentMethod.Body)
                                ExecuteCommand(cmd, localScope, globalScope, ref lastAddress, ref totalChanges, ref stepsInThisRun);

                            if (currentMethod.Access == "public")
                            {
                                foreach (var exportName in currentMethod.Clinks.Keys)
                                {
                                    if (localScope.TryGetValue(exportName, out long addr))
                                    {
                                        globalScope[$"{currentMethod.Name}.{exportName}"] = addr;
                                        SafeLog($"[Link] {currentMethod.Name}.{exportName} -> 0x{addr:X}", Brushes.Cyan);
                                    }
                                }
                            }
                            currentMethod = null;
                            continue;
                        }

                        if (line.ToLower().StartsWith("clink:") || line.Contains("["))
                        {
                            int j = i;
                            string fullClink = "";
                            while (j < lines.Length && !lines[j].Contains("]"))
                                fullClink += lines[j++];
                            if (j < lines.Length) fullClink += lines[j];
                            var match = _clinkBracketRegex.Match(fullClink);
                            if (match.Success)
                            {
                                var names = match.Groups[1].Value
                                    .Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim());
                                foreach (var name in names) currentMethod.Clinks[name] = 0;
                            }
                            i = j;
                            continue;
                        }
                        currentMethod.Body.Add(line);
                    }
                }

                if (stepsInThisRun > 0)
                {
                    lock (_undoStack) { _transactionSteps.Push(stepsInThisRun); }
                    SafeLog($"[Engine] Success. {stepsInThisRun} ops committed.", Brushes.SpringGreen);
                }
                if (!isTerminated) throw new Exception("Script reached EOF without 'end;'");
            }
            catch (Exception ex) { SafeLog($"[fatal error] {ex.Message}", Brushes.OrangeRed); }
        });
    }
   
    private void ExecuteCommand(string line,
    Dictionary<string, long> localScope,
    Dictionary<string, long> globalScope,
    ref long lastAddress, ref int totalChanges, ref int stepsInThisRun)
    {
        string cmd = line.ToLower();
        
        long currentFileLength = 0;
        Dispatcher.Invoke(() => currentFileLength = HexView.FileLength);

        try
        {
            if (cmd.StartsWith("find"))
            {
                var findParts = ExtractInsideBrackets(line).Split('=');
                if (findParts.Length < 2) return;
                string varName = findParts[0].Trim();
                long addr = FindSignature(findParts[1].Trim());
                localScope[varName] = addr;
                
                SafeLogThreadSafe($"[Search] {varName} set to 0x{addr:X}", 
                    addr == -1 ? Brushes.Orange : Brushes.Violet);
            }
            else if (cmd.StartsWith("set"))
            {
                var setParts = ExtractInsideBrackets(line).Split('=');
                if (setParts.Length < 2) return;
                localScope[setParts[0].Trim()] = ParseMath(setParts[1], lastAddress, localScope, globalScope);
            }
            else
            {
                string addrPart = line.Contains(':')
                    ? line.Split(':')[0]
                    : ExtractInsideBrackets(line).Split(':')[0];

                long addr = ParseMath(addrPart, lastAddress, localScope, globalScope);
                
                if (addr < 0 || addr >= currentFileLength)
                {
                    SafeLogThreadSafe($"[Skip] Address 0x{addr:X} out of range.", Brushes.Yellow);
                    return;
                }

                if (cmd.StartsWith("check"))
                {
                    byte[] expected = ParseBytes(line.Split(':')[1]);
                    for (int i = 0; i < expected.Length; i++)
                    {
                        if (HexView.ReadByte(addr + i) != expected[i])
                        {
                            SafeLogThreadSafe($"[Check Fail] 0x{addr:X} mismatch.", Brushes.OrangeRed);
                            return;
                        }
                    }
                    return;
                }

                if (line.Contains(':'))
                {
                    string dataPart = line.Split(':')[1].Trim();
                    byte[]? bytes = AsmLogic.Assemble(dataPart, addr);

                    if (bytes == null && dataPart.Contains('"'))
                    {
                        var match = Regex.Match(dataPart, "\"(.*)\"");
                        if (match.Success)
                            bytes = System.Text.Encoding.ASCII.GetBytes(match.Groups[1].Value);
                    }

                    if (bytes == null) bytes = ParseBytes(dataPart);

                    if (bytes != null && bytes.Length > 0)
                    {
                        byte[] oldBytes = new byte[bytes.Length];
                        for (int i = 0; i < bytes.Length; i++) oldBytes[i] = HexView.ReadByte(addr + i);

                        SafeLogThreadSafe($"[Patch] 0x{addr:X}: {BitConverter.ToString(oldBytes).Replace("-", " ")} -> {BitConverter.ToString(bytes).Replace("-", " ")}", Brushes.YellowGreen);

                        lock (_undoStack) { _undoStack.Push((addr, oldBytes, bytes)); stepsInThisRun++; }

                        Dispatcher.Invoke(() => {
                            for (int i = 0; i < bytes.Length; i++) HexView.WriteByte(addr + i, bytes[i]);
                        });

                        totalChanges += bytes.Length;
                        lastAddress = addr + bytes.Length;
     
                        Dispatcher.BeginInvoke(new Action(() => HexView.InvalidateVisual()), 
                            System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
        }
        catch (Exception ex) 
        { 
            SafeLogThreadSafe($"[Cmd Error] '{line}': {ex.Message}", Brushes.Red); 
        }
    }

    private void SafeLogThreadSafe(string msg, Brush color)
    {
        if (color.CanFreeze && !color.IsFrozen) color.Freeze();
        
        Log(msg, color);
    }

    private long ParseMath(string expr, long lastAddr,
        Dictionary<string, long> localScope,
        Dictionary<string, long> globalScope)
    {
        string formula = expr.Trim().Replace(" ", "");
        if (formula == "." || formula == "()") return lastAddr;
        int totalKeys = localScope.Count + globalScope.Count;
        var allKeys = new List<string>(totalKeys);
        foreach (var k in localScope.Keys) allKeys.Add(k);
        foreach (var k in globalScope.Keys) if (!localScope.ContainsKey(k)) allKeys.Add(k);
        allKeys.Sort((a, b) => b.Length.CompareTo(a.Length));

        foreach (var key in allKeys)
        {
            long val = localScope.TryGetValue(key, out long lv) ? lv : globalScope[key];
            string pattern = @"\b" + Regex.Escape(key) + @"\b";
            formula = Regex.Replace(formula, pattern, val.ToString("D"));
        }
        formula = _hexLiteralRegex.Replace(formula, m =>
            long.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber).ToString());

        try
        {
            if (_identifierRegex.IsMatch(formula)) return 0;
            return Convert.ToInt64(GetDataTable().Compute(formula, null));
        }
        catch { return 0; }
    }
    private static readonly Regex _parseBytesRegex = new(@"[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private byte[] ParseBytes(string byteStr)
    {
        var matches = _parseBytesRegex.Matches(byteStr);
        if (matches.Count == 0) return Array.Empty<byte>();
        var result = new byte[matches.Count];
        for (int i = 0; i < matches.Count; i++) result[i] = Convert.ToByte(matches[i].Value, 16);
        return result;
    }

    private string ExtractInsideBrackets(string input)
    {
        int start = input.IndexOf('(');
        int end = input.LastIndexOf(')');
        return (start != -1 && end != -1 && end > start)
            ? input.Substring(start + 1, end - start - 1)
            : input;
    }

    private string GetHexPreview(long offset)
    {
        try
        {
            HexView.ReadBytes(offset, _inspectorBuf);
            return BitConverter.ToString(_inspectorBuf).Replace("-", " ");
        }
        catch { return "?? ?? ?? ??"; }
    }

    private string IdentifyRegion(long offset, long totalSize)
    {
        double progress = (double)offset / totalSize;
        if (offset < 0x400) return "PE Header (Metadata)";
        if (progress > 0.95) return "EOF / Overlay Data";
        return $"Data Region ({progress * 100:F1}%)";
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
            SearchResultsGrid.Items.Add(new SearchResult
            {
                Offset = $"0x{targetOffset:X8}", Size = "16 bytes",
                Value = GetHexPreview(targetOffset), Context = region
            });
            ConsoleLog.AppendText($"\n[Jump] Moved to {region} at 0x{targetOffset:X8}");
        }
        catch (FormatException) { ConsoleLog.AppendText($"\n[Search Error] '{input}' is not a valid HEX string."); }
        catch (Exception ex)   { ConsoleLog.AppendText($"\n[Error] {ex.Message}"); }
    }

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSearch_Click(sender, e);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var action = HotkeyManager.GetAction(Keyboard.Modifiers, key);

        if (action == EUVAAction.Undo)        { PerformUndo();     e.Handled = true; return; }
        if (action == EUVAAction.FullUndo)    { PerformFullUndo(); e.Handled = true; return; }

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
        var dialog = new OpenFileDialog { Filter = "EUVA Theme Files (*.themes)|*.themes|All Files (*.*)|*.*", Title = "Select Theme File" };
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
            MessageBox.Show($"Error loading theme: {ex.Message}", "Theme Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show($"Failed to load theme file:\n{ex.Message}", "Theme Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChangeEncoding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag != null)
            if (int.TryParse(menuItem.Tag.ToString(), out int codePage))
                HexView.ChangeEncoding(codePage);
    }
    private void MenuHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "EUVA Hotkeys Files (*.htk)|*.htk|All Files (*.*)|*.*", Title = "Select Hotkeys File" };
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
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Raw Video|*.raw;*.bin|All Files|*.*" };
            if (dialog.ShowDialog() == true) StartRawVideo(dialog.FileName);
            else MagicModeMenuItem.IsChecked = false;
        }
        else StopRawVideo();
    }

    private void StartRawVideo(string path)
    {
        _videoTotalSize = _videoWidth * _videoHeight;
        _frameBuffer = new byte[_videoTotalSize];
        _rawVideoStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
        HexView.IsMediaMode = true;
        CompositionTarget.Rendering += RenderLoop;
        LogMessage("[MediaHex] MediaHex Engine Started.");
    }

    private void RenderLoop(object? sender, EventArgs e)
    {
        if (_rawVideoStream == null || !HexView.IsMediaMode || _frameBuffer == null) return;
        int bytesRead = _rawVideoStream.Read(_frameBuffer, 0, _videoTotalSize);
        if (bytesRead < _videoTotalSize) { _rawVideoStream.Position = 0; return; }
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
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
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
}
