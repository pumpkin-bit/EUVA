// SPDX-License-Identifier: GPL-3.0-or-later


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
