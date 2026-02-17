// SPDX-License-Identifier: GPL-3.0-or-later


using EUVA.Core.Interfaces;
using EUVA.Core.Models;
using EUVA.Core.Parsers;

namespace EUVA.Core.Detectors.Samples;


public class ThemidaDetector : IDetector
{
    public string Name => "Themida/WinLicense Detector";
    public string Version => "1.0";
    public int Priority => 5;

    private static readonly string[] THEMIDA_SIGNATURES = new[]
    {
        "B8 ?? ?? ?? ?? B9 ?? ?? ?? ?? 50 51 E8",  // Themida entry
        "55 8B EC 83 C4 F0 B8 ?? ?? ?? ?? E8",     // WinLicense stub
        "E8 00 00 00 00 58 05 ?? ?? ?? ?? C3",     // VM entry
        "68 ?? ?? ?? ?? 68 ?? ?? ?? ?? E8"         // Protection call
    };

    public bool CanAnalyze(BinaryStructure structure)
    {
        return structure.Type == "Root" && structure.Name == "PE File";
    }

    public async Task<DetectionResult?> DetectAsync(ReadOnlyMemory<byte> fileData, BinaryStructure structure)
    {
        return await Task.Run(() =>
        {
            var data = fileData.Span;
            var signatures = new List<SignatureMatch>();
            double confidence = 0.0;

        
            foreach (var pattern in THEMIDA_SIGNATURES)
            {
                var matches = SignatureScanner.FindPattern(data, pattern, "Themida Signature");
                signatures.AddRange(matches);
            }

            if (signatures.Count > 0)
                confidence += 0.3;

          
            var sections = structure.FindByPath("Sections");
            if (sections != null)
            {
                var sectionNames = sections.Children.Select(c => c.Name.ToUpperInvariant()).ToList();
                
                if (sectionNames.Any(s => s.Contains(".THEMIDA") || s.Contains(".WINLICE")))
                    confidence += 0.5;

            
                if (sections.Children.Count > 8)
                    confidence += 0.1;
            }

           
            var imports = structure.FindByPath("Data Directories", "Import Directory");
            if (imports != null)
            {
             
                var importRva = imports.Children.FirstOrDefault(c => c.Name == "RVA")?.Value;
                if (importRva is uint rva && (rva == 0 || rva > 0x100000))
                    confidence += 0.2;
            }

         
            var entropy = SignatureScanner.CalculateEntropy(data);
            if (entropy > 7.5) 
                confidence += 0.3;

            if (confidence == 0.0)
                return null;

            return new DetectionResult
            {
                Name = "Themida/WinLicense",
                Version = null,
                Type = DetectionType.Protector,
                Confidence = Math.Min(confidence, 1.0),
                Signatures = signatures,
                DetectorName = Name,
                Metadata = new Dictionary<string, string>
                {
                    ["Entropy"] = entropy.ToString("F2"),
                    ["SignaturesFound"] = signatures.Count.ToString(),
                    ["Type"] = "Virtualizer + Protector"
                }
            };
        });
    }
}
