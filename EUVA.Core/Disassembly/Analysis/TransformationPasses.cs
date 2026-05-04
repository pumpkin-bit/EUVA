// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public static class TransformationPasses
{
    public static void Optimize(IrBlock[] blocks)
    {
        int initialCount = blocks.Sum(b => b.Instructions.Count(i => !i.IsDead));

        for (int i = 0; i < 5; i++)
        {
            EliminateDeadVariables(blocks);
            MergeVariables(blocks);
            FoldExpressions(blocks);
        }
        FoldApiCalls(blocks);
        
        int finalCount = blocks.Sum(b => b.Instructions.Count(i => !i.IsDead));

        AnalyzePointers(blocks);
        
        var coalescedGroups = CoalesceVariables(blocks);

        RenameVariables(blocks, coalescedGroups);
    }

    private static Dictionary<(string, int), int> CoalesceVariables(IrBlock[] blocks)
    {
        var allVars = new HashSet<(string, int)>();
        var defInBlock = new Dictionary<(string, int), int>();
        var usedInBlock = new Dictionary<(string, int), HashSet<int>>();

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.DefinesDest)
                {
                    var key = SsaBuilder.GetVarKey(in instr.Destination);
                    if (key != null)
                    {
                        var v = (key, instr.Destination.SsaVersion);
                        allVars.Add(v);
                        defInBlock[v] = block.Index;
                    }
                }
                foreach (var src in instr.Sources)
                {
                    var key = SsaBuilder.GetVarKey(in src);
                    if (key != null)
                    {
                        var v = (key, src.SsaVersion);
                        allVars.Add(v);
                        if (!usedInBlock.TryGetValue(v, out var set)) usedInBlock[v] = set = new HashSet<int>();
                        set.Add(block.Index);
                    }
                }
            }
        }

        var liveOut = new HashSet<(string, int)>[blocks.Length];
        var liveIn = new HashSet<(string, int)>[blocks.Length];
        for (int i = 0; i < blocks.Length; i++) { liveOut[i] = new(); liveIn[i] = new(); }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = blocks.Length - 1; i >= 0; i--)
            {
                var block = blocks[i];
                var newLiveOut = new HashSet<(string, int)>();
                foreach (var succ in block.Successors)
                {
                    if (succ >= 0 && succ < blocks.Length)
                        newLiveOut.UnionWith(liveIn[succ]);
                }

                if (!newLiveOut.SetEquals(liveOut[i]))
                {
                    liveOut[i] = newLiveOut;
                    changed = true;
                }

                var newLiveIn = new HashSet<(string, int)>(liveOut[i]);
                foreach (var v in allVars)
                {
                    if (defInBlock.TryGetValue(v, out var b) && b == i)
                        newLiveIn.Remove(v);
                    if (usedInBlock.TryGetValue(v, out var set) && set.Contains(i))
                        newLiveIn.Add(v);
                }

                if (!newLiveIn.SetEquals(liveIn[i]))
                {
                    liveIn[i] = newLiveIn;
                    changed = true;
                }
            }
        }

        var parent = new Dictionary<(string, int), (string, int)>();
        (string, int) Find((string, int) v)
        {
            if (!parent.TryGetValue(v, out var p)) return v;
            if (p == v) return v;
            return parent[v] = Find(p);
        }
        void Union((string, int) v1, (string, int) v2)
        {
            var r1 = Find(v1); var r2 = Find(v2);
            if (r1 != r2) parent[r1] = r2;
        }

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead || instr.Opcode != IrOpcode.Phi) continue;
                var dstKey = SsaBuilder.GetVarKey(in instr.Destination);
                if (dstKey == null) continue;
                var dstV = (dstKey, instr.Destination.SsaVersion);

                foreach (var src in instr.Sources)
                {
                    var srcKey = SsaBuilder.GetVarKey(in src);
                    if (srcKey != null && srcKey == dstKey)
                        Union(dstV, (srcKey, src.SsaVersion));
                }
            }
        }

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if ((instr.Opcode == IrOpcode.Add || instr.Opcode == IrOpcode.Sub || instr.Opcode == IrOpcode.Assign) && instr.Sources.Length >= 1)
                {
                    var dstKey = SsaBuilder.GetVarKey(in instr.Destination);
                    var srcKey = SsaBuilder.GetVarKey(in instr.Sources[0]);
                    if (dstKey != null && srcKey != null && dstKey == srcKey)
                    {
                        Union((dstKey, instr.Destination.SsaVersion), (srcKey, instr.Sources[0].SsaVersion));
                    }
                }
            }
        }

        var groups = new Dictionary<int, HashSet<(string, int)>>();
        var varToIndex = new Dictionary<(string, int), int>();
        int clusterId = 0;
        var representatives = new Dictionary<(string, int), int>();

        foreach (var v in allVars)
        {
            var root = Find(v);
            if (!representatives.TryGetValue(root, out var id))
                representatives[root] = id = clusterId++;
            varToIndex[v] = id;
            if (!groups.TryGetValue(id, out var g)) groups[id] = g = new HashSet<(string, int)>();
            g.Add(v);
        }

        foreach (var group in groups.Values)
        {
            var list = group.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    var v1 = list[i]; var v2 = list[j];
                    for (int b = 0; b < blocks.Length; b++)
                    {
                    }
                }
            }
        }

        return varToIndex;
    }

    private static void AnalyzePointers(IrBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;

                var ops = instr.Sources.Concat(new[] { instr.Destination });
                foreach (var op in ops)
                {
                    if (op.Kind == IrOperandKind.Memory && op.MemBase != Register.None && op.MemIndex != Register.None)
                    {
                      
                         var type = op.MemScale switch
                         {
                             1 => new TypeInfo { BaseType = PrimitiveType.UInt8, PointerLevel = 1 },
                             2 => new TypeInfo { BaseType = PrimitiveType.UInt16, PointerLevel = 1 },
                             4 => new TypeInfo { BaseType = PrimitiveType.UInt32, PointerLevel = 1 },
                             8 => new TypeInfo { BaseType = PrimitiveType.UInt64, PointerLevel = 1 },
                             _ => TypeInfo.Unknown
                         };
                    }
                }
            }
        }
    }

    private static void RenameVariables(IrBlock[] blocks, Dictionary<(string, int), int> coalescedGroups)
    {
        var clusterToName = new Dictionary<int, string>();
        var varCounter = 0;
        var argCounter = 0;

    
        var abiRegs = new[] { Register.RCX, Register.RDX, Register.R8, Register.R9 };


        foreach (var reg in abiRegs)
        {
            var key = ($"r_{IrOperand.GetCanonical(reg)}", 0);
            if (coalescedGroups.TryGetValue(key, out var cid))
            {
                if (!clusterToName.TryGetValue(cid, out _))
                    clusterToName[cid] = $"arg_{++argCounter}";
            }
        }


        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                
                var allOps = new List<IrOperand>(instr.Sources);
                if (instr.DefinesDest) allOps.Add(instr.Destination);

                foreach (var op in allOps)
                {
                    foreach (var key in GetAvailableSsaKeys(op))
                    {
                        if (coalescedGroups.TryGetValue(key, out var cid))
                        {
                            if (!clusterToName.TryGetValue(cid, out _))
                                clusterToName[cid] = $"var_{++varCounter}";
                        }
                    }
                }
            }
        }

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.DefinesDest) RenameOperand(instr.Destination, coalescedGroups, clusterToName);
                for (int i = 0; i < instr.Sources.Length; i++)
                    RenameOperand(instr.Sources[i], coalescedGroups, clusterToName);
            }
        }
    }

    private static IEnumerable<(string, int)> GetAvailableSsaKeys(IrOperand op)
    {
        if (op.Kind == IrOperandKind.Register && op.SsaVersion >= 0)
        {
            yield return ($"r_{IrOperand.GetCanonical(op.Register)}", op.SsaVersion);
        }
        else if (op.Kind == IrOperandKind.Memory)
        {
            if (op.MemBase != Register.None) yield return ($"r_{IrOperand.GetCanonical(op.MemBase)}", op.MemBaseSsaVersion);
            if (op.MemIndex != Register.None) yield return ($"r_{IrOperand.GetCanonical(op.MemIndex)}", op.MemIndexSsaVersion);
        }
        else if (op.Kind == IrOperandKind.Expression && op.Expression != null)
        {
            var expr = op.Expression;
            if (expr.DefinesDest) foreach (var k in GetAvailableSsaKeys(expr.Destination)) yield return k;
            foreach (var src in expr.Sources) foreach (var k in GetAvailableSsaKeys(src)) yield return k;
        }
    }

    private static void RenameOperand(IrOperand op, Dictionary<(string, int), int> groups, Dictionary<int, string> names)
    {
        if (op.Kind == IrOperandKind.Expression && op.Expression != null)
        {
            var expr = op.Expression;
            if (expr.DefinesDest) RenameOperand(expr.Destination, groups, names);
            foreach (var src in expr.Sources) RenameOperand(src, groups, names);
            return;
        }

        if (op.Kind == IrOperandKind.Register && op.SsaVersion >= 0)
        {
            var key = ($"r_{IrOperand.GetCanonical(op.Register)}", op.SsaVersion);
            if (groups.TryGetValue(key, out var cid) && names.TryGetValue(cid, out var name))
                op.Name = name;
        }
        else if (op.Kind == IrOperandKind.Memory)
        {
            if (op.MemBase != Register.None)
            {
                var key = ($"r_{IrOperand.GetCanonical(op.MemBase)}", op.MemBaseSsaVersion);
                if (groups.TryGetValue(key, out var cid) && names.TryGetValue(cid, out var name))
                    op.MemBaseName = name;
            }
            if (op.MemIndex != Register.None)
            {
                var key = ($"r_{IrOperand.GetCanonical(op.MemIndex)}", op.MemIndexSsaVersion);
                if (groups.TryGetValue(key, out var cid) && names.TryGetValue(cid, out var name))
                    op.MemIndexName = name;
            }
        }
    }

    private static void EliminateDeadVariables(IrBlock[] blocks)
    {
        var counts = new Dictionary<(Register, int), int>();
        
        void CountUses(IrOperand op)
        {
            if (op.Kind == IrOperandKind.Register && op.SsaVersion >= 0)
            {
                var key = (IrOperand.GetCanonical(op.Register), op.SsaVersion);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            else if (op.Kind == IrOperandKind.Memory)
            {
                if (op.MemBase != Register.None)
                {
                    var key = (IrOperand.GetCanonical(op.MemBase), op.MemBaseSsaVersion);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                if (op.MemIndex != Register.None)
                {
                    var key = (IrOperand.GetCanonical(op.MemIndex), op.MemIndexSsaVersion);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            else if (op.Kind == IrOperandKind.Expression && op.Expression != null)
            {
                foreach (var src in op.Expression.Sources)
                    CountUses(src);
            }
        }

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                foreach (var src in instr.Sources)
                    CountUses(src);
                
                if (instr.Destination.Kind == IrOperandKind.Memory)
                    CountUses(instr.Destination);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var block in blocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr.IsDead) continue;
                    if (instr.DefinesDest && instr.Destination.Kind == IrOperandKind.Register && instr.Destination.SsaVersion >= 0)
                    {
                        var key = (IrOperand.GetCanonical(instr.Destination.Register), instr.Destination.SsaVersion);
                        if (counts.GetValueOrDefault(key) == 0)
                        {
                            if (instr.Opcode == IrOpcode.Call) continue;
                            
                            instr.IsDead = true;
                            changed = true;
                            
                            foreach (var src in instr.Sources)
                                DecreaseCounts(src, counts);
                        }
                    }
                }
            }
        } while (changed);
    }

    private static void DecreaseCounts(IrOperand op, Dictionary<(Register, int), int> counts)
    {
        if (op.Kind == IrOperandKind.Register && op.SsaVersion >= 0)
        {
            var key = (IrOperand.GetCanonical(op.Register), op.SsaVersion);
            if (counts.TryGetValue(key, out int c)) counts[key] = c - 1;
        }
        else if (op.Kind == IrOperandKind.Memory)
        {
            if (op.MemBase != Register.None)
            {
                var key = (IrOperand.GetCanonical(op.MemBase), op.MemBaseSsaVersion);
                if (counts.TryGetValue(key, out int c)) counts[key] = c - 1;
            }
            if (op.MemIndex != Register.None)
            {
                var key = (IrOperand.GetCanonical(op.MemIndex), op.MemIndexSsaVersion);
                if (counts.TryGetValue(key, out int c)) counts[key] = c - 1;
            }
        }
        else if (op.Kind == IrOperandKind.Expression && op.Expression != null)
        {
            foreach (var src in op.Expression.Sources)
                DecreaseCounts(src, counts);
        }
    }

    private static void MergeVariables(IrBlock[] blocks)
    {

        var replacements = new Dictionary<(string, int), IrOperand>();
        bool changed;

        do
        {
            changed = false;
            replacements.Clear();

            foreach (var block in blocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr.IsDead) continue;

                    if (instr.Opcode == IrOpcode.Assign && instr.Sources.Length == 1 &&
                        instr.Condition == IrCondition.None)
                    {
                        var dst = instr.Destination;
                        var src = instr.Sources[0];

                        if (dst.SsaVersion >= 0 && (src.Kind == IrOperandKind.Register || src.Kind == IrOperandKind.StackSlot || src.Kind == IrOperandKind.Constant))
                        {
                            var dstKey = SsaBuilder.GetVarKey(in dst);
                            if (dstKey != null)
                            {
                                replacements[(dstKey, dst.SsaVersion)] = src;
                                instr.IsDead = true;
                                changed = true;
                            }
                        }
                    }
                }
            }

            if (changed)
            {
                foreach (var block in blocks)
                {
                    foreach (var instr in block.Instructions)
                    {
                        if (instr.IsDead) continue;

                        for (int i = 0; i < instr.Sources.Length; i++)
                        {
                            var src = instr.Sources[i];
                            var key = SsaBuilder.GetVarKey(in src);
                            if (key != null && src.SsaVersion >= 0 && replacements.TryGetValue((key, src.SsaVersion), out var replacement))
                            {
                                var newOp = replacement;
                                newOp.Type = src.Type != TypeInfo.Unknown ? src.Type : replacement.Type;
                                instr.Sources[i] = newOp;
                            }
                        }
                    }
                }
            }
        } while (changed);
    }

    private static void FoldExpressions(IrBlock[] blocks)
    {
        var defUse = new Dictionary<(string, int), (IrInstruction Def, int UseCount, IrBlock Block)>();
        
        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.DefinesDest && instr.Destination.SsaVersion >= 0)
                {
                    var key = SsaBuilder.GetVarKey(in instr.Destination);
                    if (key != null)
                        defUse[(key, instr.Destination.SsaVersion)] = (instr, 0, block);
                }
            }
        }

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                foreach (var src in instr.Sources)
                    UpdateUseCount(src, defUse);
                
                if (instr.Destination.Kind == IrOperandKind.Memory)
                    UpdateUseCount(instr.Destination, defUse);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var block in blocks)
            {
                for (int i = 0; i < block.Instructions.Count; i++)
                {
                    var instr = block.Instructions[i];
                    if (instr.IsDead) continue;

                    for (int si = 0; si < instr.Sources.Length; si++)
                    {
                        if (TryFoldOperand(ref instr.Sources[si], defUse, block))
                        {
                            changed = true;
                        }
                    }

                    if (instr.Destination.Kind == IrOperandKind.Memory)
                    {
                        var dest = instr.Destination;
                        if (TryFoldOperand(ref dest, defUse, block))
                        {
                            instr.Destination = dest;
                            changed = true;
                        }
                    }
                }
            }
        } while (changed);
    }

    private static void UpdateUseCount(IrOperand op, Dictionary<(string, int), (IrInstruction Def, int UseCount, IrBlock Block)> defUse)
    {
        if (op.Kind == IrOperandKind.Expression && op.Expression != null)
        {
            foreach (var src in op.Expression.Sources)
                UpdateUseCount(src, defUse);
        }
        else
        {
            var key = SsaBuilder.GetVarKey(in op);
            if (key != null && op.SsaVersion >= 0 && defUse.TryGetValue((key, op.SsaVersion), out var entry))
            {
                defUse[(key, op.SsaVersion)] = (entry.Def, entry.UseCount + 1, entry.Block);
            }
        }
    }

    private static bool TryFoldOperand(ref IrOperand op, Dictionary<(string, int), (IrInstruction Def, int UseCount, IrBlock Block)> defUse, IrBlock currentBlock)
    {
        if (op.Kind == IrOperandKind.Expression && op.Expression != null)
        {
            bool changed = false;
            var expr = op.Expression;
            for (int i = 0; i < expr.Sources.Length; i++)
            {
                if (TryFoldOperand(ref expr.Sources[i], defUse, currentBlock))
                    changed = true;
            }
            return changed;
        }

        var key = SsaBuilder.GetVarKey(in op);
        if (key != null && op.SsaVersion >= 0 && defUse.TryGetValue((key, op.SsaVersion), out var entry))
        {
            if (entry.UseCount == 1 && IsFoldable(entry.Def))
            {
                if (entry.Block != currentBlock && !IsCrossBlockFoldable(entry.Def))
                    return false;

                var def = entry.Def;
                op = new IrOperand
                {
                    Kind = IrOperandKind.Expression,
                    Expression = def,
                    BitSize = op.BitSize,
                    Type = op.Type != TypeInfo.Unknown ? op.Type : def.Destination.Type
                };
                def.IsDead = true;
                return true;
            }
        }
        
        if (op.Kind == IrOperandKind.Memory)
        {
            bool changed = false;
            if (op.MemBase != Register.None)
            {
                var baseOp = IrOperand.Reg(op.MemBase, 64);
                baseOp.SsaVersion = op.MemBaseSsaVersion;
                if (TryFoldOperand(ref baseOp, defUse, currentBlock))
                {
                    if (baseOp.Kind == IrOperandKind.Register)
                    {
                        op.MemBase = baseOp.Register;
                        op.MemBaseSsaVersion = baseOp.SsaVersion;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        return false;
    }

    private static bool IsCrossBlockFoldable(IrInstruction instr)
    {
        return instr.Opcode == IrOpcode.Assign || 
               (instr.Opcode == IrOpcode.Add && instr.Sources.Length == 2 && instr.Sources[1].Kind == IrOperandKind.Constant);
    }

    private static bool IsFoldable(IrInstruction instr)
    {
        if (instr.IsDead) return false;
        
        return instr.Opcode is IrOpcode.Add or IrOpcode.Sub or IrOpcode.Mul or IrOpcode.Div or
               IrOpcode.And or IrOpcode.Or or IrOpcode.Xor or IrOpcode.Shl or IrOpcode.Shr or
               IrOpcode.ZeroExtend or IrOpcode.SignExtend or IrOpcode.Truncate or IrOpcode.Assign or
               IrOpcode.Load;
    }

    private static void FoldApiCalls(IrBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Instructions.Count - 1; i++)
            {
                var instr = block.Instructions[i];
                if (instr.IsDead) continue;
                 
                if ((instr.Opcode == IrOpcode.Assign || instr.Opcode == IrOpcode.Load) && instr.DefinesDest)
                {
                    var src = instr.Sources[0];
                    bool isApi = false;
                    
                    if (src.Kind == IrOperandKind.Memory && src.MemBase == Register.None && src.MemDisplacement != 0) isApi = true;
                    if (src.Kind == IrOperandKind.Constant) isApi = true;
                     
                    if (isApi)
                    {
                        int nextIdx = i + 1;
                        while (nextIdx < block.Instructions.Count && block.Instructions[nextIdx].IsDead) nextIdx++;
                        
                        if (nextIdx < block.Instructions.Count)
                        {
                            var nextInstr = block.Instructions[nextIdx];
                            if (nextInstr.Opcode == IrOpcode.Call && nextInstr.Sources.Length > 0)
                            {
                                var target = nextInstr.Sources[0];
                                if (target.Kind == instr.Destination.Kind && 
                                    target.Register == instr.Destination.Register && 
                                    target.StackOffset == instr.Destination.StackOffset && 
                                    target.SsaVersion == instr.Destination.SsaVersion)
                                {
                                    nextInstr.Sources[0] = src;
                                    instr.IsDead = true;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
