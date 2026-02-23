// SPDX-License-Identifier: GPL-3.0-or-later
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
