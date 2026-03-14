// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace EUVA.UI.Theming;

public sealed class ThemeManager
{

    private static ThemeManager? _instance;
    public static ThemeManager Instance => _instance ??= new ThemeManager();
    private ThemeManager() { }

    private static readonly Regex _lineRegex = new(
        @"^[\w_]+\s*=\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);


    public static readonly IReadOnlyDictionary<string, (byte R, byte G, byte B, byte A)> DefaultTheme =
        new Dictionary<string, (byte, byte, byte, byte)>(StringComparer.Ordinal)
        {
           
            { "Background",             ( 30,  30,  46, 255) }, 
            { "Sidebar",                ( 24,  24,  37, 255) }, 
            { "Toolbar",                ( 30,  30,  46, 255) }, 
            { "Border",                 ( 49,  50,  68, 255) }, 
            { "SeparatorLine",          ( 69,  71,  90, 255) }, 
            
            { "MenuBackground",         ( 30,  30,  46, 255) }, 
            { "MenuForeground",         (205, 214, 244, 255) }, 
            { "MenuHighlight",          ( 49,  50,  68, 255) }, 
            
            { "ForegroundPrimary",      (205, 214, 244, 255) }, 
            { "ForegroundSecondary",    (166, 173, 200, 255) }, 
            { "ForegroundDisabled",     (108, 112, 134, 255) }, 
            
            { "Hex_Background",         ( 30,  30,  46, 255) }, 
            { "Hex_OffsetForeground",   (108, 112, 134, 255) }, 
            { "HexOffset",              (108, 112, 134, 255) }, 
            { "Hex_ByteActive",         (205, 214, 244, 255) }, 
            { "Hex_ByteNull",           ( 69,  71,  90, 255) }, 
            { "Hex_ByteSelected",       ( 30,  30,  46, 255) }, 
            { "Hex_ByteHighlight",      (203, 166, 247,  96) }, 
            { "Hex_AsciiPrintable",     (166, 227, 161, 255) }, 
            { "Hex_AsciiNonPrintable",  ( 69,  71,  90, 255) }, 
            
            { "TreeBackground",         ( 24,  24,  37, 255) }, 
            { "TreeText",               (205, 214, 244, 255) }, 
            { "TreeItemHighlight",      ( 49,  50,  68, 200) }, 
            { "TreeIconSection",        (137, 180, 250, 255) }, 
            { "TreeIconField",          (148, 226, 213, 255) }, 
            
            { "PropertyBackground",     ( 24,  24,  37, 255) }, 
            { "PropertyKey",            (180, 190, 254, 255) }, 
            { "PropertyValue",          (250, 179, 135, 255) }, 
            
            { "ConsoleBackground",      ( 24,  24,  37, 255) },
            { "ConsoleForeground",      (205, 214, 244, 255) }, 
            { "ConsoleError",           (243, 139, 168, 255) }, 
            { "ConsoleSuccess",         (166, 227, 161, 255) }, 
        };


    public void ApplyDefaultTheme()
    {
        foreach (var kvp in DefaultTheme)
        {
            var (r, g, b, a) = kvp.Value;
            InjectResource(kvp.Key, Color.FromArgb(a, r, g, b));
        }
        ThemeDiagnostics.Info("Default theme applied.");
    }


    public void LoadTheme(string path)
    {
        ThemeDiagnostics.BeginParse(path);

        int lineIndex = 0;
        int loaded = 0;
        int total = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            lineIndex++;


            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            total++;


            var match = _lineRegex.Match(line);
            if (!match.Success)
            {
                ThemeDiagnostics.ErrorInvalidSyntax(lineIndex, line);
                continue;
            }


            string[] channelStrings = {
                match.Groups[1].Value, match.Groups[2].Value,
                match.Groups[3].Value, match.Groups[4].Value
            };

            bool rangeOk = true;
            byte[] channels = new byte[4];

            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(channelStrings[i], out int val) || val > 255)
                {
                    ThemeDiagnostics.ErrorValueOutOfRange(lineIndex, channelStrings[i]);
                    rangeOk = false;
                    break;
                }
                channels[i] = (byte)val;
            }

            if (!rangeOk)
                continue;


            var key = line[..line.IndexOf('=')].Trim();

            InjectResource(key, Color.FromArgb(channels[3], channels[0], channels[1], channels[2]));
            loaded++;
        }

        ThemeDiagnostics.ThemeApplied(loaded, total);
    }


    public void SaveThemePath(string path)
    {
        EuvaSettings.Default.LastThemePath = path;
        EuvaSettings.Default.Save();
    }

    public void ResetToDefault()
    {
        EuvaSettings.Default.LastThemePath = string.Empty;
        EuvaSettings.Default.Save();
        ApplyDefaultTheme();
        ThemeDiagnostics.Info("Theme reset to built-in default.");
    }


    private static void InjectResource(string key, Color color)
    {
        var resources = Application.Current.Resources;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key + "_Color"] = color;
        resources[key] = brush;
    }



    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx < 0 ? line : line[..idx];
    }
}
