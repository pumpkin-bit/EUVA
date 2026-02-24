## Detectors 

This system operates on the basis of confidence: the program needs to find a sufficient number of matches to consider a binary file either protected by a protector or something else. Section names, entropy, signatures, and anomalies are compared to determine whether a file is protected.
DetectorManager is the manager that manages all of this. It can scan a folder and detect new plugins. This means the system is scalable, and nothing prevents you from easily writing a new plugin for the program.
I've put a lot of effort into this. I want my program to be omnivorous to new plugins, so I've laid the foundation for an architecture that makes plugins easily extensible.
Also, each plugin has its own priorities. For example, the Themida scanner might work a little later than the UPX scanner.
And asynchronous operation, which prevents the interface from crashing or lagging.

---

**1. Themida/Winlicense Detector**

**ThemidaDetector.cs**

```csharp

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

```

---

**2. UPX Detector**
**UPXDetector.cs**

```csharp

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


```

---

**3. Detector Manager**

**DetectorManager.cs**
```csharp

using EUVA.Core.Interfaces;
using EUVA.Core.Models;
using System.Reflection;
using System.IO;
using System.Linq;

namespace EUVA.Core.Detectors;

public class DetectorManager
{
    private readonly List<IDetector> _detectors = new();

    public IReadOnlyList<IDetector> Detectors => _detectors;


    public void RegisterDetector(IDetector detector)
    {
        _detectors.Add(detector);
        _detectors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }


    public void LoadFromAssembly(Assembly assembly)
    {
        var detectorTypes = assembly.GetTypes()
            .Where(t => typeof(IDetector).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in detectorTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IDetector detector)
                {
                    RegisterDetector(detector);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load detector {type.Name}: {ex.Message}");
            }
        }
    }


    public void LoadFromDirectory(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return;

        var dllFiles = Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories);

        foreach (var dllFile in dllFiles)
        {
            try
            {
                var assembly = Assembly.LoadFrom(dllFile);
                LoadFromAssembly(assembly);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load plugin {Path.GetFileName(dllFile)}: {ex.Message}");
            }
        }
    }

    public async Task<List<DetectionResult>> AnalyzeAsync(ReadOnlyMemory<byte> fileData,
        BinaryStructure structure, IProgress<string>? progress = null)
    {
        var results = new List<DetectionResult>();

        var applicableDetectors = _detectors.Where(d => d.CanAnalyze(structure)).ToList();

        foreach (var detector in applicableDetectors)
        {
            try
            {
                progress?.Report($"Running {detector.Name}...");
                var result = await detector.DetectAsync(fileData, structure);

                if (result != null && result.Confidence > 0.0)
                {
                    results.Add(result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Detector {detector.Name} failed: {ex.Message}");
            }
        }

        return results.OrderByDescending(r => r.Confidence).ToList();
    }


    public DetectionResult? GetBestMatch(List<DetectionResult> results)
    {
        return results.OrderByDescending(r => r.Confidence).FirstOrDefault();
    }
}

```
