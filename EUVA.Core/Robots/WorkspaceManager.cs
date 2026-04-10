// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;

namespace EUVA.Core.Robots;

public static class WorkspaceManager
{
    public static string DumpsDirectory { get; }

    static WorkspaceManager()
    {
        DumpsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps");
    }

    public static string CreateFunctionWorkspace(long funcAddress, string code)
    {
        if (!Directory.Exists(DumpsDirectory))
        {
            Directory.CreateDirectory(DumpsDirectory);
        }
        else
        {
            foreach (var file in Directory.GetFiles(DumpsDirectory))
            {
                try { File.Delete(file); } catch {  }
            }
        }

        string dumpPath = Path.Combine(DumpsDirectory, $"func_{funcAddress:X}.dump");
        File.WriteAllText(dumpPath, code);

        string annPath = Path.Combine(DumpsDirectory, $"func_{funcAddress:X}.annotations");
        File.WriteAllText(annPath, ""); 

        return dumpPath; 
    }

    public static void WriteAnnotation(string dumpPath, RobotRole role, long offset, int line, string action, string context)
    {
        string annPath = dumpPath.Replace(".dump", ".annotations");
        string entry = $"{role}|0x{offset:X8}|L{line}|{action}|{context}";
        
        lock (string.Intern(annPath)) 
        {
            File.AppendAllText(annPath, entry + Environment.NewLine);
        }
    }

    public static string[] ReadAnnotations(string dumpPath)
    {
        string annPath = dumpPath.Replace(".dump", ".annotations");
        if (!File.Exists(annPath)) return [];
        
        lock (string.Intern(annPath))
        {
            return File.ReadAllLines(annPath);
        }
    }

    public static void PurgeAllDumps()
    {
        if (Directory.Exists(DumpsDirectory))
        {
            try { Directory.Delete(DumpsDirectory, true); } catch { }
        }
    }
}
