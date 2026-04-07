// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EUVA.Core.Robots;

public sealed class DecompilerRobot : RobotBase
{
    public DecompilerRobot(RobotRole role, IRobotNetwork network) : base(role, network) { }

    public override async Task<RobotResult> ExecuteAsync(string linearOutput, CancellationToken ct = default)
    {
        SetStatus(RobotStatus.Working);

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WORK]   {Role,-28} # analyzing {linearOutput.Length} chars...");
        Console.ForegroundColor = prev;

        try
        {
            var annotations = await DispatchByRole(linearOutput, ct).ConfigureAwait(false);

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

  

    private Task<List<RobotAnnotation>> DispatchByRole(string input, CancellationToken ct) =>
        Role switch
        {
            RobotRole.YaraScanner              => ScanYaraPatterns(input, ct),
            RobotRole.HexSignatureMatcher      => MatchHexSignatures(input, ct),
            RobotRole.BinaryPatternAnalyzer    => AnalyzeBinaryPatterns(input, ct),
            RobotRole.ApiChainTracer           => TraceApiChains(input, ct),
            RobotRole.MetadataExtractor        => ExtractMetadata(input, ct),
            RobotRole.IrLifterAgent            => AnnotateIrLifting(input, ct),
            RobotRole.ControlFlowAnalyzer      => AnalyzeControlFlow(input, ct),
            RobotRole.DataFlowAnalyzer         => AnalyzeDataFlow(input, ct),
            RobotRole.TypeInferenceAgent       => InferTypes(input, ct),
            RobotRole.CallingConventionAgent   => AnalyzeCallingConventions(input, ct),
            RobotRole.StringExtractor          => ExtractStrings(input, ct),
            RobotRole.EntropyAnalyzer          => AnalyzeEntropy(input, ct),
            RobotRole.ImportTracer             => TraceImports(input, ct),
            RobotRole.ExportTracer             => TraceExports(input, ct),
            RobotRole.SsaTransformer           => AnnotateSsa(input, ct),
            RobotRole.LoopDetectionAgent       => DetectLoops(input, ct),
            RobotRole.SwitchDetectionAgent     => DetectSwitches(input, ct),
            RobotRole.StructReconstructor      => ReconstructStructs(input, ct),
            RobotRole.VTableDetectionAgent     => DetectVTables(input, ct),
            RobotRole.IdiomRecognizer          => RecognizeIdioms(input, ct),
            RobotRole.DeadCodeAgent            => EliminateDeadCode(input, ct),
            RobotRole.ConstantPropagationAgent => PropagateConstants(input, ct),
            RobotRole.ExpressionSimplifier     => SimplifyExpressions(input, ct),
            RobotRole.SemanticGuesser          => GuessSemantics(input, ct),
            RobotRole.FingerprintAgent         => MatchFingerprints(input, ct),
            RobotRole.PseudocodeEmitter        => EnhancePseudocode(input, ct),
            RobotRole.NamingAgent              => ApplyNaming(input, ct),
            RobotRole.XrefAnalyzer             => AnalyzeXrefs(input, ct),
            RobotRole.WeightChainValidator     => ValidateWeightChain(input, ct),
            RobotRole.VerificationRelay        => RelayVerification(input, ct),
            _                                  => Task.FromResult(new List<RobotAnnotation>()),
        };

    

    private async Task<List<RobotAnnotation>> ScanYaraPatterns(string input, CancellationToken ct)
    {
        await Task.Yield();
     
        return [];
    }

    private async Task<List<RobotAnnotation>> MatchHexSignatures(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> AnalyzeBinaryPatterns(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> TraceApiChains(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        var annotations = new List<RobotAnnotation>();

        if (ContainsAny(input, "CreateFile", "ReadFile", "WriteFile", "CloseHandle"))
            annotations.Add(Annotate("File IO API chain detected CreateFile / ReadFile / WriteFile."));

        if (ContainsAny(input, "VirtualAlloc", "VirtualProtect", "VirtualFree"))
            annotations.Add(Annotate("Memory API chain detected — possible injected code or shellcode."));

        if (ContainsAny(input, "CreateThread", "OpenThread", "ResumeThread"))
            annotations.Add(Annotate("Thread management API chain detected."));

        if (ContainsAny(input, "RegOpenKey", "RegSetValue", "RegQueryValue"))
            annotations.Add(Annotate("Registry API chain detected — persistence mechanism likely."));

        if (ContainsAny(input, "WSAStartup", "connect(", "send(", "recv("))
            annotations.Add(Annotate("Network API chain detected Winsock."));

        return annotations;
    }

    private async Task<List<RobotAnnotation>> ExtractMetadata(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

  

    private async Task<List<RobotAnnotation>> AnnotateIrLifting(string input, CancellationToken ct)
    {
        await Task.Yield();
    
        return [];
    }

    private async Task<List<RobotAnnotation>> AnalyzeControlFlow(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> AnalyzeDataFlow(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> InferTypes(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        var annotations = new List<RobotAnnotation>();

        if (input.Contains("int ") || input.Contains("unsigned int"))
            annotations.Add(Annotate("Suggest promoting 'int' / 'unsigned int' to std::int32_t / std::uint32_t for C++ output.",
                replacementCode: "// Type promotion: int > std::int32_t"));

        return annotations;
    }

    private async Task<List<RobotAnnotation>> AnalyzeCallingConventions(string input, CancellationToken ct)
    {
        await Task.Yield();
     
        return [];
    }



    private async Task<List<RobotAnnotation>> ExtractStrings(string input, CancellationToken ct)
    {
        await Task.Yield();

        var annotations = new List<RobotAnnotation>();
        int stringCount = CountOccurrences(input, "\"");
        if (stringCount > 0)
            annotations.Add(Annotate($"Detected {stringCount / 2} string literals in linear output."));
        return annotations;
    }

    private async Task<List<RobotAnnotation>> AnalyzeEntropy(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> TraceImports(string input, CancellationToken ct)
    {
        await Task.Yield();
     
        return [];
    }

    private async Task<List<RobotAnnotation>> TraceExports(string input, CancellationToken ct)
    {
        await Task.Yield();
       
        return [];
    }

    private async Task<List<RobotAnnotation>> AnnotateSsa(string input, CancellationToken ct)
    {
        await Task.Yield();
    
        return [];
    }


    private async Task<List<RobotAnnotation>> DetectLoops(string input, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();
        int forCount   = CountOccurrences(input, "for (");
        int whileCount = CountOccurrences(input, "while (");
        int doCount    = CountOccurrences(input, "do {");

        if (forCount + whileCount + doCount > 0)
            annotations.Add(Annotate(
                $"Detected {forCount} for-loop(s), {whileCount} while-loop(s), {doCount} do-while(s)."));

        return annotations;
    }

    private async Task<List<RobotAnnotation>> DetectSwitches(string input, CancellationToken ct)
    {
        await Task.Yield();
        var annotations = new List<RobotAnnotation>();
        int switchCount = CountOccurrences(input, "switch (");
        if (switchCount > 0)
            annotations.Add(Annotate($"Detected {switchCount} switch statement(s)."));
        return annotations;
    }

    private async Task<List<RobotAnnotation>> ReconstructStructs(string input, CancellationToken ct)
    {
        await Task.Yield();
       
        return [];
    }

    private async Task<List<RobotAnnotation>> DetectVTables(string input, CancellationToken ct)
    {
        await Task.Yield();
       
        return [];
    }

    private async Task<List<RobotAnnotation>> RecognizeIdioms(string input, CancellationToken ct)
    {
        await Task.Yield();
        
        return [];
    }

    private async Task<List<RobotAnnotation>> EliminateDeadCode(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> PropagateConstants(string input, CancellationToken ct)
    {
        await Task.Yield();
       
        return [];
    }

    private async Task<List<RobotAnnotation>> SimplifyExpressions(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

  

    private async Task<List<RobotAnnotation>> GuessSemantics(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> MatchFingerprints(string input, CancellationToken ct)
    {
        await Task.Yield();

        return [];
    }

    private async Task<List<RobotAnnotation>> EnhancePseudocode(string input, CancellationToken ct)
    {
        await Task.Yield();
       
        return [];
    }

    private async Task<List<RobotAnnotation>> ApplyNaming(string input, CancellationToken ct)
    {
        await Task.Yield();

        return [];
    }

  

    private async Task<List<RobotAnnotation>> AnalyzeXrefs(string input, CancellationToken ct)
    {
        await Task.Yield();
        
        return [];
    }

    private async Task<List<RobotAnnotation>> ValidateWeightChain(string input, CancellationToken ct)
    {
        await Task.Yield();
      
        return [];
    }

    private async Task<List<RobotAnnotation>> RelayVerification(string input, CancellationToken ct)
    {
        await Task.Yield();

        return [];
    }

    private RobotAnnotation Annotate(string description, string? replacementCode = null) =>
        new()
        {
            Category        = Role,
            Location        = string.Empty,
            Description     = description,
            ReplacementCode = replacementCode,
        };

    private static bool ContainsAny(string text, params string[] patterns) =>
        patterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string BuildSummary(List<RobotAnnotation> annotations) =>
        annotations.Count == 0
            ? "No findings."
            : string.Join(" | ", annotations.Select(a => a.Description));

    private static double ComputeConfidence(List<RobotAnnotation> annotations) =>
        Math.Min(1.0, annotations.Count * 0.3);
}
