// SPDX-License-Identifier: GPL-3.0-or-later

using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public static class ExpressionInliner
{
    private sealed class DefUseInfo
    {
        public IrInstruction? DefInstr;
        public IrBlock? DefBlock;
        public int UseCount;
        public List<(IrBlock Block, IrInstruction Instr)> Uses = new();
    }

    public static int Inline(IrBlock[] blocks)
    {
        int changes = 0;
        var info = new Dictionary<string, DefUseInfo>();

        
        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;

                
                if (instr.DefinesDest && (instr.Destination.Kind == IrOperandKind.Register || instr.Destination.Kind == IrOperandKind.StackSlot))
                {
                    string key = GetSsaKey(instr.Destination);
                    if (!info.TryGetValue(key, out var dt))
                    {
                        dt = new DefUseInfo();
                        info[key] = dt;
                    }
                    dt.DefInstr = instr;
                    dt.DefBlock = block;
                }

                TrackUsesRecursively(instr, instr, block, info);

                if (instr.Destination.Kind == IrOperandKind.Memory)
                {
                    var dst = instr.Destination;
                    if (dst.MemBase != Register.None) AddUse(dst.MemBase, dst.SsaVersion, instr, block, info);
                    if (dst.MemIndex != Register.None) AddUse(dst.MemIndex, dst.SsaVersion, instr, block, info);
                }
            }
        }

        
        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (instr.IsDead) continue;

                
                if (!instr.DefinesDest || (instr.Destination.Kind != IrOperandKind.Register && instr.Destination.Kind != IrOperandKind.StackSlot))
                    continue;

                
                if (instr.Opcode == IrOpcode.Call || instr.HasSideEffects)
                    continue;
                    
                
                if (instr.Opcode == IrOpcode.Phi)
                    continue;

                string key = GetSsaKey(instr.Destination);
                if (info.TryGetValue(key, out var dt) && dt.UseCount == 1 && dt.DefBlock != null)
                {
                    var useEntry = dt.Uses[0];
                    var consumer = useEntry.Instr;
                    var consumerBlock = useEntry.Block;

                    if (consumer.Opcode == IrOpcode.Phi)
                        continue;

                    bool readsMemory = instr.Opcode == IrOpcode.Load;
                    bool safeToInline = true;

                    if (readsMemory)
                    {
                        safeToInline = IsPathMemorySafe(dt.DefBlock, instr, consumerBlock, consumer, blocks);
                    }

                    if (safeToInline)
                    {
                        bool replaced = ReplaceUseWithExpression(consumer, instr);
                        if (replaced)
                        {
                            instr.IsDead = true; 
                            changes++;
                        }
                    }
                }
            }
        }

        return changes;
    }

    private static void TrackUsesRecursively(IrInstruction node, IrInstruction rootConsumer, IrBlock consumerBlock, Dictionary<string, DefUseInfo> info)
    {
        foreach (var src in node.Sources)
        {
            if (src.Kind == IrOperandKind.Expression && src.Expression != null)
            {
                TrackUsesRecursively(src.Expression, rootConsumer, consumerBlock, info);
            }
            else if (src.Kind == IrOperandKind.Register || src.Kind == IrOperandKind.StackSlot)
            {
                string key = GetSsaKey(src);
                if (!info.TryGetValue(key, out var dt))
                {
                    dt = new DefUseInfo();
                    info[key] = dt;
                }
                dt.UseCount++;
                dt.Uses.Add((consumerBlock, rootConsumer));
            }
            else if (src.Kind == IrOperandKind.Memory)
            {
                if (src.MemBase != Register.None) AddUse(src.MemBase, src.SsaVersion, rootConsumer, consumerBlock, info);
                if (src.MemIndex != Register.None) AddUse(src.MemIndex, src.SsaVersion, rootConsumer, consumerBlock, info);
            }
        }
    }

    private static void AddUse(Register reg, int ssa, IrInstruction consumer, IrBlock consumerBlock, Dictionary<string, DefUseInfo> info)
    {
        var op = IrOperand.Reg(reg, 64);
        op.SsaVersion = ssa;
        string key = GetSsaKey(op);
        if (!info.TryGetValue(key, out var dt))
        {
            dt = new DefUseInfo();
            info[key] = dt;
        }
        dt.UseCount++;
        dt.Uses.Add((consumerBlock, consumer));
    }

    private static bool ReplaceUseWithExpression(IrInstruction consumer, IrInstruction exprInstr)
    {
        bool replaced = false;
        string defKey = GetSsaKey(exprInstr.Destination);

        for (int i = 0; i < consumer.Sources.Length; i++)
        {
            var src = consumer.Sources[i];

            if (src.Kind == IrOperandKind.Expression && src.Expression != null)
            {
                if (ReplaceUseWithExpression(src.Expression, exprInstr))
                    replaced = true;
                continue;
            }

            if ((src.Kind == IrOperandKind.Register || src.Kind == IrOperandKind.StackSlot) && GetSsaKey(src) == defKey)
            {
                consumer.Sources[i] = IrOperand.Expr(exprInstr);
                replaced = true;
            }
        }

        return replaced;
    }

    private static bool IsPathMemorySafe(IrBlock defBlock, IrInstruction defInstr, IrBlock useBlock, IrInstruction useInstr, IrBlock[] allBlocks)
    {
        if (defBlock == useBlock)
        {
            int defIdx = defBlock.Instructions.IndexOf(defInstr);
            int useIdx = defBlock.Instructions.IndexOf(useInstr);
            
            if (defIdx == -1 || useIdx == -1 || defIdx >= useIdx) return false; 
            
            for (int i = defIdx + 1; i < useIdx; i++)
            {
                var instr = defBlock.Instructions[i];
                if (instr.IsDead) continue;
                if (instr.Opcode == IrOpcode.Store || instr.Opcode == IrOpcode.Call || instr.HasSideEffects) return false;
            }
            return true;
        }

        
        int dIdx = defBlock.Instructions.IndexOf(defInstr);
        for (int i = dIdx + 1; i < defBlock.Instructions.Count; i++)
        {
            var instr = defBlock.Instructions[i];
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Store || instr.Opcode == IrOpcode.Call || instr.HasSideEffects) return false;
        }

     
        int uIdx = useBlock.Instructions.IndexOf(useInstr);
        for (int i = 0; i < uIdx; i++)
        {
            var instr = useBlock.Instructions[i];
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Store || instr.Opcode == IrOpcode.Call || instr.HasSideEffects) return false;
        }

       
        int curr = useBlock.Idom;
        while (curr >= 0 && curr < allBlocks.Length && curr != defBlock.Index)
        {
            var currBlock = allBlocks[curr];
            foreach(var instr in currBlock.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.Opcode == IrOpcode.Store || instr.Opcode == IrOpcode.Call || instr.HasSideEffects) return false;
            }
            if (curr == currBlock.Idom) break;
            curr = currBlock.Idom;
        }

        return curr == defBlock.Index; 
    }

    private static string GetSsaKey(IrOperand op)
    {
        if (op.Kind == IrOperandKind.StackSlot)
            return $"stack_{op.StackOffset}_{op.SsaVersion}";
        return $"{op.CanonicalRegister}_{op.SsaVersion}";
    }
}
