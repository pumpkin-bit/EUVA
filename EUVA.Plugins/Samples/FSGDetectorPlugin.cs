// SPDX-License-Identifier: GPL-3.0-or-later


using EUVA.Core.Interfaces;
using EUVA.Core.Models;
using EUVA.Core.Parsers;
using EUVA.Plugins;

namespace EUVA.Plugins.Samples;


public class FSGDetectorPlugin : IDetectorPlugin
{
    public string Name => "FSG Detector Plugin";
    public string Version => "1.0.0";
    public int Priority => 15;

    public PluginMetadata Metadata => new()
    {
        Author = "EUVA Contributors",
        Description = "Detects FSG (Fast Small Good) packer v1.x-2.x",
       // Url = "https://github.com/euva/plugins",
        LastUpdated = new DateTime(2025, 2, 10),
        SupportedPackers = new List<string> { "FSG 1.0", "FSG 1.3", "FSG 2.0" }
    };

    private static readonly Dictionary<string, string> FSG_SIGNATURES = new()
    {
        ["FSG 1.0"] = "87 25 ?? ?? ?? ?? 61 94 55 A4 B6 80",
        ["FSG 1.3"] = "BE A4 01 40 00 AD 93 AD 97 AD 56 96 A5 B7 8B",
        ["FSG 2.0"] = "87 25 ?? ?? ?? ?? 61 94 55 A4 B6 80 7C ?? ?? 00 00 6C"
    };

    public void Initialize()
    {
        Console.WriteLine($"[Plugin] {Name} v{Version} initialized");
    }

    public void Cleanup()
    {
        Console.WriteLine($"[Plugin] {Name} cleaned up");
    }

    public bool CanAnalyze(BinaryStructure structure)
    {
       
        if (structure.Type != "Root" || structure.Name != "PE File")
            return false;

      
        var sections = structure.FindByPath("Sections");
        if (sections?.Children.Count is >= 2 and <= 4)
            return true;

        return false;
    }

    public async Task<DetectionResult?> DetectAsync(
        ReadOnlyMemory<byte> fileData,
        BinaryStructure structure)
    {
        return await Task.Run(() =>
        {
            var data = fileData.Span;
            var allSignatures = new List<SignatureMatch>();
            var detectedVersion = "";
            double baseConfidence = 0.0;

            
            foreach (var (version, pattern) in FSG_SIGNATURES)
            {
                var matches = SignatureScanner.FindPattern(data, pattern, $"FSG {version}");
                
                if (matches.Count > 0)
                {
                    allSignatures.AddRange(matches);
                    detectedVersion = version;
                    baseConfidence = 0.6; 
                    break;
                }
            }

            
            var sections = structure.FindByPath("Sections");
            if (sections != null)
            {
                
                var sectionNames = sections.Children
                    .Select(c => c.Name.ToUpperInvariant())
                    .ToList();

                
                bool hasSmallSections = sections.Children
                    .Any(s => s.Size.HasValue && s.Size < 1024);

                if (hasSmallSections)
                    baseConfidence += 0.1;

                
                var firstSection = sections.Children.FirstOrDefault();
                if (firstSection?.Size is > 0 and < 512)
                    baseConfidence += 0.15;
            }

            
            var entropy = SignatureScanner.CalculateEntropy(data);
            if (entropy > 7.0)
            {
                baseConfidence += 0.15;
            }

            
            var imports = structure.FindByPath("Data Directories", "Import Directory");
            if (imports != null)
            {
                var importRva = imports.Children
                    .FirstOrDefault(c => c.Name == "RVA")?.Value as uint?;

                
                if (importRva == 0)
                    baseConfidence += 0.1;
            }

            
            if (baseConfidence == 0.0 && allSignatures.Count == 0)
                return null;

            return new DetectionResult
            {
                Name = "FSG (Fast Small Good)",
                Version = string.IsNullOrEmpty(detectedVersion) ? null : detectedVersion,
                Type = DetectionType.Packer,
                Confidence = Math.Min(baseConfidence, 1.0),
                Signatures = allSignatures,
                DetectorName = Name,
                Metadata = new Dictionary<string, string>
                {
                    ["Entropy"] = entropy.ToString("F2"),
                    ["SignaturesFound"] = allSignatures.Count.ToString(),
                    ["PluginVersion"] = Version,
                    ["Author"] = Metadata.Author,
                    ["Description"] = Metadata.Description
                }
            };
        });
    }

    
    
    
    private bool AnalyzeSectionCharacteristics(BinaryStructure sections)
    {
        if (sections.Children.Count < 2)
            return false;

        
        
        
        

        var firstSection = sections.Children[0];
        var secondSection = sections.Children.Count > 1 ? sections.Children[1] : null;

        if (firstSection.Size < 1024 && secondSection?.Size > 10000)
            return true;

        return false;
    }
}
