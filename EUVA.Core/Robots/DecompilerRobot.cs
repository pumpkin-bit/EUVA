// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public sealed class DecompilerRobot : RobotBase
{
    public DecompilerRobot(RobotRole role, IRobotNetwork network) : base(role, network) { }

    public override async Task<RobotResult> ExecuteAsync(MappedDumpContext ctx, string workspacePath, CancellationToken ct = default)
    {
        SetStatus(RobotStatus.Working);

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WORK]   {Role,-28} # analyzing dump via MMF...");
        Console.ForegroundColor = prev;

        try
        {
            List<RobotAnnotation> annotations;

            annotations = await DispatchByRole(ctx, ct).ConfigureAwait(false);

            foreach (var ann in annotations)
            {
                WorkspaceManager.AppendAnnotation(workspacePath, $"[{Role}] {ann.Description}");
            }

            double confidence = annotations.Count > 0 ? ComputeConfidence(annotations) : 1.0;

            var result = new RobotResult
            {
                RobotId     = Id,
                Role        = Role,
                HasFindings = annotations.Count > 0,
                Summary     = BuildSummary(annotations),
                Annotations = annotations,
                Confidence  = confidence,
            };

            SetStatus(RobotStatus.Done);
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

    private Task<List<RobotAnnotation>> DispatchByRole(MappedDumpContext ctx, CancellationToken ct) =>
        Role switch
        {
            RobotRole.YaraScanner              => ScanYaraPatterns(ctx, ct),
            RobotRole.HexSignatureMatcher      => MatchHexSignatures(ctx, ct),
            RobotRole.BinaryPatternAnalyzer    => AnalyzeBinaryPatterns(ctx, ct),
            RobotRole.ApiChainTracer           => TraceApiChains(ctx, ct),
            RobotRole.MetadataExtractor        => ExtractMetadata(ctx, ct),
            RobotRole.IrLifterAgent            => AnnotateIrLifting(ctx, ct),
            RobotRole.ControlFlowAnalyzer      => AnalyzeControlFlow(ctx, ct),
            RobotRole.DataFlowAnalyzer         => AnalyzeDataFlow(ctx, ct),
            RobotRole.TypeInferenceAgent       => InferTypes(ctx, ct),
            RobotRole.CallingConventionAgent   => AnalyzeCallingConventions(ctx, ct),
            RobotRole.StringExtractor          => ExtractStrings(ctx, ct),
            RobotRole.EntropyAnalyzer          => AnalyzeEntropy(ctx, ct),
            RobotRole.ImportTracer             => TraceImports(ctx, ct),
            RobotRole.ExportTracer             => TraceExports(ctx, ct),
            RobotRole.SsaTransformer           => AnnotateSsa(ctx, ct),
            RobotRole.LoopDetectionAgent       => DetectLoops(ctx, ct),
            RobotRole.SwitchDetectionAgent     => DetectSwitches(ctx, ct),
            RobotRole.StructReconstructor      => ReconstructStructs(ctx, ct),
            RobotRole.VTableDetectionAgent     => DetectVTables(ctx, ct),
            RobotRole.IdiomRecognizer          => RecognizeIdioms(ctx, ct),
            RobotRole.DeadCodeAgent            => EliminateDeadCode(ctx, ct),
            RobotRole.ConstantPropagationAgent => PropagateConstants(ctx, ct),
            RobotRole.ExpressionSimplifier     => SimplifyExpressions(ctx, ct),
            RobotRole.SemanticGuesser          => GuessSemantics(ctx, ct),
            RobotRole.FingerprintAgent         => MatchFingerprints(ctx, ct),
            RobotRole.PseudocodeEmitter        => EnhancePseudocode(ctx, ct),
            RobotRole.NamingAgent              => ApplyNaming(ctx, ct),
            RobotRole.XrefAnalyzer             => AnalyzeXrefs(ctx, ct),
            RobotRole.WeightChainValidator     => ValidateWeightChain(ctx, ct),
            RobotRole.VerificationRelay        => RelayVerification(ctx, ct),
            _                                  => Task.FromResult(new List<RobotAnnotation>()),
        };

    private async Task<List<RobotAnnotation>> ScanYaraPatterns(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();

        //debug
        string missingKey = "TestSig_01";
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ROBOT:ERR] {Role} missing YARA signature: '{missingKey}'. Requesting Admin help...");
        Console.ForegroundColor = prev;

        AdminResponse response = await RequestAdminHelpAsync(missingKey, ct);

        if (response.Decision == AdminDecision.InheritData && response.Payload != null)
        {
            prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[ROBOT:ACK] {Role} inherited payload of {response.Payload.Length} bytes. Processing...");
            Console.ForegroundColor = prev;
            annotations.Add(Annotate($"Inherited KDB YARA payload for {missingKey}"));
        }
        else
        {
            prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[ROBOT:ACK] {Role} was instructed to Ignore missing '{missingKey}'. Skipping.");
            Console.ForegroundColor = prev;
        }

        return annotations;
    }
    private async Task<List<RobotAnnotation>> MatchHexSignatures(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> AnalyzeBinaryPatterns(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }

    private async Task<List<RobotAnnotation>> TraceApiChains(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();

        ctx.RunScoped(span => 
        {
            if (KmpContainsAny(span, "CreateFile", "ReadFile", "WriteFile", "CloseHandle"))
                annotations.Add(Annotate("File IO API chain detected CreateFile / ReadFile / WriteFile."));

            if (KmpContainsAny(span, "VirtualAlloc", "VirtualProtect", "VirtualFree"))
                annotations.Add(Annotate("Memory API chain detected — possible injected code or shellcode."));

            if (KmpContainsAny(span, "CreateThread", "OpenThread", "ResumeThread"))
                annotations.Add(Annotate("Thread management API chain detected."));

            if (KmpContainsAny(span, "RegOpenKey", "RegSetValue", "RegQueryValue"))
                annotations.Add(Annotate("Registry API chain detected — persistence mechanism likely."));

            if (KmpContainsAny(span, "WSAStartup", "connect(", "send(", "recv("))
                annotations.Add(Annotate("Network API chain detected Winsock."));
        });

        return annotations;
    }

    private async Task<List<RobotAnnotation>> ExtractMetadata(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> AnnotateIrLifting(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> AnalyzeControlFlow(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> AnalyzeDataFlow(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }

    private async Task<List<RobotAnnotation>> InferTypes(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();

        ctx.RunScoped(span => 
        {
            if (KmpContainsAny(span, "int ", "unsigned int"))
                annotations.Add(Annotate("Suggest promoting 'int' / 'unsigned int' to std::int32_t / std::uint32_t for C++ output.",
                    replacementCode: "// Type promotion: int > std::int32_t"));
        });

        return annotations;
    }

    private async Task<List<RobotAnnotation>> AnalyzeCallingConventions(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }

    private async Task<List<RobotAnnotation>> ExtractStrings(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();

        ctx.RunScoped(span => 
        {
            int stringCount = KmpCountOccurrences(span, "\"");
            if (stringCount > 0)
                annotations.Add(Annotate($"Detected {stringCount / 2} string literals in linear output."));
        });

        return annotations;
    }

    private async Task<List<RobotAnnotation>> AnalyzeEntropy(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> TraceImports(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> TraceExports(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> AnnotateSsa(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }

    private async Task<List<RobotAnnotation>> DetectLoops(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();
        
        ctx.RunScoped(span => 
        {
            int forCount   = KmpCountOccurrences(span, "for (");
            int whileCount = KmpCountOccurrences(span, "while (");
            int doCount    = KmpCountOccurrences(span, "do {");

            if (forCount + whileCount + doCount > 0)
                annotations.Add(Annotate(
                    $"Detected {forCount} for-loop(s), {whileCount} while-loop(s), {doCount} do-while(s)."));
        });

        return annotations;
    }

    private async Task<List<RobotAnnotation>> DetectSwitches(MappedDumpContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();
        
        ctx.RunScoped(span => 
        {
            int switchCount = KmpCountOccurrences(span, "switch (");
            if (switchCount > 0)
                annotations.Add(Annotate($"Detected {switchCount} switch statement(s)."));
        });

        return annotations;
    }

    private async Task<List<RobotAnnotation>> ReconstructStructs(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> DetectVTables(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> RecognizeIdioms(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> EliminateDeadCode(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> PropagateConstants(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> SimplifyExpressions(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> GuessSemantics(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> MatchFingerprints(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> EnhancePseudocode(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> ApplyNaming(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> AnalyzeXrefs(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> ValidateWeightChain(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }
    private async Task<List<RobotAnnotation>> RelayVerification(MappedDumpContext ctx, CancellationToken ct) { await Task.Yield(); return []; }

    private RobotAnnotation Annotate(string description, string? replacementCode = null) =>
        new()
        {
            Category        = Role,
            Location        = string.Empty,
            Description     = description,
            ReplacementCode = replacementCode,
        };

    private static string BuildSummary(List<RobotAnnotation> annotations) =>
        annotations.Count == 0 ? "No findings." : string.Join(" | ", annotations.ConvertAll(a => a.Description));

    private static double ComputeConfidence(List<RobotAnnotation> annotations) =>
        System.Math.Min(1.0, annotations.Count * 0.3);

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
