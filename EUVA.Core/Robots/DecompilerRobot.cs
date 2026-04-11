// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EUVA.Core.Robots.Patterns;

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
        Console.WriteLine($"[WORK]   {Role,-28} # analyzing dump via PatternEngine...");
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

    private static string[] GetCategoriesForRole(RobotRole role) => role switch
    {
        RobotRole.WinApiToCppAgent      => new[] { "winapi" },
        RobotRole.PointerCastSimplifier => new[] { "casts" },
        RobotRole.MacroReconstructor    => new[] { "macros" },
        RobotRole.TypeInferenceAgent    => new[] { "type_inference" },
        RobotRole.GlobalVariableRenamer => new[] { "compiler_idioms" },
        RobotRole.IfElseStructurer      => new[] { "std_namespace", "oop_wrappers" },
        RobotRole.VerificationRelay     => Array.Empty<string>(),
        _                               => Array.Empty<string>(),
    };

    private Task DispatchByRole(MappedDumpContext ctx, CancellationToken ct) =>
        Role switch
        {
            RobotRole.VerificationRelay => RelayVerification(ctx, ct),
            _                          => ApplyPatternsAsync(ctx, ct),
        };

    private async Task ApplyPatternsAsync(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();

        var lines = ctx.ReadLines();
        var categories = GetCategoriesForRole(Role);

        if (categories.Length == 0)
        {
            return;
        }

        string rulesDir = PatternLoader.GetDefaultRulesDir();
        var rules = PatternLoader.LoadByCategories(rulesDir, categories);

        if (rules.Count == 0)
        {
            var warnColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[WARN]   {Role,-28} # no rules found in: {string.Join(", ", categories)}");
            Console.ForegroundColor = warnColor;
            return;
        }

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"[ENGINE] {Role,-28} # loaded {rules.Count} rules from [{string.Join(", ", categories)}]");
        Console.ForegroundColor = prev;

        var engine = new PatternEngine(rules);
        var result = engine.ApplyAll(lines);

        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] != lines[i])
            {
                Emit(0, i, "PATCH_LINE", $"{i}:{result[i]}");
            }
        }

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ENGINE] {Role,-28} # {engine.TotalPatches} line(s) transformed");
        Console.ForegroundColor = prev;
    }

    private async Task RelayVerification(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
    }
}
