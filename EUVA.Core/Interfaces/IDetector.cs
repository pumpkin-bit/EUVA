// SPDX-License-Identifier: GPL-3.0-or-later
using EUVA.Core.Models;
namespace EUVA.Core.Interfaces;

public interface IDetector
{
    
    string Name { get; }
    string Version { get; }
    int Priority { get; }
    Task<DetectionResult?> DetectAsync(ReadOnlyMemory<byte> fileData, BinaryStructure structure);
    bool CanAnalyze(BinaryStructure structure);
}
