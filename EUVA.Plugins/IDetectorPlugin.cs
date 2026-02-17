// SPDX-License-Identifier: GPL-3.0-or-later


using EUVA.Core.Interfaces;

namespace EUVA.Plugins;


public interface IDetectorPlugin : IDetector
{
   
    PluginMetadata Metadata { get; }

    
    void Initialize();

    
    void Cleanup();
}


public class PluginMetadata
{
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public DateTime LastUpdated { get; init; }
    public List<string> SupportedPackers { get; init; } = new();
}
