// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Iced.Intel;

namespace EUVA.Core.Disassembly;

public struct ExecutableRange
{
    public long Start;
    public long End;
    public string Name;
}

public sealed class CfgScanner
{
    private const int MaxBlocks = 32768; 
    private const int MaxBlockInstr = 1000;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe BasicBlock[] ScanFunction(byte* data, int length, long baseAddress, int bitness, byte* fullMap = null, long fullLen = 0, ExecutableRange[]? executableSections = null)
    {
        if (length <= 0) return Array.Empty<BasicBlock>();

        var visited = new HashSet<long>();
        var queue = new Queue<long>();
        var leaders = new HashSet<long>();

        queue.Enqueue(baseAddress);
        leaders.Add(baseAddress);

        var reader = new UnsafePointerCodeReader();
        
        while (queue.Count > 0)
        {
            long currentIP = queue.Dequeue();
            if (visited.Contains(currentIP)) continue;
            visited.Add(currentIP);

            byte* basePtr = (fullMap != null) ? fullMap : data;
            long baseLimit = (fullMap != null) ? fullLen : baseAddress + length;
            long baseAddrRel = (fullMap != null) ? 0 : baseAddress;

            if (currentIP < baseAddrRel || currentIP >= baseLimit) continue;
            
            if (executableSections != null && !IsExecutable(currentIP, executableSections))
                continue;

            reader.Reset(basePtr + (currentIP - baseAddrRel), (int)(baseLimit - currentIP));
            var decoder = Decoder.Create(bitness, reader, (ulong)currentIP);
            
            int continuousZeros = 0;
            int continuousPaddings = 0;

            for (int i = 0; i < MaxBlockInstr; i++)
            {
                decoder.Decode(out var instr);

            
                if (instr.IsInvalid) break; 

                if (instr.Code == Code.Add_rm8_r8 && instr.Op0Kind == OpKind.Memory && instr.Op1Kind == OpKind.Register)
                {
                    continuousZeros++;
                    if (continuousZeros >= 4) break; 
                }
                else continuousZeros = 0;

              
                if (instr.Mnemonic == Mnemonic.Int3 || instr.Mnemonic == Mnemonic.Nop || instr.Mnemonic == Mnemonic.Fnop)
                {
                    continuousPaddings++;
                    if (continuousPaddings >= 4) break; 
                }
                else continuousPaddings = 0;

                long nextIP = (long)instr.NextIP;
                bool isTerminal = false;

             
                switch (instr.FlowControl)
                {
                    case FlowControl.ConditionalBranch:
                        long t1 = (long)instr.NearBranchTarget;
                        CheckAndEnqueue(t1, queue, leaders, executableSections);
                        CheckAndEnqueue(nextIP, queue, leaders, executableSections);
                        isTerminal = true;
                        break;

                    case FlowControl.UnconditionalBranch:
                        long t2 = (long)instr.NearBranchTarget;
                        CheckAndEnqueue(t2, queue, leaders, executableSections);
                        isTerminal = true;
                        break;

                    case FlowControl.Return:
                    case FlowControl.Exception:
                        isTerminal = true;
                        break;

                    case FlowControl.Call:
                    case FlowControl.IndirectCall:
                       
                        if (IsNonReturningCall(instr)) isTerminal = true;
                        break;

                    case FlowControl.IndirectBranch:
                        isTerminal = true;
                        break;
                }

                if (isTerminal) break;
                
               
                if (leaders.Contains(nextIP)) break;
            }
        }

        var leaderList = leaders.OrderBy(x => x).ToArray();
        return BuildBlocksRecursive(data, length, baseAddress, bitness, leaderList, fullMap, fullLen, executableSections);
    }

    private static bool IsExecutable(long addr, ExecutableRange[] sections)
    {
        foreach (var sec in sections)
            if (addr >= sec.Start && addr < sec.End) return true;
        return false;
    }

    private static bool CheckAndEnqueue(long addr, Queue<long> q, HashSet<long> leaders, ExecutableRange[]? executableSections)
    {
        if (executableSections != null && !IsExecutable(addr, executableSections)) return false;
        if (leaders.Add(addr))
        {
            q.Enqueue(addr);
            return true;
        }
        return false;
    }

    private static bool IsNonReturningCall(Instruction instr)
    {
        return false; 
    }

    private unsafe BasicBlock[] BuildBlocksRecursive(byte* data, int length, long baseAddress, int bitness, long[] leaders, byte* fullMap, long fullLen, ExecutableRange[]? executableSections)
    {
        var blocks = new List<BasicBlock>();
        var reader = new UnsafePointerCodeReader();
        byte* basePtr = (fullMap != null) ? fullMap : data;
        long baseLimit = (fullMap != null) ? fullLen : baseAddress + length;
        long baseAddrRel = (fullMap != null) ? 0 : baseAddress;

        for (int i = 0; i < leaders.Length; i++)
        {
            long start = leaders[i];
            if (start < baseAddrRel || start >= baseLimit) continue;

            reader.Reset(basePtr + (start - baseAddrRel), (int)(baseLimit - start));
            var decoder = Decoder.Create(bitness, reader, (ulong)start);
            
            var block = new BasicBlock
            {
                StartOffset = start,
                IsFirstBlock = (i == 0)
            };

            var successors = new List<long>();
            int instrCount = 0;
            long currentIP = start;

            while (decoder.IP < (ulong)baseLimit && instrCount < MaxBlockInstr)
            {
                decoder.Decode(out var instr);
                if (instr.IsInvalid) break;
                instrCount++;
                currentIP = (long)decoder.IP;

                bool endBlock = false;
                switch (instr.FlowControl)
                {
                    case FlowControl.ConditionalBranch:
                        successors.Add((long)instr.NearBranchTarget);
                        successors.Add((long)instr.NextIP);
                        block.IsConditional = true;
                        endBlock = true;
                        break;

                    case FlowControl.UnconditionalBranch:
                        successors.Add((long)instr.NearBranchTarget);
                        endBlock = true;
                        break;

                    case FlowControl.Return:
                    case FlowControl.Exception:
                        block.IsReturn = true;
                        endBlock = true;
                        break;

                    case FlowControl.IndirectBranch:
                        endBlock = true;
                        break;

                    case FlowControl.Call:
                        if (IsNonReturningCall(instr)) { block.IsReturn = true; endBlock = true; }
                        break;
                }

                if (endBlock) break;

             
                if (i + 1 < leaders.Length && (long)decoder.IP >= leaders[i + 1])
                {
                  
                    successors.Add((long)decoder.IP);
                    break;
                }
            }

            block.InstructionCount = instrCount;
            block.ByteLength = (int)(currentIP - start);
         
            block.SuccessorOffsets = successors.ToArray();
            blocks.Add(block);
        }

       
        var finalBlocks = blocks.ToArray();
        for (int i = 0; i < finalBlocks.Length; i++)
        {
            if (finalBlocks[i].SuccessorOffsets == null) continue;
            var succIndices = new List<int>();
            foreach (var off in finalBlocks[i].SuccessorOffsets)
            {
                int idx = Array.FindIndex(finalBlocks, b => b.StartOffset == off);
                if (idx >= 0) succIndices.Add(idx);
            }
            finalBlocks[i].Successors = succIndices.ToArray();
        }

        return finalBlocks;
    }
}
