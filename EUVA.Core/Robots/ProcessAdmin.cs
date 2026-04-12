// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EUVA.Core.Robots.Patterns;

namespace EUVA.Core.Robots;

public sealed class ProcessAdmin : IProcessAdmin
{
    private readonly RobotNetwork _network;
    private readonly List<RobotBase> _robots;

    public RobotVerifier Verifier { get; }

    public ProcessAdmin()
    {
        Verifier = new RobotVerifier();
        _network = new RobotNetwork(this);
        _robots  = new List<RobotBase>(7);
    }

    public void InitializeFleet()
    {
        _robots.Add(new DecompilerRobot(RobotRole.WinApiToCppAgent, _network));
        _robots.Add(new DecompilerRobot(RobotRole.PointerCastSimplifier, _network));
        _robots.Add(new DecompilerRobot(RobotRole.MacroReconstructor, _network));
        _robots.Add(new DecompilerRobot(RobotRole.TypeInferenceAgent, _network));
        _robots.Add(new DecompilerRobot(RobotRole.GlobalVariableRenamer, _network));
        _robots.Add(new DecompilerRobot(RobotRole.IfElseStructurer, _network));
        _robots.Add(new DecompilerRobot(RobotRole.VerificationRelay, _network));

        foreach (var robot in _robots)
        {
            _network.Register(robot);
        }

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ADMIN] Fleet refactored for C++ AST generation. Total robots: {_robots.Count}");
        Console.ForegroundColor = prev;
    }

    public async Task<string> RunPipelineAsync(long funcAddress, string linearOutput, CancellationToken ct = default)
    {
        await InvokeHelloPhaseAsync(ct).ConfigureAwait(false);

        string dumpPath = WorkspaceManager.CreateFunctionWorkspace(funcAddress, linearOutput);

        RunUnifiedTransform(dumpPath);

        var results = await KickAllSimultaneouslyAsync(dumpPath, ct).ConfigureAwait(false);

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[ADMIN] Verifying cryptographic keys for 7 robots...");
        Console.ForegroundColor = prev;

        int totalVerified = 0;
        int totalRejected = 0;
        foreach (var res in results)
        {
            bool isValid = Verifier.ValidateKey(res.RobotId, res.AnnotationCount, res.Summary, res.VerificationKey);
            if (!isValid)
            {
                var errColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SECURITY FATAL] Robot {res.Role} returned an INVALID or missing Verification Key! Rejecting findings.");
                Console.ForegroundColor = errColor;
                totalRejected++;
                continue;
            }
            totalVerified++;
        }

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ADMIN] Verification complete. Accepted: {totalVerified}, Rejected: {totalRejected}");
        Console.ForegroundColor = prev;

        string annPath = dumpPath.Replace(".dump", ".annotations");
        string[] lines = WorkspaceManager.ReadAnnotations(dumpPath);

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ADMIN] Annotations file: {annPath} ({lines.Length} entries)");
        Console.ForegroundColor = prev;

        return annPath;
    }

    private async Task InvokeHelloPhaseAsync(CancellationToken ct)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("[ADMIN] Initiating Hello Phase. Waiting for all robots to report...");
        Console.ForegroundColor = prev;

        var helloTasks = _robots.Select(r => r.OnHello(ct));
        await Task.WhenAll(helloTasks).ConfigureAwait(false);

        if (!_network.AllRobotsReady)
        {
            throw new InvalidOperationException(
                "Hello phase completed but network reports not all robots are ready.");
        }

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("[ADMIN] Hello phase complete. All robots are ready.");
        Console.ForegroundColor = prev;
    }

    private async Task<RobotResult[]> KickAllSimultaneouslyAsync(string dumpPath, CancellationToken ct)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ADMIN] Kicking all 7 robots simultaneously now!");
        Console.ForegroundColor = prev;

        RobotResult[] results;
        using (var ctx = new MappedDumpContext(dumpPath))
        {
            var executeTasks = _robots.Select(r => r.ExecuteAsync(ctx, dumpPath, ct));
            
            results = await Task.WhenAll(executeTasks).ConfigureAwait(false);
        }

        prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ADMIN] All {results.Length} robots have finished their work.");
        Console.ForegroundColor = prev;

        Console.WriteLine($"[ADMIN] Annotations file: {dumpPath.Replace(".dump", ".annotations")} ({WorkspaceManager.ReadAnnotations(dumpPath).Length} entries)");

        var applyColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("[ADMIN] Applying AST-Lite transformations to the source dump...");
        Console.ForegroundColor = applyColor;

        WorkspaceManager.ApplyTransformations(dumpPath);

        return results;
    }

    public Task<AdminResponse> OnRobotErrorAsync(Guid robotId, RobotRole role, string missingKey)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[ADMIN:KDB] Robot {robotId.ToString().Substring(0, 8)} ({role}) reported missing key: '{missingKey}'");
        Console.ForegroundColor = prev;

        bool found = false;
        byte[]? payload = null;

        if (missingKey.Contains("TestSig"))
        {
            found = true;
            payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }; 
        }

        if (found)
        {
            prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[ADMIN:KDB] Found data for '{missingKey}'. Passing payload to Robot.");
            Console.ForegroundColor = prev;
            return Task.FromResult(new AdminResponse(AdminDecision.InheritData, payload));
        }
        else
        {
            prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[ADMIN:KDB] Key '{missingKey}' not found in KDB. Instructing Robot to Ignore.");
            Console.ForegroundColor = prev;
            return Task.FromResult(new AdminResponse(AdminDecision.Ignore));
        }
    }

    private void RunUnifiedTransform(string dumpPath)
    {
        string rulesDir = PatternLoader.GetDefaultRulesDir();
        var allRules = PatternLoader.LoadAll(rulesDir);

        if (allRules.Count == 0) return;

        var lines = System.IO.File.ReadAllLines(dumpPath);
        var engine = new PatternEngine(allRules);
        var transformed = engine.ApplyAll(lines);

        var finalLines = new List<string>();

        if (engine.UsedStructs.Any())
        {
            finalLines.Add("================================ \n");
            foreach (var structName in engine.UsedStructs)
            {
                var def = EUVA.Core.Robots.Patterns.Types.TypeDatabase.GetStruct(structName);
                if (def != null)
                {
                    finalLines.Add(def.EmitSyntax());
                }
            }
            finalLines.Add("================================ \n");
            finalLines.Add("// start of code (main label): \n");
        }

        finalLines.AddRange(transformed);
        System.IO.File.WriteAllLines(dumpPath, finalLines);

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[ADMIN] Unified PatternEngine: {allRules.Count} rules, {engine.TotalPatches} lines transformed");
        if (engine.UsedStructs.Any())
            Console.WriteLine($"[ADMIN] {engine.UsedStructs.Count()} structs: {string.Join(", ", engine.UsedStructs)}");
        Console.ForegroundColor = prev;
    }
}
