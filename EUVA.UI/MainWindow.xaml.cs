
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
        _bindings[(ModifierKeys.Alt,                          Key.D1)] = EUVAAction.NavInspector;
        _bindings[(ModifierKeys.Alt,                          Key.D2)] = EUVAAction.NavSearch;
        _bindings[(ModifierKeys.Alt,                          Key.D3)] = EUVAAction.NavDetections;
        _bindings[(ModifierKeys.Alt,                          Key.D4)] = EUVAAction.NavProperties;
        _bindings[(ModifierKeys.Control,                      Key.Z)]  = EUVAAction.Undo;
        _bindings[(ModifierKeys.Control | ModifierKeys.Shift, Key.Z)]  = EUVAAction.FullUndo;
        _bindings[(ModifierKeys.Control,                      Key.C)]  = EUVAAction.CopyHex;
        _bindings[(ModifierKeys.Control | ModifierKeys.Shift, Key.C)]  = EUVAAction.CopyCArray;
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
                ModifierKeys mods  = ModifierKeys.None;
                Key          targetKey = Key.None;
                foreach (var k in keys)
                {
                    string token = k.Trim();
                    if      (Enum.TryParse(token, true, out ModifierKeys m))  mods      |= m;
                    else if (Enum.TryParse(token, true, out Key tk))          targetKey  = tk;
                    else    throw new Exception($"Unknown token: '{token}'");
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
    public string              Name   = "";
    public string              Access = "";
    public List<string>        Body   = new();
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
            int rel    = (int)(target - (currentAddr + 5));
            var result = new byte[5];
            result[0]  = 0xE9;
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
            result[0]  = (byte)(0xB8 + regIdx);
            WriteLE32(result, 1, val);
            return result;
        }

        return null;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLE32(byte[] buf, int off, int v)
    {
        buf[off]     = (byte) v;
        buf[off + 1] = (byte)(v >>  8);
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
            byte b  = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            shift  += 7;
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
            byte b  = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift  += 7;
        }
        return (result, pos);
    }
    public static string ParseDosDate(ushort v) => DataParser.ToDosDate(v);
    public static string ParseDosTime(ushort v) => DataParser.ToDosTime(v);
}
public partial class MainWindow : Window
{
    private PEMapper?         _mapper;
    private DetectorManager   _detectorManager = new();
    public static MainWindow  Instance { get; private set; } = null!;
    private readonly ConcurrentQueue<(string Text, Brush Color)> _logQueue = new();
    private readonly System.Windows.Threading.DispatcherTimer _logFlushTimer;
    private static readonly Regex _whitespaceRegex =
        new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex _clinkBracketRegex =
        new(@"\[(.*?)\]", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly byte[] _inspectorBuf = new byte[16];
    private readonly Stack<(long Offset, byte[] Old, byte[] New)> _undoStack = new();
    private readonly Stack<int>  _transactionSteps = new();
    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

    private FileStream?      _rawVideoStream;
    private byte[]?          _frameBuffer;
    private readonly int     _videoWidth  = 24;
    private readonly int     _videoHeight = 26;
    private int              _videoTotalSize;
    private string?          _activeScriptPath;
    private FileSystemWatcher? _scriptWatcher;
    private volatile bool    _isProcessingScript = false;
    private string?          _lastScriptPath;
    private bool             IsLittleEndian = true;

    private readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkey.cfg");

    public MainWindow()
    {
        InitializeComponent();
        Instance        = this;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _logFlushTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(100) };
        _logFlushTimer.Tick += FlushLogBuffer;
        _logFlushTimer.Start();

        InitializeSystemSettings();
        InitializeDetectors();
    }

    public void Log(string message, Brush color)
    {
        if (color.CanFreeze && !color.IsFrozen) color.Freeze();
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        _logQueue.Enqueue((line, color));
    }

    private void SafeLog(string msg, Brush color)       => Log(msg, color);
    private void LogMessage(string msg)                 => Log(msg, Brushes.White);
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
        string configPath    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "euva.cfg");
        string defaultTheme  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Theming", "default.themes");
        string currentHtk    = "", currentTheme = defaultTheme, alwaysDefault = defaultTheme;

        if (File.Exists(configPath))
        {
            var lines = File.ReadAllLines(configPath);
            if (lines.Length > 0) currentHtk    = lines[0];
            if (lines.Length > 1) currentTheme   = lines[1];
            if (lines.Length > 2) alwaysDefault  = lines[2];
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
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
                items.Add(new InspectorItem("Int8 / UInt8",     $"{(sbyte)b} | {b}",     $"{b:X2}"));
                items.Add(new InspectorItem("Двоичный (8 бит)", Convert.ToString(b, 2).PadLeft(8, '0'), "-"));
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
                uint  uv = IsLittleEndian ? MemoryMarshal.Read<uint>(s4)
                                          : BinaryPrimitives.ReadUInt32BigEndian(s4);
                float fv = MemoryMarshal.Read<float>(s4);
                items.Add(new InspectorItem("Int32 / UInt32",   $"{(int)uv} | {uv}",
                    BitConverter.ToString(_inspectorBuf, 0, 4)));
                items.Add(new InspectorItem("Single (float32)", fv.ToString("G6"), "-"));
                items.Add(new InspectorItem("time_t (32 бит)",
                    DateTimeOffset.FromUnixTimeSeconds(uv).DateTime.ToString(), "Unix"));
            }
            if (remaining >= 8)
            {
                var s8 = new ReadOnlySpan<byte>(_inspectorBuf, 0, 8);
                ulong  uv = IsLittleEndian ? MemoryMarshal.Read<ulong>(s8)
                                           : BinaryPrimitives.ReadUInt64BigEndian(s8);
                double dv = MemoryMarshal.Read<double>(s8);
                items.Add(new InspectorItem("Int64 / UInt64",   uv.ToString(),
                    BitConverter.ToString(_inspectorBuf, 0, 8)));
                items.Add(new InspectorItem("Double (float64)", dv.ToString("G8"), "-"));
                items.Add(new InspectorItem("FILETIME",
                    DateTime.FromFileTime((long)uv).ToString(), "Win32"));
                items.Add(new InspectorItem("OLETIME",
                    DateTime.FromOADate(dv).ToString(), "OLE"));
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
            MessageBox.Show("Please load a PE file first.", "No File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LogMessage("Running detection analysis...");
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            int    size = (int)Math.Min(HexView.FileLength, 10 * 1024 * 1024);
            byte[] buf  = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                HexView.ReadBytes(0, buf.AsSpan(0, size));
                var mem      = new ReadOnlyMemory<byte>(buf, 0, size);
                var progress = new Progress<string>(msg => LogMessage(msg));
                var results  = await _detectorManager.AnalyzeAsync(
                    mem, _mapper.RootStructure, progress);

                DetectionList.ItemsSource = results;
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
        int    size = (int)Math.Min(HexView.FileLength, 10 * 1024 * 1024);
        byte[] buf  = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            HexView.ReadBytes(0, buf.AsSpan(0, size));
            var span    = buf.AsSpan(0, size);
            var regions = _mapper.GetRegions();
            foreach (var r in SignatureScanner.AnalyzeSectionEntropy(span, regions))
                LogMessage($"  {r.Key}: {r.Value:F3} bits");
            LogMessage($"Overall entropy: {SignatureScanner.CalculateEntropy(span):F3} bits");
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
    private long FindSignature(string pattern)
    {
        var  parts  = pattern.Split(' ');
        int  patLen = parts.Length;
        if (patLen == 0) return -1;

        byte[] pat    = new byte[patLen];
        bool[] isMask = new bool[patLen];
        bool   anyWild = false;

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
                long avail   = fileLen - pos;
                int  toRead  = (int)Math.Min(ChunkSize, avail);
                int  overlap = (int)Math.Min(patLen - 1, avail - toRead);
                int  total   = toRead + overlap;

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
        for (int i = 0; i < 256; i++) bad[i] = patLen;
        for (int i = 0; i < patLen - 1; i++)
            if (!isMask[i]) bad[pat[i]] = patLen - 1 - i;

        const int ChunkSize = 256 * 1024;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize + patLen);
        try
        {
            long pos = 0;
            while (pos <= fileLen - patLen)
            {
                long avail   = fileLen - pos;
                int  toRead  = (int)Math.Min(ChunkSize, avail);
                int  overlap = (int)Math.Min(patLen - 1, avail - toRead);
                int  total   = toRead + overlap;

                int  limit   = total - patLen + 1;

                HexView.ReadBytes(pos, chunk.AsSpan(0, total));

                int i = 0;
                while (i < limit)
                {
                    int skip = bad[chunk[i + patLen - 1]];
                    if (skip > 1) { i += skip; continue; }


                    bool match = true;
                    for (int j = patLen - 1; j >= 0; j--)
                        if (!isMask[j] && chunk[i + j] != pat[j]) { match = false; break; }

                    if (match) return pos + i;
                    i++;
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
        int      stepsInThisRun = 0;
        string[] lines;
        try   { lines = await File.ReadAllLinesAsync(scriptPath); }
        catch (Exception ex) { Log($"[Engine] IO Error: {ex.Message}", Brushes.Red); return; }

        int  totalChanges = 0;
        var  globalScope  = new Dictionary<string, long>();



        long fileLength = 0;
        await Dispatcher.InvokeAsync(() => fileLength = HexView.FileLength);

        await Task.Run(() =>
        {
            try
            {
                long   lastAddress    = 0;
                string currentModifier = "default";
                MethodContainer? currentMethod = null;
                bool inScriptBody = false, isTerminated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = _whitespaceRegex
                        .Replace(lines[i].Split('#')[0].Split("//")[0], " ").Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.ToLower() == "start;") { inScriptBody = true; continue; }
                    if (!inScriptBody) continue;
                    if (line.ToLower() == "end;")   { isTerminated = true; break; }

                    if (line.EndsWith(":"))
                    {
                        var mod = line.Replace(":", "").ToLower();
                        if (mod == "public" || mod == "private")
                            { currentModifier = mod; continue; }
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
                            SafeLog($"[Engine] Executing method: {currentMethod.Name}",
                                Brushes.CornflowerBlue);

                            foreach (var cmd in currentMethod.Body)
                                ExecuteCommand(cmd, localScope, globalScope,
                                    ref lastAddress, ref totalChanges,
                                    ref stepsInThisRun, fileLength);

                            if (currentMethod.Access == "public")
                            {
                                foreach (var exportName in currentMethod.Clinks.Keys)
                                {
                                    if (localScope.TryGetValue(exportName, out long addr))
                                    {
                                        globalScope[$"{currentMethod.Name}.{exportName}"] = addr;
                                        SafeLog($"[Link] {currentMethod.Name}.{exportName} -> 0x{addr:X}",
                                            Brushes.Cyan);
                                    }
                                }
                            }
                            currentMethod = null;
                            continue;
                        }

                        if (line.ToLower().StartsWith("clink:") || line.Contains("["))
                        {
                            int j = i; string fullClink = "";
                            while (j < lines.Length && !lines[j].Contains("]"))
                                fullClink += lines[j++];
                            if (j < lines.Length) fullClink += lines[j];
                            var match = _clinkBracketRegex.Match(fullClink);
                            if (match.Success)
                            {
                                var names = match.Groups[1].Value
                                    .Split(new[] { ',', '\r', '\n' },
                                        StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim());
                                foreach (var name in names) currentMethod.Clinks[name] = 0;
                            }
                            i = j; continue;
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
        Dictionary<string, long> localScope, Dictionary<string, long> globalScope,
        ref long lastAddress, ref int totalChanges, ref int stepsInThisRun,
        long currentFileLength)
    {
        string cmd = line.ToLower();
        try
        {
            if (cmd.StartsWith("find"))
            {
                var fp = ExtractInsideBrackets(line).Split('=');
                if (fp.Length < 2) return;
                string varName = fp[0].Trim();
                string sigPat  = fp[1].Trim();
                long   rawAddr = FindSignature(sigPat);

                if (rawAddr == -1)
                {
                    localScope[varName] = long.MinValue;
                    SafeLogThreadSafe(
                        $"[Search] Signature NOT found: '{sigPat}'. " +
                        $"Variable '{varName}' marked INVALID — all dependent commands will be skipped.",
                        Brushes.Orange);
                }
                else
                {
                    localScope[varName] = rawAddr;
                    SafeLogThreadSafe($"[Search] ✓ {varName} = 0x{rawAddr:X8}", Brushes.Violet);
                }
            }
            else if (cmd.StartsWith("set"))
            {
                var sp = ExtractInsideBrackets(line).Split('=');
                if (sp.Length < 2) return;
                string varName = sp[0].Trim();
                long   val     = ParseMath(sp[1], lastAddress, localScope, globalScope);
                localScope[varName] = val; 

                if (val == long.MinValue)
                    SafeLogThreadSafe(
                        $"[Set] Variable '{varName}' set to INVALID " +
                        $"(expression depends on a missing signature).",
                        Brushes.Orange);
            }
            else
            {
                string addrPart = line.Contains(':')
                    ? line.Split(':')[0]
                    : ExtractInsideBrackets(line).Split(':')[0];

                long addr = ParseMath(addrPart, lastAddress, localScope, globalScope);
                if (addr == long.MinValue)
                {
                    SafeLogThreadSafe(
                        $"[Skip] Command '{line.Split(':')[0].Trim()}' skipped: " +
                        $"address expression depends on an INVALID variable (missing signature).",
                        Brushes.Yellow);
                    return;
                }

                if (addr < 0 || addr >= currentFileLength)
                {
                    SafeLogThreadSafe($"[Skip] Address 0x{addr:X8} out of range " +
                        $"(file size: 0x{currentFileLength:X8}).", Brushes.Yellow);
                    return;
                }

                if (cmd.StartsWith("check"))
                {
                    byte[] expected = ParseBytes(line.Split(':')[1]);
                    for (int i = 0; i < expected.Length; i++)
                        if (HexView.ReadByte(addr + i) != expected[i])
                        {
                            SafeLogThreadSafe($"[Check Fail] 0x{addr:X} mismatch.", Brushes.OrangeRed);
                            return;
                        }
                    return;
                }

                if (line.Contains(':'))
                {
                    string dataPart = line.Split(':')[1].Trim();
                    byte[]? bytes   = AsmLogic.Assemble(dataPart, addr);

                    if (bytes == null && dataPart.Contains('"'))
                    {
                        var m = Regex.Match(dataPart, "\"(.*)\"");
                        if (m.Success)
                            bytes = System.Text.Encoding.ASCII.GetBytes(m.Groups[1].Value);
                    }
                    if (bytes == null) bytes = ParseBytes(dataPart);

                    if (bytes is { Length: > 0 })
                    {
                        byte[] oldBytes = new byte[bytes.Length];
                        for (int i = 0; i < bytes.Length; i++)
                            oldBytes[i] = HexView.ReadByte(addr + i);

                        SafeLogThreadSafe(
                            $"[Patch] 0x{addr:X}: {BitConverter.ToString(oldBytes).Replace("-", " ")} -> " +
                            $"{BitConverter.ToString(bytes).Replace("-", " ")}", Brushes.YellowGreen);

                        lock (_undoStack) { _undoStack.Push((addr, oldBytes, bytes)); stepsInThisRun++; }

                        var captured = bytes;
                        Dispatcher.Invoke(() =>
                        {
                            for (int i = 0; i < captured.Length; i++)
                                HexView.WriteByte(addr + i, captured[i]);
                        });

                        totalChanges += bytes.Length;
                        lastAddress   = addr + bytes.Length;

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
    private static long ParseMath(string expr, long lastAddr,
        Dictionary<string, long> localScope, Dictionary<string, long> globalScope)
    {
        ReadOnlySpan<char> src = expr.AsSpan().Trim();
        if (src.Length == 0 || src is "." or "()") return lastAddr;
        char[] buf = ArrayPool<char>.Shared.Rent(src.Length * 21 + 64);
        int outLen = 0;
        try
        {
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < src.Length &&
                           (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '.'))
                        i++;
                    string token = src.Slice(start, i - start).ToString();

                    long val = localScope.TryGetValue(token,  out long lv) ? lv
                             : globalScope.TryGetValue(token, out long gv) ? gv
                             : 0L;
                    if (val == long.MinValue) return long.MinValue;

                    AppendLong(buf, ref outLen, val);
                    continue;
                }
                if (outLen < buf.Length) buf[outLen++] = c;
                i++;
            }
            return EvalExpr(buf.AsSpan(0, outLen));
        }
        catch { return long.MinValue; }
        finally { ArrayPool<char>.Shared.Return(buf); }
    }
    private static void AppendLong(char[] buf, ref int len, long v)
    {
        if (v == 0) { if (len < buf.Length) buf[len++] = '0'; return; }
        if (v < 0)  { if (len < buf.Length) buf[len++] = '-';
                      if (v == long.MinValue) { AppendStr(buf, ref len, "9223372036854775808"); return; }
                      v = -v; }
        int start = len;
        while (v > 0 && len < buf.Length) { buf[len++] = (char)('0' + v % 10); v /= 10; }
        int end = len - 1;
        while (start < end) { (buf[start], buf[end]) = (buf[end], buf[start]); start++; end--; }
    }

    private static void AppendStr(char[] buf, ref int len, string s)
    {
        foreach (char c in s) if (len < buf.Length) buf[len++] = c;
    }

    private static long EvalExpr(ReadOnlySpan<char> s) { int p = 0; return EvalAdd(s, ref p); }

    private static long EvalAdd(ReadOnlySpan<char> s, ref int p)
    {
        long lhs = EvalMul(s, ref p); SkipWs(s, ref p);
        while (p < s.Length && (s[p] == '+' || s[p] == '-'))
        {
            char op = s[p++]; SkipWs(s, ref p);
            long rhs = EvalMul(s, ref p);
            lhs = op == '+' ? lhs + rhs : lhs - rhs;
            SkipWs(s, ref p);
        }
        return lhs;
    }

    private static long EvalMul(ReadOnlySpan<char> s, ref int p)
    {
        long lhs = EvalUnary(s, ref p); SkipWs(s, ref p);
        while (p < s.Length && (s[p] == '*' || s[p] == '/' || s[p] == '%'))
        {
            char op = s[p++]; SkipWs(s, ref p);
            long rhs = EvalUnary(s, ref p);
            lhs = op == '*' ? lhs * rhs
                : op == '/' ? (rhs == 0 ? 0 : lhs / rhs)
                :              (rhs == 0 ? 0 : lhs % rhs);
            SkipWs(s, ref p);
        }
        return lhs;
    }

    private static long EvalUnary(ReadOnlySpan<char> s, ref int p)
    {
        SkipWs(s, ref p);
        if (p < s.Length && s[p] == '-') { p++; return -EvalAtom(s, ref p); }
        if (p < s.Length && s[p] == '+') { p++; }
        return EvalAtom(s, ref p);
    }

    private static long EvalAtom(ReadOnlySpan<char> s, ref int p)
    {
        SkipWs(s, ref p);
        if (p >= s.Length) return 0;

        if (s[p] == '(')
        {
            p++;
            long val = EvalAdd(s, ref p); SkipWs(s, ref p);
            if (p < s.Length && s[p] == ')') p++;
            return val;
        }
        if (p + 1 < s.Length && s[p] == '0' && (s[p + 1] == 'x' || s[p + 1] == 'X'))
        {
            p += 2; long val = 0;
            while (p < s.Length && IsHexChar(s[p]))
                val = val * 16 + HexNibble(s[p++]);
            return val;
        }
        if (s[p] >= '0' && s[p] <= '9')
        {
            long val = 0;
            while (p < s.Length && s[p] >= '0' && s[p] <= '9')
                val = val * 10 + (s[p++] - '0');
            return val;
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SkipWs(ReadOnlySpan<char> s, ref int p)
    { while (p < s.Length && s[p] == ' ') p++; }
    private static byte[] ParseBytes(string s)      => ParseBytes(s.AsSpan());
    private static byte[] ParseBytes(ReadOnlySpan<char> input)
    {
        int count = 0;
        for (int i = 0; i + 1 < input.Length; )
        {
            if (IsHexChar(input[i]) && IsHexChar(input[i + 1])) { count++; i += 2; }
            else i++;
        }
        if (count == 0) return Array.Empty<byte>();

        byte[] result = new byte[count];
        int ri = 0;
        for (int i = 0; i + 1 < input.Length && ri < count; )
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
        if (p > 0.95)       return "EOF / Overlay Data";
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
        catch (FormatException)  { ConsoleLog.AppendText($"\n[Search Error] '{input}' is not a valid HEX string."); }
        catch (Exception ex)     { ConsoleLog.AppendText($"\n[Error] {ex.Message}"); }
    }

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSearch_Click(sender, e);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key    = e.Key == Key.System ? e.SystemKey : e.Key;
        var action = HotkeyManager.GetAction(Keyboard.Modifiers, key);

        if (action == EUVAAction.Undo)     { PerformUndo();     e.Handled = true; return; }
        if (action == EUVAAction.FullUndo) { PerformFullUndo(); e.Handled = true; return; }

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
            { Filter = "EUVA Theme Files (*.themes)|*.themes|All Files (*.*)|*.*",
              Title  = "Select Theme File" };
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
            { Filter = "EUVA Hotkeys Files (*.htk)|*.htk|All Files (*.*)|*.*",
              Title  = "Select Hotkeys File" };
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
        _frameBuffer    = new byte[_videoTotalSize];
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
        _frameBuffer    = null;
        HexView.InvalidateVisual();
        LogMessage("[MediaHex] Stopped.");
    }

    private void MenuWatchScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
            { Filter = "Euva Scripts (*.euv)|*.euv|All Files (*.*)|*.*",
              Title  = "Select Script to Watch" };
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
        _scriptWatcher    = new FileSystemWatcher(Path.GetDirectoryName(path)!)
        {
            Filter              = Path.GetFileName(path),
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size |
                                  NotifyFilters.FileName  | NotifyFilters.CreationTime,
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
            try   { await RunParallelEngine(e.FullPath); }
            catch (Exception ex) { Log($"[Error] {ex.Message}", Brushes.Red); }
            finally { _isProcessingScript = false; }
        });
    }
}