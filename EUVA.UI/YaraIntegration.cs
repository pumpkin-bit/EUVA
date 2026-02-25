// SPDX-License-Identifier: GPL-3.0-or-later


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