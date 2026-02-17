// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace EUVA.UI.Theming;



internal static class ThemeDiagnostics
{
   

    public static void Warning(string message) =>
        Write("WARNING", message, ConsoleColor.Yellow);

    public static void Error(string message) =>
        Write("ERROR", message, ConsoleColor.Red);

    public static void Info(string message) =>
        Write("THEME ENGINE", message, ConsoleColor.Cyan);

    public static void Success(string message) =>
        Write("SUCCESS", message, ConsoleColor.Green);

    
    public static void ThemeFileNotFound(string path) =>
        Warning($"Theme file not found: {path}. Using internal fallback.");

  
    public static void BeginParse(string filePath) =>
        Info($"Parsing '{System.IO.Path.GetFileName(filePath)}'...");

    
    public static void ErrorValueOutOfRange(int lineIndex, string val) =>
        Error($"Line {lineIndex}: Value '{val}' out of range [0-255]. Skipping.");

 
    public static void ErrorInvalidSyntax(int lineIndex, string rawLine) =>
        Error($"Line {lineIndex}: Invalid syntax '{rawLine}'. Skipping.");

   
    public static void ThemeApplied(int loaded, int total) =>
        Success($"Theme applied. {loaded}/{total} tokens loaded.");

  
    private static void Write(string tag, string message, ConsoleColor color)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timestamp}] [{tag}] {message}";

        Debug.WriteLine(line);

        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(line);
        }
        catch (System.IO.IOException) { /* no console attached */ }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }
}
