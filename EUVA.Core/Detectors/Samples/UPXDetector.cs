// SPDX-License-Identifier: GPL-3.0-or-later


using System.Runtime.InteropServices;
using EUVA.Core.Interfaces;
using EUVA.Core.Models;
using EUVA.Core.Parsers;

namespace EUVA.Core.Detectors.Samples;


public class UPXDetector : IDetector
{
    public string Name => "UPX Detector";
    public string Version => "1.0";
    public int Priority => 10;
    private const double SignatureConfidence = 0.4;
    private const double Upx0SectionConfidence = 0.4;
    private const double DotUpxSectionConfidence = 0.3;
    private const double EntropyConfidence = 0.2;
    private const double EntropyThreshold = 7.0;
    

    private static readonly string[] UPX_SIGNATURES = new[]
    {
        "55 50 58 30",  // UPX0
        "55 50 58 31",  // UPX1
        "55 50 58 21",  // UPX!
        "60 BE ?? ?? ?? ?? 8D BE ?? ?? ?? ??",  // UPX entry stub
        "60 E8 00 00 00 00 58 83 E8 3D"  // UPX decompressor
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


            foreach (var pattern in UPX_SIGNATURES)
            {
                var matches = SignatureScanner.FindPattern(data, pattern, $"UPX Signature");
                signatures.AddRange(matches);
            }

            if (signatures.Count > 0)
                confidence += SignatureConfidence;


            var sections = structure.FindByPath("Sections");
            if (sections != null)
            {
                var sectionNames = sections.Children.Select(c => c.Name.ToUpperInvariant()).ToList();

                if (sectionNames.Contains("UPX0") || sectionNames.Contains("UPX1"))
                    confidence += Upx0SectionConfidence;

                if (sectionNames.Contains(".UPX0") || sectionNames.Contains(".UPX1"))
                    confidence += DotUpxSectionConfidence;
            }


            var entropy = SignatureScanner.CalculateEntropy(data);
            if (entropy > EntropyThreshold)   
                confidence += EntropyConfidence;

            if (confidence == 0.0)
                return null;


            string? version = null;
            if (signatures.Any(s => s.Pattern.Contains("55 50 58 21")))
                version = "3.x+";

            return new DetectionResult
            {
                Name = "UPX",
                Version = version,
                Type = DetectionType.Packer,
                Confidence = Math.Min(confidence, 1.0),
                Signatures = signatures,
                DetectorName = Name,
                Metadata = new Dictionary<string, string>
                {
                    ["Entropy"] = entropy.ToString("F2"),
                    ["SignaturesFound"] = signatures.Count.ToString()
                }
            };
        });
    }
}
