// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Iced.Intel;
using System.Text.Json.Serialization;

namespace EUVA.Core.Disassembly.Analysis;


public sealed class FunctionFingerprint
{
    [JsonPropertyName("bc")]  public int BlockCount           { get; set; }
    [JsonPropertyName("ec")]  public int EdgeCount            { get; set; }  
    [JsonPropertyName("cc")]  public int CyclomaticComplexity { get; set; }
    [JsonPropertyName("bec")] public int BackEdgeCount        { get; set; }
    [JsonPropertyName("md")]  public int MaxDepth             { get; set; }

    
    [JsonPropertyName("ti")]   public int TotalInstructions  { get; set; }
    [JsonPropertyName("clc")]  public int CallCount          { get; set; }
    [JsonPropertyName("icc")]  public int IndirectCallCount  { get; set; }
    [JsonPropertyName("mrc")]  public int MemReadCount       { get; set; }
    [JsonPropertyName("mwc")]  public int MemWriteCount      { get; set; }
    [JsonPropertyName("cmpc")] public int CompareCount       { get; set; }  
    [JsonPropertyName("ac")]   public int ArithmeticCount    { get; set; }  
    [JsonPropertyName("bwc")]  public int BitwiseCount       { get; set; }  

    
    [JsonPropertyName("ic")]  public ulong[] ImmediateConstants { get; set; } = Array.Empty<ulong>();

    
    [JsonPropertyName("bss")] public int[] BlockSizeSequence { get; set; } = Array.Empty<int>();

    
    [JsonPropertyName("th")]  public uint TopoHash { get; set; }
}




public static class FingerprintExtractor
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static FunctionFingerprint Extract(IrBlock[] blocks)
    {
        if (blocks.Length == 0)
            return new FunctionFingerprint();

        var fp = new FunctionFingerprint();

        
        
        
        fp.BlockCount = blocks.Length;

        int edgeCount = 0;
        foreach (var b in blocks)
            edgeCount += b.Successors?.Count ?? 0;
        fp.EdgeCount = edgeCount;

        fp.CyclomaticComplexity = edgeCount - blocks.Length + 2;
        fp.BackEdgeCount        = CountBackEdges(blocks);
        fp.MaxDepth             = ComputeMaxDepth(blocks);

        
        
        
        var constants = new HashSet<ulong>();

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                fp.TotalInstructions++;

                switch (instr.Opcode)
                {
                    case IrOpcode.Call:
                        if (instr.Sources.Length > 0 &&
                            instr.Sources[0].Kind == IrOperandKind.Constant)
                            fp.CallCount++;
                        else
                            fp.IndirectCallCount++;
                        break;

                    case IrOpcode.Load:
                        fp.MemReadCount++;
                        break;

                    case IrOpcode.Store:
                        fp.MemWriteCount++;
                        break;

                    case IrOpcode.Cmp:
                    case IrOpcode.Test:
                        fp.CompareCount++;
                        break;

                    case IrOpcode.Add:
                    case IrOpcode.Sub:
                    case IrOpcode.Mul:
                    case IrOpcode.IMul:
                    case IrOpcode.Div:
                    case IrOpcode.IDiv:
                        fp.ArithmeticCount++;
                        break;

                    case IrOpcode.And:
                    case IrOpcode.Or:
                    case IrOpcode.Xor:
                    case IrOpcode.Shl:
                    case IrOpcode.Shr:
                    case IrOpcode.Sar:
                        fp.BitwiseCount++;
                        break;
                }

                CollectConstants(instr, constants);
            }
        }

        var sortedConstants = new List<ulong>(constants);
        sortedConstants.Sort();
        fp.ImmediateConstants = sortedConstants.ToArray();

        
        
        
        fp.BlockSizeSequence = BuildBlockSizeSequence(blocks);

        
        
        
        fp.TopoHash = ComputeTopoHash(fp);

        return fp;
    }

    private static int FindEntry(IrBlock[] blocks)
    {
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i].IsEntry) return i;
        return 0;
    }

    private static int CountBackEdges(IrBlock[] blocks)
    {
        var color     = new byte[blocks.Length];
        int backEdges = 0;

        void Dfs(int idx)
        {
            if (idx < 0 || idx >= blocks.Length) return;
            if (color[idx] == 2) return;
            if (color[idx] == 1) { backEdges++; return; }

            color[idx] = 1;
            var succs = blocks[idx].Successors;
            if (succs != null)
                foreach (int s in succs)
                    Dfs(s);
            color[idx] = 2;
        }

        Dfs(FindEntry(blocks));
        return backEdges;
    }

    private static int ComputeMaxDepth(IrBlock[] blocks)
    {
        if (blocks.Length == 0) return 0;

        int entry    = FindEntry(blocks);
        var depth    = new int[blocks.Length];
        var visited  = new bool[blocks.Length];
        var queue    = new Queue<int>();

        queue.Enqueue(entry);
        visited[entry] = true;
        int maxDepth   = 0;

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (depth[cur] > maxDepth) maxDepth = depth[cur];

            var succs = blocks[cur].Successors;
            if (succs == null) continue;
            foreach (int s in succs)
            {
                if (s >= 0 && s < blocks.Length && !visited[s])
                {
                    visited[s] = true;
                    depth[s]   = depth[cur] + 1;
                    queue.Enqueue(s);
                }
            }
        }

        return maxDepth;
    }

    private static int[] BuildBlockSizeSequence(IrBlock[] blocks)
    {
        if (blocks.Length == 0) return Array.Empty<int>();

        int entry   = FindEntry(blocks);
        var result  = new List<int>(blocks.Length);
        var visited = new HashSet<int>();

        void Dfs(int idx)
        {
            if (idx < 0 || idx >= blocks.Length) return;
            if (!visited.Add(idx)) return;

            int liveCount = 0;
            foreach (var instr in blocks[idx].Instructions)
                if (!instr.IsDead) liveCount++;

            result.Add(liveCount);

            var succs = blocks[idx].Successors;
            if (succs == null) return;
            foreach (int s in succs)
                Dfs(s);
        }

        Dfs(entry);
        return result.ToArray();
    }

    private static void CollectConstants(IrInstruction instr, HashSet<ulong> constants)
    {
        foreach (var src in instr.Sources)
        {
            if (src.Kind != IrOperandKind.Constant) continue;
            ulong val = (ulong)src.ConstantValue;
            if (val <= 0xFF)          continue; 
            if (IsPowerOfTwo(val))    continue; 
            if (IsLikelyAddress(val)) continue; 
            constants.Add(val);
        }
    }

    private static bool IsPowerOfTwo(ulong val)
        => val != 0 && (val & (val - 1)) == 0;

    private static bool IsLikelyAddress(ulong val)
        => (val >= 0x00400000UL && val <= 0x7FFFFFFFUL)   
        || (val >= 0x140000000UL && val <= 0x7FFFFFFFFFFFFUL); 

    private static uint ComputeTopoHash(FunctionFingerprint fp)
    {
        
        uint h = 2166136261u;
        h = (h ^ (uint)fp.BlockCount)           * 16777619u;
        h = (h ^ (uint)fp.CyclomaticComplexity) * 16777619u;
        h = (h ^ (uint)fp.BackEdgeCount)        * 16777619u;
        h = (h ^ (uint)fp.MaxDepth)             * 16777619u;
        return h;
    }
}



public static class FingerprintMatcher
{
    
    public const float ThresholdHigh   = 0.85f;
    public const float ThresholdMedium = 0.70f;
    public const float ThresholdLow    = 0.55f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Compare(FunctionFingerprint a, FunctionFingerprint b)
    {
        float score = 0f;

        
        
        score += 0.10f * SmoothMatch(a.BlockCount,           b.BlockCount,           0.25f);
        score += 0.08f * SmoothMatch(a.CyclomaticComplexity, b.CyclomaticComplexity, 0.20f);
        score += 0.06f * SmoothMatch(a.BackEdgeCount,        b.BackEdgeCount,        0.30f);
        score += 0.06f * SmoothMatch(a.EdgeCount,            b.EdgeCount,            0.25f); 
        score += 0.05f * SmoothMatch(a.MaxDepth,             b.MaxDepth,             0.25f);

        
        
        score += 0.04f * SmoothMatch(a.TotalInstructions, b.TotalInstructions, 0.35f); 
        score += 0.10f * SmoothMatch(a.CallCount,         b.CallCount,         0.20f); 
        score += 0.05f * SmoothMatch(a.IndirectCallCount, b.IndirectCallCount, 0.25f);
        score += 0.06f * SmoothMatch(a.CompareCount,      b.CompareCount,      0.20f); 
        score += 0.03f * SmoothMatch(a.MemReadCount,      b.MemReadCount,      0.30f);
        score += 0.03f * SmoothMatch(a.MemWriteCount,     b.MemWriteCount,     0.30f);
        score += 0.02f * SmoothMatch(a.ArithmeticCount,   b.ArithmeticCount,   0.35f);
        score += 0.02f * SmoothMatch(a.BitwiseCount,      b.BitwiseCount,      0.35f);

        
        score += 0.20f * JaccardSimilarity(a.ImmediateConstants, b.ImmediateConstants);

        
        score += 0.10f * SequenceSimilarity(a.BlockSizeSequence, b.BlockSizeSequence);

        return MathF.Min(score, 1f);
    }

    private static float SmoothMatch(int a, int b, float maxTolerance)
    {
        if (a == b) return 1f;
        int maxVal = Math.Max(Math.Abs(a), Math.Abs(b));
        if (maxVal == 0) return 1f;

        float diffRatio = (float)Math.Abs(a - b) / maxVal;
        if (diffRatio > maxTolerance) return 0f;

        
        return 1f - (diffRatio / maxTolerance);
    }

    private static float JaccardSimilarity(ulong[] a, ulong[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 1f;
        if (a.Length == 0 || b.Length == 0) return 0f;

        int intersect = 0, ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            if      (a[ia] < b[ib]) ia++;
            else if (a[ia] > b[ib]) ib++;
            else                  { intersect++; ia++; ib++; }
        }

        int union = a.Length + b.Length - intersect;
        return union == 0 ? 1f : (float)intersect / union;
    }

    private const int MaxSeqLen = 32;

    private static float SequenceSimilarity(int[] a, int[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 1f;
        if (a.Length == 0 || b.Length == 0) return 0f;

        int lenA    = Math.Min(a.Length, MaxSeqLen);
        int lenB    = Math.Min(b.Length, MaxSeqLen);
        int dist    = LevenshteinFuzzy(a, lenA, b, lenB);
        int maxDist = Math.Max(lenA, lenB);

        return 1f - (float)dist / (maxDist * 2f);
    }

    private static int LevenshteinFuzzy(int[] a, int lenA, int[] b, int lenB)
    {
        var dp = new int[lenA + 1, lenB + 1];
        for (int i = 0; i <= lenA; i++) dp[i, 0] = i * 2;
        for (int j = 0; j <= lenB; j++) dp[0, j] = j * 2;

        for (int i = 1; i <= lenA; i++)
        {
            for (int j = 1; j <= lenB; j++)
            {
                int   maxVal  = Math.Max(Math.Abs(a[i-1]), Math.Abs(b[j-1]));
                float diff    = maxVal == 0 ? 0f : (float)Math.Abs(a[i-1] - b[j-1]) / maxVal;
                
                int   subCost = diff <= 0.30f ? 0 : 1; 

                dp[i, j] = Math.Min(
                    Math.Min(dp[i-1, j] + 2, dp[i, j-1] + 2),
                    dp[i-1, j-1] + subCost);
            }
        }

        return dp[lenA, lenB];
    }

    public static string FormatResult(float score, string name, string lib)
    {
        string display = (!string.IsNullOrEmpty(lib) && !string.IsNullOrEmpty(name))
            ? $"{lib}::{name}"
            : (!string.IsNullOrEmpty(name) ? name : (!string.IsNullOrEmpty(lib) ? lib : "unknown"));

        return score switch
        {
            >= ThresholdHigh   => $"[!] Identified: {display} (similarity: {score:P0})",
            >= ThresholdMedium => $"[~] Likely: {display} (similarity: {score:P0})",
            >= ThresholdLow    => $"[?] Possible: {display} (similarity: {score:P0})",
            _                  => $"[?] Unrecognized (best: {display} @ {score:P0})"
        };
    }
}