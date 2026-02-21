// SPDX-License-Identifier: GPL-3.0-or-later


using EUVA.Core.Models;

namespace EUVA.Core.Parsers;

public class SignatureScanner
{
    
    public static List<SignatureMatch> FindPattern(ReadOnlySpan<byte> data, string pattern, string signatureName)
    {
        var matches = new List<SignatureMatch>();
        var patternBytes = ParsePattern(pattern);

        if (patternBytes.Length == 0)
            return matches;

        for (int i = 0; i <= data.Length - patternBytes.Length; i++)
        {
            if (MatchesPattern(data.Slice(i, patternBytes.Length), patternBytes))
            {
                matches.Add(new SignatureMatch
                {
                    Offset = i,
                    Name = signatureName,
                    Pattern = pattern,
                    Length = patternBytes.Length
                });
            }
        }

        return matches;
    }

    public static long FindFirst(ReadOnlySpan<byte> data, string pattern)
    {
        var patternBytes = ParsePattern(pattern);

        if (patternBytes.Length == 0)
            return -1;

        for (int i = 0; i <= data.Length - patternBytes.Length; i++)
        {
            if (MatchesPattern(data.Slice(i, patternBytes.Length), patternBytes))
                return i;
        }

        return -1;
    }
    
    public static List<SignatureMatch> FindInRange(ReadOnlySpan<byte> data, long offset, long size, 
        string pattern, string signatureName)
    {
        if (offset < 0 || offset + size > data.Length)
            return new List<SignatureMatch>();

        var slice = data.Slice((int)offset, (int)size);
        var matches = FindPattern(slice, pattern, signatureName);

        
        foreach (var match in matches)
        {
            match.GetType().GetProperty(nameof(SignatureMatch.Offset))!
                .SetValue(match, match.Offset + offset);
        }

        return matches;
    }

    private static PatternByte[] ParsePattern(string pattern)
    {
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new PatternByte[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "??" || parts[i] == "?")
            {
                result[i] = new PatternByte { IsWildcard = true };
            }
            else
            {
                result[i] = new PatternByte
                {
                    Value = Convert.ToByte(parts[i], 16),
                    IsWildcard = false
                };
            }
        }

        return result;
    }
    private static bool MatchesPattern(ReadOnlySpan<byte> data, PatternByte[] pattern)
    {
        if (data.Length != pattern.Length)
            return false;

        for (int i = 0; i < pattern.Length; i++)
        {
            if (!pattern[i].IsWildcard && data[i] != pattern[i].Value)
                return false;
        }

        return true;
    }

    private struct PatternByte
    {
        public byte Value;
        public bool IsWildcard;
    }


    public static double CalculateEntropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return 0.0;

        Span<int> frequencies = stackalloc int[256];
        
        foreach (byte b in data)
            frequencies[b]++;

        double entropy = 0.0;
        double dataLength = data.Length;

        for (int i = 0; i < 256; i++)
        {
            if (frequencies[i] == 0)
                continue;

            double probability = frequencies[i] / dataLength;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }


    public static Dictionary<string, double> AnalyzeSectionEntropy(ReadOnlySpan<byte> data, 
        IEnumerable<DataRegion> regions)
    {
        var results = new Dictionary<string, double>();

        foreach (var region in regions.Where(r => r.Type == RegionType.Code || r.Type == RegionType.Data))
        {
            if (region.Offset + region.Size <= data.Length)
            {
                var slice = data.Slice((int)region.Offset, (int)region.Size);
                results[region.Name] = CalculateEntropy(slice);
            }
        }

        return results;
    }
}
