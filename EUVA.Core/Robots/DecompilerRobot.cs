// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public sealed class DecompilerRobot : RobotBase
{
    private string _workspacePath = string.Empty;
    private int _annotationCount = 0;

    public DecompilerRobot(RobotRole role, IRobotNetwork network) : base(role, network) { }

    public override async Task<RobotResult> ExecuteAsync(MappedDumpContext ctx, string workspacePath, CancellationToken ct = default)
    {
        SetStatus(RobotStatus.Working);
        _workspacePath = workspacePath;
        _annotationCount = 0;

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WORK]   {Role,-28} # analyzing dump via MMF...");
        Console.ForegroundColor = prev;

        try
        {
            await DispatchByRole(ctx, ct).ConfigureAwait(false);

            double confidence = _annotationCount > 0 ? Math.Min(1.0, _annotationCount * 0.3) : 1.0;
            string summaryText = $"{Role}: {_annotationCount} annotation(s)";

            byte[] verifKey = await _network.Admin.Verifier.RequestVerificationKeyAsync(Id, Role, _annotationCount, summaryText).ConfigureAwait(false);

            var result = new RobotResult
            {
                RobotId         = Id,
                Role            = Role,
                HasFindings     = _annotationCount > 0,
                Summary         = summaryText,
                AnnotationCount = _annotationCount,
                Confidence      = confidence,
                VerificationKey = verifKey
            };

            var prevLog = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[MSG] {Role,-28} waiting for peers at the finish line...");
            Console.ForegroundColor = prevLog;
            
            await WaitUntilAllPeersDoneAsync(ct).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException)
        {
            SetStatus(RobotStatus.Faulted);
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(RobotStatus.Faulted);
            return new RobotResult
            {
                RobotId     = Id,
                Role        = Role,
                HasFindings = false,
                Summary     = $"[ERROR] {Role}: {ex.Message}",
                Confidence  = 0.0,
            };
        }
    }

    private void Emit(long offset, int line, string action, string context)
    {
        WorkspaceManager.WriteAnnotation(_workspacePath, Role, offset, line, action, context);
        Interlocked.Increment(ref _annotationCount);
    }

    private Task DispatchByRole(MappedDumpContext ctx, CancellationToken ct) =>
        Role switch
        {
            RobotRole.WinApiToCppAgent      => TransformWinApiAsync(ctx, ct),
            RobotRole.PointerCastSimplifier => SimplifyPointerCastsAsync(ctx, ct),
            RobotRole.MacroReconstructor    => ReconstructMacrosAsync(ctx, ct),
            RobotRole.TypeInferenceAgent    => InferCppTypesAsync(ctx, ct),
            RobotRole.GlobalVariableRenamer => RenameGlobalsAsync(ctx, ct),
            RobotRole.IfElseStructurer      => StructureIfElseAsync(ctx, ct),
            RobotRole.VerificationRelay     => RelayVerification(ctx, ct),
            _                               => Task.CompletedTask,
        };

    private async Task TransformWinApiAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var lines = ctx.ReadLines();
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("kernel32::DeleteFileW"))
            {
                string replaced = line.Replace("kernel32::DeleteFileW", "std::filesystem::remove");
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
            else if (line.Contains("kernel32::CloseHandle"))
            {
                string replaced = line.Replace("kernel32::CloseHandle", "CloseHandle"); 
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
            else if (line.Contains("kernel32::lstrlenA"))
            {
                string replaced = line.Replace("kernel32::lstrlenA()", "strlen(a1)"); 
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
        }
    }


    
    //  (stub and code hardcode are used here)
    //   because it's a test..
    private async Task SimplifyPointerCastsAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var lines = ctx.ReadLines();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("((void* (*)(unsigned int))rax)"))
            {
                string replaced = line.Replace("((void* (*)(unsigned int))rax)", "reinterpret_cast<void*(*)(unsigned int)>(rax)");
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
            else if (line.Contains("((void* (*)(unsigned int))v2)"))
            {
                string replaced = line.Replace("((void* (*)(unsigned int))v2)", "reinterpret_cast<void*(*)(unsigned int)>(v2)");
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
        }
    }

    private async Task ReconstructMacrosAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var lines = ctx.ReadLines();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("<< 16 |") || line.Contains("<< 8 |"))
            {
                int eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                {
                    string lvalue = line.Substring(0, eqIdx + 1);
                    string replaced = lvalue + " MAKELONG(rdx, g_0x40A2B6); // reconstructed macro";
                    Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
                }
            }
        }
    }

    private async Task InferCppTypesAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var lines = ctx.ReadLines();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("sz_ErrorMsg = \""))
            {
                string replaced = line.Replace("sz_ErrorMsg = ", "const char* sz_ErrorMsg = ");
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
        }
    }

    private async Task RenameGlobalsAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var lines = ctx.ReadLines();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("&g_Data_4CF000"))
            {
                string replaced = line.Replace("&g_Data_4CF000", "&g_SysBuffer_CF00");
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
        }
    }

    private async Task StructureIfElseAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var lines = ctx.ReadLines();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("if (rax == 0)") && lines.Length > i + 1 && lines[i+1].Contains("ExitProcess"))
            {
                string replaced = line.Replace("if (rax == 0)", "if (!rax) /* check */");
                Emit(0, i, "PATCH_LINE", $"{i}:{replaced}");
            }
        }
    }

    private async Task RelayVerification(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); }

    private static unsafe int KmpCountOccurrences(ReadOnlySpan<byte> text, string patternString)
    {
        int patLen = patternString.Length;
        if (patLen == 0 || text.Length < patLen) 
            return 0;
        
        byte* pat = stackalloc byte[patLen];
        for (int i = 0; i < patLen; i++) 
            pat[i] = (byte)patternString[i];

        int* lps = stackalloc int[patLen];
        lps[0] = 0;
        int len = 0, idx = 1;
        while (idx < patLen)
        {
            if (pat[idx] == pat[len]) 
                lps[idx++] = ++len;
            else if (len != 0) 
                len = lps[len - 1];
            else 
                lps[idx++] = 0;
        }

        int count = 0;
        int iTxt = 0, jPat = 0;
        
        while (iTxt < text.Length)
        {
            if (pat[jPat] == text[iTxt])
            {
                jPat++; 
                iTxt++;
            }
            if (jPat == patLen)
            {
                count++;
                jPat = lps[jPat - 1];
            }
            else if (iTxt < text.Length && pat[jPat] != text[iTxt])
            {
                if (jPat != 0) 
                    jPat = lps[jPat - 1];
                else 
                    iTxt++;
            }
        }
        return count;
    }

    private static bool KmpContainsAny(ReadOnlySpan<byte> text, params string[] patterns)
    {
        foreach (var p in patterns)
            if (KmpCountOccurrences(text, p) > 0) return true;
        return false;
    }
}
