// SPDX-License-Identifier: GPL-3.0-or-later


using System.Windows;
using EUVA.UI.Theming;

namespace EUVA.UI;

public partial class App : Application
{   
    protected override void OnStartup(StartupEventArgs e)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        base.OnStartup(e);
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show($"Unhandled exception: {ex.Exception.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        try
        {
            var tm   = ThemeManager.Instance;
            var path = EuvaSettings.Default.LastThemePath;

            if (string.IsNullOrWhiteSpace(path))
            {
                tm.ApplyDefaultTheme();
            }
            else if (!System.IO.File.Exists(path))
            {
                ThemeDiagnostics.Warning(
                    $"Last used theme not found: {path}. Reverting to Default.");
                tm.ApplyDefaultTheme();
            }
            else
            {
                tm.LoadTheme(path);
            }
        }
        catch (Exception ex)
        {
            ThemeDiagnostics.Error($"Theme init failed: {ex.Message}. Using defaults.");
            ThemeManager.Instance.ApplyDefaultTheme();
        }
    }
}
