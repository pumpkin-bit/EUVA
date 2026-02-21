// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Models;

public class DetectionResult
{
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public DetectionType Type { get; init; }
    public double Confidence { get; init; }
    public List<SignatureMatch> Signatures { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
    public string DetectorName { get; init; } = string.Empty;
    public override string ToString() => 
        $"{Name} {Version ?? ""} ({Type}, {Confidence:P0})".Trim();
}

public enum DetectionType
{
    Packer,
    Protector,
    Cryptor,
    Virtualizer,
    Compiler,
    Unknown
}


public class SignatureMatch
{
    public long Offset { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public int Length { get; init; }
    public override string ToString() => $"{Name} at 0x{Offset:X8}";
}
