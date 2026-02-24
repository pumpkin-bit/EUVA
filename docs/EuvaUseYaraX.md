## Euva Use Yarax rules

So, the important technical part of this project is the YaraX rules engine. It's needed to expand the range of tasks and maintain compatibility with thousands of ready-made rules.
I've separated the scanning logic for different files. For example, for large files, I scan using chunk methods, project, and read in 64MB chunks. I also use MMF here.
I put small files into the RAM buffer for maximum speed.
I also added a small 4kb overlap for the borders, which protects us from the signature being on the border of two pieces so as not to miss anything.

I also struggled with the garbage collector and implemented a function so that I wouldn't constantly create new arrays. It's better to rent them and return them back so that the garbage collector wouldn't dare slow down the program.
I also allocate memory for a string for the hex context, the program also inserts small methods into the call site specifically to avoid wasting time on transitions.

There's a queue system here, which is important to prevent the program interface from crashing because it's impossible to process, or, in other words, highlight, thousands of newly found matches. Everyone lines up in a queue, and there's also a 50-millisecond timer for every 200 matches.
I suggested hashing only the first 64 kilobytes of Yara rules, it's more of a compromise, but I also can't wait for everything to be hashed. The first 64 kilobytes, and reading the rules file from scratch, already guarantees that the rules haven't changed, and if the hash matches, the compiled rules are used.

---

**YaraIntegration.cs**

```csharp

using System;
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
    private YaraxRulesHandle? _rulesHandle;
    private byte[]? _rulesFileHash;
    private string? _rulesFilePath;
    private readonly SemaphoreSlim _compileLock = new(1, 1);

    private volatile CancellationTokenSource? _scanCts;
    private volatile bool _isScanRunning;
    public bool IsScanRunning => _isScanRunning;

    private const long LargeFileThreshold = 256L * 1024 * 1024;
    private const int ChunkSize = 64 * 1024 * 1024;
    private const int ChunkOverlap = 4 * 1024;

    public async Task<bool> LoadRulesAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await _compileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // This function does not imply full hashing, but only partial, that is, the first 64kb, but this will be sufficient for a larger percentage of rule files,
            // otherwise I risk losing speed with full hashing. This compromise is sufficient for most rule files.
            byte[] hash = await ComputeFileHashAsync(path, ct).ConfigureAwait(false);
            if (_rulesFileHash != null && _rulesFilePath == path &&
                hash.AsSpan().SequenceEqual(_rulesFileHash.AsSpan()))
            {
                progress?.Report("[YARA] Rules unchanged reusing cached compilation.");
                return true;
            }

            progress?.Report($"[YARA] Compiling: {System.IO.Path.GetFileName(path)} …");
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
        var rules = _rulesHandle ?? throw new InvalidOperationException("No rules compiled. Load a .yar file first.");
        if (fileLength <= 0) { writer.TryComplete(); return; }

        _scanCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        var token = linked.Token;

        _isScanRunning = true;
        int matchCount = 0;

        try
        {
            if (fileLength <= LargeFileThreshold)
            {
                matchCount = await ScanSmallAsync(mmf, fileLength, rules, writer, progress, token).ConfigureAwait(false);
            }
            else
            {
                matchCount = await ScanLargeAsync(mmf, fileLength, rules, writer, progress, token).ConfigureAwait(false);
            }
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
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            using (var acc = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read))
            {
                await Task.Run(() =>
                {
                    unsafe
                    {
                        byte* ptr = null;
                        acc.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                        try
                        {
                            new ReadOnlySpan<byte>(ptr, size).CopyTo(buf.AsSpan(0, size));
                        }
                        finally
                        {
                            acc.SafeMemoryMappedViewHandle.ReleasePointer();
                        }
                    }
                }, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            int localCount = 0;
            await Task.Run(() =>
            {
                using var scanner = new YaraxScanner(rules);
                scanner.OnHit += (ref YaraxRuleHit hit) =>
                {
                    ct.ThrowIfCancellationRequested();
                    string ruleName = hit.Name;
                    string? ns = hit.Namespace;

                    foreach (var match in hit.Matches)
                    {
                        long offset = (long)match.Offset;
                        string? id = null;
                        string raw = match.ToString() ?? "";
                        int at = raw.IndexOf(" @ ", StringComparison.Ordinal);
                        if (at > 0) id = raw[..at].Trim();

                        var ym = new YaraMatch(
                            ruleName, ns, id, offset,
                            BuildHexContext(buf.AsSpan(0, size), offset));

                        writer.TryWrite(ym);
                        localCount++;
                    }
                };
                scanner.Scan(buf.AsSpan(0, size));
            }, ct).ConfigureAwait(false);

            return localCount;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async Task<int> ScanLargeAsync(
        MemoryMappedFile mmf,
        long fileLength,
        YaraxRulesHandle rules,
        ChannelWriter<YaraMatch> writer,
        IProgress<YaraScanProgress>? progress,
        CancellationToken ct)
    {
        int bufSize = ChunkSize + ChunkOverlap;
        byte[] buf = ArrayPool<byte>.Shared.Rent(bufSize);
        int localCount = 0;
        long chunkStart = 0;

        try
        {
            while (chunkStart < fileLength)
            {
                ct.ThrowIfCancellationRequested();

                long remaining = fileLength - chunkStart;
                int readSize = (int)Math.Min(bufSize, remaining);
                int scanSize = (int)Math.Min(ChunkSize, remaining);

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
                                new ReadOnlySpan<byte>(ptr, readSize).CopyTo(buf.AsSpan(0, readSize));
                            }
                            finally
                            {
                                acc.SafeMemoryMappedViewHandle.ReleasePointer();
                            }
                        }
                    }, ct).ConfigureAwait(false);
                }

                long capturedStart = chunkStart;
                int chunkMatches = 0;

                await Task.Run(() =>
                {
                    using var scanner = new YaraxScanner(rules);
                    scanner.OnHit += (ref YaraxRuleHit hit) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        string ruleName = hit.Name;
                        string? ns = hit.Namespace;

                        foreach (var match in hit.Matches)
                        {
                            long localOffset = (long)match.Offset;
                            if (localOffset >= scanSize) continue;

                            long absOffset = capturedStart + localOffset;

                            string? id = null;
                            string raw = match.ToString() ?? "";
                            int at = raw.IndexOf(" @ ", StringComparison.Ordinal);
                            if (at > 0) id = raw[..at].Trim();

                            var ym = new YaraMatch(
                                ruleName, ns, id, absOffset,
                                BuildHexContext(buf.AsSpan(0, readSize), localOffset));

                            writer.TryWrite(ym);
                            chunkMatches++;
                        }
                    };
                    scanner.Scan(buf.AsSpan(0, readSize));
                }, ct).ConfigureAwait(false);

                localCount += chunkMatches;
                chunkStart += scanSize;

                progress?.Report(new YaraScanProgress(chunkStart, fileLength, localCount, false));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return localCount;
    }

    // this solution is used to avoid allocations and unnecessary bounds checking.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? BuildHexContext(ReadOnlySpan<byte> data, long offset)
    {
        if (offset < 0 || offset >= data.Length) return null;
        int take = (int)Math.Min(16, data.Length - offset);
        if (take <= 0) return null;

        Span<char> buf = stackalloc char[take * 3 - 1];
        ref byte src = ref MemoryMarshal.GetReference(data);
        int pos = 0;

        for (int i = 0; i < take; i++)
        {
            byte b = Unsafe.Add(ref src, (nint)(offset + i));
            int hi = b >> 4;
            int lo = b & 0xF;
            if (i > 0) buf[pos++] = ' ';
            buf[pos++] = (char)(hi < 10 ? '0' + hi : 'A' + hi - 10);
            buf[pos++] = (char)(lo < 10 ? '0' + lo : 'A' + lo - 10);
        }
        return new string(buf[..pos]);
    }

    private static async Task<byte[]> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan);
            byte[] head = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int read = fs.Read(head, 0, 65536);
                long size = fs.Length;
                var buf = new byte[read + 8];
                Buffer.BlockCopy(head, 0, buf, 0, read);
                MemoryMarshal.Write(buf.AsSpan(read), in size);
                using var sha = SHA256.Create();
                return sha.ComputeHash(buf);
            }
            finally { ArrayPool<byte>.Shared.Return(head); }
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