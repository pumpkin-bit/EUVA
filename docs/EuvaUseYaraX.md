## Euva Use Yarax rules

So, the important technical part of this project is the YaraX rules engine. It's needed to expand the range of tasks and maintain compatibility with thousands of ready-made rules.
Here, the MMF file isn't loaded into RAM, but the OS allocates virtual addresses that reference the data on disk. The data is loaded by the OS kernel at the time of access.
This is implemented specifically to save RAM.
For small files up to 256 MB, mapping is done in one piece. Large files in this case are scanned in chunks or windows of 16 MB each. Also, to avoid situations where half of the signature is in another window, there is an overlap of 64 kilobytes. This is enough for each chunk or window to overlap another.

If thousands of rules have the same name, the engine doesn't create thousands of strings; it stores them by reusing the same reference.
In the hex context generation and hashing methods, memory is allocated on the stack. This code also works with unsafe pointers to remove array bounds checks, providing some speed gain.
Security features have been implemented. If the engine detects bad rules, such as finding any byte, the engine will simply abort scanning either the entire file or a chunk.
The interface has a limit on the number of records. If the limit is exceeded, the scanner will pause and wait. This is done to avoid cluttering the memory or channel with an endless number of new records. 

You can also change the ruleset on the fly if there's a new rules file.
And there's a shutdown system that doesn't just throw an exception but tries to terminate gracefully via a flag. However, you need to be careful with this, as it's an experimental feature and its behavior may be ambiguous at the moment. When a match is found, the program needs to show it in the byte grid; a pointer is taken; chunk boundaries are checked, or the entire file is not checked; if the file is small, the bytes are converted to a string using bitwise shifts.

---

**YaraIntegration.cs**

```csharp

using System;
using System.Collections.Concurrent;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DefenceTechSecurity.Yarax;

namespace EUVA.UI;

public readonly record struct YaraMatch(
    string RuleName,
    string? Namespace,
    string? MatchedString,
    long Offset,
    string? HexContext
)
{
    public string OffsetHex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => $"0x{Offset:X8}";
    }

    public string Label =>
        MatchedString is { Length: > 0 }
            ? $"{RuleName} :: {MatchedString}"
            : RuleName;
}

public readonly record struct YaraScanProgress(
    long BytesScanned,
    long TotalBytes,
    int MatchesFound,
    bool IsComplete
)
{
    public double Percent =>
        TotalBytes > 0 ? (double)BytesScanned / TotalBytes * 100.0 : 0;
}

public sealed class YaraEngine : IDisposable
{
    private static readonly ConcurrentDictionary<string, string> _stringPool = new();
    private const int StringPoolMaxSize = 10_000;

    private YaraxRulesHandle? _rulesHandle;
    private byte[]? _rulesFileHash;
    private string? _rulesFilePath;
    private readonly SemaphoreSlim _compileLock = new(1, 1);

    private volatile CancellationTokenSource? _scanCts;
    private volatile bool _isScanRunning;
    public bool IsScanRunning => _isScanRunning;
    private const long LargeFileThreshold = 256L * 1024 * 1024; 
    private const int ChunkSize    = 16 * 1024 * 1024;
    private const int ChunkOverlap = 64 * 1024;       
    private const int MaxMatchesPerChunk = 50_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Intern(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (_stringPool.Count > StringPoolMaxSize) _stringPool.Clear();
        return _stringPool.GetOrAdd(s, s);
    }

    public static Channel<YaraMatch> CreateBoundedChannel(int capacity = 1_000) =>
        Channel.CreateBounded<YaraMatch>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,         
            SingleReader = false,       
            FullMode     = BoundedChannelFullMode.Wait,
        });

    public async Task<bool> LoadRulesAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        byte[] hash = await ComputeFileHashAsync(path, ct).ConfigureAwait(false);

        await _compileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rulesFileHash != null && _rulesFilePath == path &&
                hash.AsSpan().SequenceEqual(_rulesFileHash.AsSpan()))
            {
                progress?.Report("[YARA] Rules unchanged, reusing cached compilation.");
                return true;
            }

            progress?.Report($"[YARA] Compiling: {Path.GetFileName(path)} …");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            YaraxRulesHandle newRules = await Task.Run(() =>
            {
                using var compiler = YaraxCompilerHandle.Create();
                compiler.AddFile(path);
                return compiler.Build();
            }, ct).ConfigureAwait(false);

            var old = Interlocked.Exchange(ref _rulesHandle, newRules);
            old?.Dispose();

            _rulesFileHash = hash;
            _rulesFilePath = path;

            sw.Stop();
            progress?.Report($"[YARA] Compiled in {sw.Elapsed.TotalMilliseconds:F0} ms.");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"[YARA] Compile ERROR: {ex.Message}");
            return false;
        }
        finally { _compileLock.Release(); }
    }

    public async Task ScanAsync(
        MemoryMappedFile mmf,
        long fileLength,
        ChannelWriter<YaraMatch> writer,
        IProgress<YaraScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var rules = _rulesHandle
            ?? throw new InvalidOperationException("No rules compiled. Load a .yar file first.");
            
        if (IntPtr.Size == 4 && fileLength > int.MaxValue)
            throw new PlatformNotSupportedException(
                "Scanning files larger than 2 GB requires a 64-bit process.");

        if (fileLength <= 0) { writer.TryComplete(); return; }

        _scanCts?.Cancel();
        using var cts    = new CancellationTokenSource();
        _scanCts          = cts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        var token         = linked.Token;

        _isScanRunning = true;
        int matchCount  = 0;

        try
        {
            matchCount = fileLength <= LargeFileThreshold
                ? await ScanSmallAsync(mmf, fileLength, rules, writer, progress, token).ConfigureAwait(false)
                : await ScanLargeAsync(mmf, fileLength, rules, writer, progress, token).ConfigureAwait(false);
        }
        finally
        {
            _isScanRunning = false;
            Interlocked.Exchange(ref _scanCts, null);
            progress?.Report(new YaraScanProgress(fileLength, fileLength, matchCount, true));
            writer.TryComplete();
        }
    }

    private static async Task<int> ScanSmallAsync(
        MemoryMappedFile mmf,
        long fileLength,
        YaraxRulesHandle rules,
        ChannelWriter<YaraMatch> writer,
        IProgress<YaraScanProgress>? progress,
        CancellationToken ct)
    {
        int size = (int)fileLength;
        var pendingMatches = new System.Collections.Generic.List<YaraMatch>();

        using var acc = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        await Task.Run(() =>
        {
            unsafe
            {
                byte* ptr = null;
                acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    bool cancelFlag = false;

                    using var scanner = new YaraxScanner(rules);
                    scanner.OnHit += (ref YaraxRuleHit hit) =>
                    {
                        if (cancelFlag) return;
                        if (ct.IsCancellationRequested) { cancelFlag = true; return; }

                        if (pendingMatches.Count >= MaxMatchesPerChunk) { cancelFlag = true; return; }

                        string  ruleName = Intern(hit.Name);
                        string? ns       = hit.Namespace != null ? Intern(hit.Namespace) : null;

                        foreach (var match in hit.Matches)
                        {
                            long offset = (long)match.Offset;

                            string? id  = null;
                            string  raw = match.ToString() ?? "";
                            int     at  = raw.IndexOf(" @ ", StringComparison.Ordinal);
                            if (at > 0) id = raw[..at].Trim();

                            pendingMatches.Add(new YaraMatch(ruleName, ns, id, offset,
                                BuildHexContext(ptr, size, offset)));

                            if (pendingMatches.Count >= MaxMatchesPerChunk) { cancelFlag = true; return; }
                        }
                    };

                    scanner.Scan(new ReadOnlySpan<byte>(ptr, size));
                    if (cancelFlag) ct.ThrowIfCancellationRequested();
                }
                finally
                {
                    acc.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }, ct).ConfigureAwait(false);

        foreach (var ym in pendingMatches)
            await writer.WriteAsync(ym, ct).ConfigureAwait(false);

        return pendingMatches.Count;
    }

    private static async Task<int> ScanLargeAsync(
        MemoryMappedFile mmf,
        long fileLength,
        YaraxRulesHandle rules,
        ChannelWriter<YaraMatch> writer,
        IProgress<YaraScanProgress>? progress,
        CancellationToken ct)
    {
        int  localCount = 0;
        long chunkStart = 0;

        while (chunkStart < fileLength)
        {
            ct.ThrowIfCancellationRequested();

            long remaining = fileLength - chunkStart;
            int readSize = (int)Math.Min((long)ChunkSize + ChunkOverlap, remaining);
            int scanSize = (int)Math.Min(ChunkSize, remaining);
            long capturedStart = chunkStart;
            var pendingMatches = new System.Collections.Generic.List<YaraMatch>();

         
            using (var acc = mmf.CreateViewAccessor(chunkStart, readSize, MemoryMappedFileAccess.Read))
            {
                await Task.Run(() =>
                {
                    unsafe
                    {
                        byte* ptr = null;
                        acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            bool cancelFlag = false;

                            using var scanner = new YaraxScanner(rules);
                            scanner.OnHit += (ref YaraxRuleHit hit) =>
                            {
                                if (cancelFlag) return;
                                if (ct.IsCancellationRequested) { cancelFlag = true; return; }
                                if (pendingMatches.Count >= MaxMatchesPerChunk) { cancelFlag = true; return; }

                                string  ruleName = Intern(hit.Name);
                                string? ns       = hit.Namespace != null ? Intern(hit.Namespace) : null;

                                foreach (var match in hit.Matches)
                                {
                                    long localOffset = (long)match.Offset; 
                                    if (localOffset >= scanSize) continue;

                                    long absOffset = capturedStart + localOffset;

                                    string? id  = null;
                                    string  raw = match.ToString() ?? "";
                                    int     at  = raw.IndexOf(" @ ", StringComparison.Ordinal);
                                    if (at > 0) id = raw[..at].Trim();
 
                                    pendingMatches.Add(new YaraMatch(ruleName, ns, id, absOffset,
                                        BuildHexContext(ptr, readSize, localOffset)));
 
                                    if (pendingMatches.Count >= MaxMatchesPerChunk) { cancelFlag = true; return; }
                                }
                            };

                            scanner.Scan(new ReadOnlySpan<byte>(ptr, readSize));
                            if (cancelFlag) ct.ThrowIfCancellationRequested();
                        }
                        finally
                        {
                            acc.SafeMemoryMappedViewHandle.ReleasePointer();
                        }
                    }
                }, ct).ConfigureAwait(false);
            } 
            foreach (var ym in pendingMatches)
                await writer.WriteAsync(ym, ct).ConfigureAwait(false);

            localCount += pendingMatches.Count;
            chunkStart += scanSize;

            progress?.Report(new YaraScanProgress(chunkStart, fileLength, localCount, false));
        }

        return localCount;
    } 
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe string? BuildHexContext(byte* data, int dataLength, long offset)
    {
        if ((ulong)offset >= (ulong)dataLength) return null;

        int take = (int)Math.Min(16L, dataLength - offset);
        if (take <= 0) return null;

        Span<char> chars = stackalloc char[take * 3 - 1];
        int pos = 0;

        byte* src = data + offset;

        for (int i = 0; i < take; i++)
        {
            byte b  = src[i]; 
            if (i > 0) chars[pos++] = ' ';

            int hi = b >> 4;
            int lo = b & 0xF;
            chars[pos++] = (char)(hi < 10 ? '0' + hi : 'A' + hi - 10);
            chars[pos++] = (char)(lo < 10 ? '0' + lo : 'A' + lo - 10);
        }

        return new string(chars);
    }

    private static async Task<byte[]> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var fs   = new FileStream(path, FileMode.Open, FileAccess.Read,
                                 FileShare.Read, 65536, FileOptions.SequentialScan);
            byte[] head = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int  read = fs.Read(head, 0, 65536);
                long size = fs.Length;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(head, 0, read);

                Span<byte> lenBytes = stackalloc byte[8];
                MemoryMarshal.Write(lenBytes, in size);
                hash.AppendData(lenBytes);

                return hash.GetHashAndReset();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
        }, ct).ConfigureAwait(false);
    }

    public void CancelScan() => _scanCts?.Cancel();

    public void Dispose()
    {
        _rulesHandle?.Dispose();
        _rulesHandle = null;
        _compileLock.Dispose();
    }
}

```

---

**YaraMainWindowPartial.cs**

```csharp
// YARA-x may be an experimental feature, so please keep in mind that I cannot guarantee 100% security or stability of the Yara engine.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EUVA.UI;

public sealed class YaraDetectionEntry
{
    public string Name { get; }
    public string Type { get; }

    public long Offset { get; }

    public YaraDetectionEntry(in YaraMatch m)
    {
        Name = m.Label;
        Offset = m.Offset;

        var sb = new System.Text.StringBuilder(72);
        sb.Append("YARA  ·  ");
        sb.Append(m.OffsetHex);
        if (m.HexContext is { Length: > 0 })
        {
            sb.Append("  |  ");
            sb.Append(m.HexContext);
        }
        Type = sb.ToString();
    }
}

public partial class MainWindow
{
    private readonly YaraEngine _yaraEngine = new();
    private Channel<YaraMatch>? _yaraChannel;
    private DispatcherTimer? _yaraUiTimer;
    private string? _yaraRulesPath;
    private readonly List<YaraDetectionEntry> _yaraBatch = new(200);
    public void InitializeYara()
    {
        _yaraUiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _yaraUiTimer.Tick += DrainChannelToDetectionList;

    }
    private async void BtnLoadYaraRules_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "YARA rules (*.yar;*.yara)|*.yar;*.yara|All files (*.*)|*.*",
            Title = "Select YARA rule file"
        };
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;

        TxtDetectionStatus.Text = $"Compiling {Path.GetFileName(path)} …";

        var progress = new Progress<string>(msg => LogMessage(msg));
        bool ok = await _yaraEngine.LoadRulesAsync(path, progress);

        if (ok)
        {
            _yaraRulesPath = path;
            TxtDetectionStatus.Text = $"Rules ready: {Path.GetFileName(path)}  —  click Run YARAx";
        }
        else
        {
            TxtDetectionStatus.Text = "Compilation failed — see console";
        }
    }
    private async void BtnRunYaraScan_Click(object sender, RoutedEventArgs e)
    {
        if (HexView.FileLength == 0)
        {
            LogMessage("[YARA] No file loaded.");
            return;
        }

        //  the delay is needed to ensure the channel is clear and to confirm that everything has either been cancelled or loaded.
        if (_yaraEngine.IsScanRunning)
        {
            _yaraEngine.CancelScan();
            await Task.Delay(150);
        }

        DetectionList.ItemsSource = null;
        DetectionList.Items.Clear();
        HexView.SetYaraOffsets(Array.Empty<long>());
        TxtYaraMatchCount.Text = "";
        TxtDetectionStatus.Text = "YARA scan running …";
        Mouse.OverrideCursor = Cursors.Wait;

        SwitchToDetectionsTab();


        //  using queues and buffer overflow protection
        _yaraChannel = Channel.CreateBounded<YaraMatch>(new BoundedChannelOptions(65536)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
        _yaraUiTimer!.Start();

        long fileLength = HexView.FileLength;

        var scanProgress = new Progress<YaraScanProgress>(p =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (p.MatchesFound > 0)
                    TxtYaraMatchCount.Text =
                        $"{p.MatchesFound} match{(p.MatchesFound == 1 ? "" : "es")}";
            }));

        LogMessage($"[YARA] Starting scan of {fileLength:N0} bytes …");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            MemoryMappedFile? mmf = HexView.GetMemoryMappedFile();
            if (mmf == null)
            {
                LogMessage("[YARA] ERROR: MMF not available.");
                return;
            }

            await _yaraEngine.ScanAsync(
                mmf, fileLength, _yaraChannel.Writer, scanProgress);

            sw.Stop();
            int count = DetectionList.Items.Count;
            string done = count == 0
                ? "YARA scan complete no matches"
                : $"YARA scan complete {count} match{(count == 1 ? "" : "es")} in {sw.Elapsed.TotalSeconds:F2}s";

            TxtDetectionStatus.Text = done;
            TxtYaraMatchCount.Text = count > 0 ? $"{count} matches" : "";
            LogMessage($"[YARA] {done}");
        }
        catch (OperationCanceledException)
        {
            LogMessage("[YARA] Scan cancelled.");
            TxtDetectionStatus.Text = "YARA scan cancelled.";
        }

        // be aware that the engine may throw another error, but it will be handled by a general exception.
        catch (InvalidOperationException ex) when (ex.Message.Contains("No rules"))
        {
            LogMessage("[YARA] ERROR: No rules compiled. Load a .yar file first.");
            TxtDetectionStatus.Text = "Load rules first!";
        }
        catch (Exception ex)
        {
            LogMessage($"[YARA] ERROR: {ex.Message}");
            TxtDetectionStatus.Text = $"YARA error: {ex.Message}";
        }
        finally
        {
            _yaraUiTimer!.Stop();
            DrainChannelToDetectionList(null, EventArgs.Empty);

            var offsets = new List<long>(DetectionList.Items.Count);
            foreach (var item in DetectionList.Items)
                if (item is YaraDetectionEntry entry)
                    offsets.Add(entry.Offset);
            HexView.SetYaraOffsets(offsets);

            _yaraChannel = null;
            Mouse.OverrideCursor = null;
        }
    }

    private void BtnCancelYaraScan_Click(object sender, RoutedEventArgs e)
    {
        _yaraEngine.CancelScan();
        LogMessage("[YARA] Cancel requested …");
    }
    private void DrainChannelToDetectionList(object? sender, EventArgs e)
    {
        var ch = _yaraChannel;
        if (ch == null) return;

        const int MaxPerTick = 200;
        _yaraBatch.Clear();

        while (_yaraBatch.Count < MaxPerTick && ch.Reader.TryRead(out var match))
        {
            if (match.RuleName is { Length: > 0 })
                _yaraBatch.Add(new YaraDetectionEntry(in match));
        }

        if (_yaraBatch.Count == 0) return;

        foreach (var entry in _yaraBatch)
            DetectionList.Items.Add(entry);

        int total = DetectionList.Items.Count;
        TxtYaraMatchCount.Text = $"{total} match{(total == 1 ? "" : "es")}";
    }

    private void DetectionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetectionList.SelectedItem is not YaraDetectionEntry entry) return;
        if (entry.Offset < 0 || entry.Offset >= HexView.FileLength) return;

        HexView.SelectedOffset = entry.Offset;
        HexView.ScrollToOffset(entry.Offset);
        LogMessage($"[YARA] Jumped to {entry.Name} @ 0x{entry.Offset:X8}");
    }

    public void OnFileLoaded()
    {
        TxtYaraMatchCount.Text = "";
        TxtDetectionStatus.Text = _yaraRulesPath != null
            ? $"New file loaded click Run YARAx  ({Path.GetFileName(_yaraRulesPath)})"
            : "Run Detectors or load YARA rules to begin";
        HexView.SetYaraOffsets(Array.Empty<long>());
    }
    public void ResetYaraState()
    {
        _yaraEngine.CancelScan();
        _yaraUiTimer?.Stop();
        _yaraChannel = null;
        HexView.SetYaraOffsets(Array.Empty<long>());
    }
    private void SwitchToDetectionsTab()
    {
        foreach (TabItem tab in RightTabControl.Items)
        {
            if (tab.Header is string h && h == "Detections")
            {
                RightTabControl.SelectedItem = tab;
                return;
            }
        }
    }
}

```