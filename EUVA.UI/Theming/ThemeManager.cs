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
            // General shell
            { "Background",             ( 30,  30,  30, 255) },
            { "Sidebar",                ( 37,  37,  38, 255) },
            { "Toolbar",                ( 45,  45,  48, 255) },
            { "Border",                 ( 62,  62,  66, 255) },
            { "SeparatorLine",          ( 62,  62,  66, 255) },
            // Menus
            { "MenuBackground",         ( 45,  45,  48, 255) },
            { "MenuForeground",         (220, 220, 220, 255) },
            { "MenuHighlight",          ( 62,  62,  66, 255) },
            // Text
            { "ForegroundPrimary",      (220, 220, 220, 255) },
            { "ForegroundSecondary",    (153, 153, 153, 255) },
            { "ForegroundDisabled",     (100, 100, 100, 255) },
            // Hex view
            { "Hex_Background",         ( 30,  30,  30, 255) },
            { "Hex_OffsetForeground",   (160, 160, 160, 255) },
            { "HexOffset",              (160, 160, 160, 255) },
            { "Hex_ByteActive",         (173, 216, 230, 255) },  // LightBlue
            { "Hex_ByteNull",           ( 80,  80,  80, 255) },  // dim grey
            { "Hex_ByteSelected",       (255, 255,   0, 255) },  // Yellow
            { "Hex_ByteHighlight",      ( 78, 201, 176,  80) },  // teal tint
            { "Hex_AsciiPrintable",     (144, 238, 144, 255) },  // LightGreen
            { "Hex_AsciiNonPrintable",  (100, 100, 100, 255) },
            // Structure tree
            { "TreeBackground",         ( 37,  37,  38, 255) },
            { "TreeText",               (220, 220, 220, 255) },
            { "TreeItemHighlight",      ( 62,  62,  66, 200) },
            { "TreeIconSection",        ( 86, 156, 214, 255) },
            { "TreeIconField",          ( 78, 201, 176, 255) },
            // Property grid
            { "PropertyBackground",     ( 45,  45,  48, 255) },
            { "PropertyKey",            (156, 220, 254, 255) },
            { "PropertyValue",          (206, 145, 120, 255) },
            // Console
            { "ConsoleBackground",      ( 30,  30,  30, 255) },
            { "ConsoleForeground",      (220, 220, 220, 255) },
            { "ConsoleError",           (244,  71,  71, 255) },
            { "ConsoleSuccess",         (106, 153,  85, 255) },
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
        int loaded    = 0;
        int total     = 0;

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
        resources[key]            = brush;
    }

  

    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx < 0 ? line : line[..idx];
    }
}
