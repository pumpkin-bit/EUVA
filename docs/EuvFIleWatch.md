## EuvFileWatch

The script watcher implementation is quite simple.

When a script file is selected, the program remembers the file path, replaces the item name to create a file watch label, and runs the FileSystemWatcher method.
File watcher is also implemented via StartScriptWatcher, which monitors file saves, file size, name changes, or file re-creations. I also don't start the engine immediately after any of the above-mentioned file events occur, so that file lock errors don't occur and the program can correctly access the file without the risk of lock errors. The automatic parsing cycle starts after a cooldown, and the engine calls RunParallelEngine Afterwards, the patch is applied and the result is displayed.

---

**MainWindow.xaml.cs**




```csharp
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
```
