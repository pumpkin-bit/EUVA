// SPDX-License-Identifier: GPL-3.0-or-later


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