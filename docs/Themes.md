## Themes 

The theme system works here, allowing you to customize your interface extensively, and most importantly, it's effortless. The theme syntax is as follows: you have existing color editing areas, and you assign RGBA values ​​to each area.
For this, the program uses a .themes file format that uses an RGBA table, which is intuitive because it has Red - Green - Blue - Alpha.
Theme examples are included in the source code.
If you exit the program, you don't need to worry about saving themes, because the program creates a config file and writes there the paths for loading themes or hot keys.

---

**MainWindow.xaml.cs**

```csharp
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
```