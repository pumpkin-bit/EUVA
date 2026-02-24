### Memory-Mapped File 

EUVA does not load files into `byte[]` arrays. It maps them directly through the OS virtual memory system via `System.IO.MemoryMappedFiles`. This is a fundamental architectural choice, not an optimization.

An example of how to open a file in memory and manage access to it, as well as multithreaded security. The MMF mechanism is controlled by the operating system, which allows the file to be mapped to the process's virtual address rather than using RAM. If a byte[] array is used, the program will not be able to open or overwrite large files.

Dynamic file loading is also implemented, and all content is not processed instantly; it is updated when scrolling through the file, also via MMF.

---

**VirtualizedHexView.cs**


```csharp
    public void LoadFile(string filePath)
    {
        _accessorLock.EnterWriteLock();
        try
        {
            _accessor?.Dispose();
            _mmf?.Dispose();
            if (!File.Exists(filePath)) return;

            _selectionStart = _selectionEnd = _selectedOffset = -1;
            lock (_modLock) _modifiedOffsets.Clear();
            _modifiedSnapshot = new HashSet<long>();

            _fileLength = new FileInfo(filePath).Length;
            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
                MemoryMappedFileAccess.ReadWrite);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        }
        finally { _accessorLock.ExitWriteLock(); }

        _currentScrollLine = 0;
        RequestFullRedraw();
    }

    public void Dispose()
    {
        _accessorLock.EnterWriteLock();
        try { _accessor?.Dispose(); _mmf?.Dispose(); }
        finally { _accessorLock.ExitWriteLock(); }
        _accessorLock.Dispose();
    }

    public void Save() => _accessor?.Flush();
    public MemoryMappedFile? GetMemoryMappedFile()
    {
        _accessorLock.EnterReadLock();
        try { return _mmf; }
        finally { _accessorLock.ExitReadLock(); }
    }
```
---

**YaraIntegration.cs**

Data is copied virtually by the operating system until it is accessed, after which physical copying occurs. There are also moments when working through unsafe pointers, specifically because this is one of the fastest ways to drag data into the working buffer.

Sample:

```csharp

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
```

---

**YaraMainWindowPartial.cs**

Here, an existing MMF object is taken from hexview, and chunks are read, as well as basic conditions to ensure that the channel is not closed, i.e., access to the MMF is not blocked.

Sample:

```csharp
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
```