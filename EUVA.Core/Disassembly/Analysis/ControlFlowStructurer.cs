// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace EUVA.Core.Disassembly.Analysis;

public abstract class StructuredNode
{
    public int BlockIndex = -1;
}


public sealed class SequenceNode : StructuredNode
{
    public List<StructuredNode> Children = new();
}

public sealed class BlockNode : StructuredNode
{
    public IrBlock Block;
    public BlockNode(IrBlock block) { Block = block; BlockIndex = block.Index; }
}

public sealed class IfNode : StructuredNode
{
    public IrCondition Condition;
    public IrInstruction? ConditionInstr; 
    public StructuredNode ThenBody = null!;
    public StructuredNode? ElseBody;
}


public sealed class WhileNode : StructuredNode
{
    public IrCondition Condition;
    public IrInstruction? ConditionInstr;
    public StructuredNode Body = null!;
}


public sealed class ForNode : StructuredNode
{
    public bool IsDoWhile;
    public IrInstruction? InitInstr;
    public IrCondition Condition;
    public IrInstruction? ConditionInstr;
    public IrInstruction? StepInstr;
    public StructuredNode Body = null!;
}


public sealed class DoWhileNode : StructuredNode
{
    public IrCondition Condition;
    public IrInstruction? ConditionInstr;
    public StructuredNode Body = null!;
}


public sealed class SwitchNode : StructuredNode
{
    public IrOperand SwitchValue;
    public List<(long CaseValue, StructuredNode Body)> Cases = new();
    public StructuredNode? DefaultBody;
}

public sealed class GotoNode : StructuredNode
{
    public int TargetBlockIndex;
}

public sealed class ReturnNode : StructuredNode
{
    public IrOperand? ReturnValue;
}

public static class ControlFlowStructurer
{
    public static StructuredNode Structure(IrBlock[] blocks, List<LoopInfo> loops)
    {
        var visited = new HashSet<int>();
        var loopMap = new Dictionary<int, LoopInfo>();
        foreach (var loop in loops)
            loopMap.TryAdd(loop.Header, loop);

        var root = StructureRegion(blocks, 0, blocks.Length, visited, loopMap);
        if (root != null) OptimizeAst(root);
        return root ?? new SequenceNode();
    }

    private static StructuredNode OptimizeAst(StructuredNode node)
    {
        SimplifyNodeCondition(node);

        if (node is SequenceNode seq)
        {
            for (int i = 0; i < seq.Children.Count; i++)
            {
                seq.Children[i] = OptimizeAst(seq.Children[i]);

                    if (seq.Children[i] is WhileNode wn)
                    {
                        if (wn.Condition == IrCondition.NotEqual && 
                            wn.ConditionInstr != null && 
                            wn.ConditionInstr.Sources.Length >= 2 && 
                            wn.ConditionInstr.Sources[1].Kind == IrOperandKind.Constant && 
                            wn.ConditionInstr.Sources[1].ConstantValue == 0)
                        {
                            var condVar = wn.ConditionInstr.Sources[0];
                            (var init, var step) = FindForLoopComponents(seq, i, condVar, wn.Body);

                            if (init != null && step != null)
                            {
                                seq.Children[i] = new ForNode
                                {
                                    InitInstr = init,
                                    Condition = wn.Condition,
                                    ConditionInstr = wn.ConditionInstr,
                                    StepInstr = step,
                                    Body = wn.Body
                                };
                                init.IsDead = true;
                                step.IsDead = true;
                            }
                        }
                    }
                    else if (seq.Children[i] is DoWhileNode dwn)
                    {
                        if (dwn.Condition == IrCondition.NotEqual && 
                            dwn.ConditionInstr != null && 
                            dwn.ConditionInstr.Sources.Length >= 2 && 
                            dwn.ConditionInstr.Sources[1].Kind == IrOperandKind.Constant && 
                            dwn.ConditionInstr.Sources[1].ConstantValue == 0)
                        {
                            var condVar = dwn.ConditionInstr.Sources[0];
                            (var init, var step) = FindForLoopComponents(seq, i, condVar, dwn.Body);

                            if (init != null && step != null)
                            {
                                seq.Children[i] = new ForNode
                                {
                                    IsDoWhile = true,
                                    InitInstr = init,
                                    Condition = dwn.Condition,
                                    ConditionInstr = dwn.ConditionInstr,
                                    StepInstr = step,
                                    Body = dwn.Body
                                };
                                init.IsDead = true;
                                step.IsDead = true;
                            }
                        }
                    }
            }
            return seq;
        }
        else if (node is IfNode ifn)
        {
            ifn.ThenBody = OptimizeAst(ifn.ThenBody);
            if (ifn.ElseBody != null) ifn.ElseBody = OptimizeAst(ifn.ElseBody);

            if (IsAlwaysTrue(ifn.Condition, ifn.ConditionInstr))
            {
                return ifn.ThenBody;
            }
            if (IsAlwaysFalse(ifn.Condition, ifn.ConditionInstr))
            {
                return ifn.ElseBody ?? new SequenceNode();
            }
            return ifn;
        }
        else if (node is WhileNode wn2) 
        {
            wn2.Body = OptimizeAst(wn2.Body);
            return wn2;
        }
        else if (node is DoWhileNode dwn) 
        {
            dwn.Body = OptimizeAst(dwn.Body);
            return dwn;
        }
        else if (node is ForNode fn) 
        {
            fn.Body = OptimizeAst(fn.Body);
            return fn;
        }
        else if (node is SwitchNode sn)
        {
            for (int i = 0; i < sn.Cases.Count; i++)
            {
                var (val, body) = sn.Cases[i];
                sn.Cases[i] = (val, OptimizeAst(body));
            }
            if (sn.DefaultBody != null) sn.DefaultBody = OptimizeAst(sn.DefaultBody);
            return sn;
        }
        return node;
    }

    private static (IrInstruction? Init, IrInstruction? Step) FindForLoopComponents(SequenceNode seq, int loopIdx, IrOperand condVar, StructuredNode body)
    {
        if (condVar.Kind != IrOperandKind.Register && condVar.Kind != IrOperandKind.StackSlot) return (null, null);
        string? vName = condVar.Name;
        if (string.IsNullOrEmpty(vName)) return (null, null);

        IrInstruction? init = null;
        for (int k = loopIdx - 1; k >= 0; k--)
        {
            init = FindInNode(seq.Children[k], condVar);
            if (init != null) break;
        }

        IrInstruction? step = FindInNode(body, condVar);

        if (step != null && (step.Opcode != IrOpcode.Add && step.Opcode != IrOpcode.Sub && step.Opcode != IrOpcode.Assign))
            step = null;

        return (init, step);
    }

    private static IrInstruction? FindInNode(StructuredNode node, IrOperand target)
    {
        if (node is BlockNode bn)
        {
            return bn.Block.Instructions.LastOrDefault(ins => !ins.IsDead && ins.DefinesDest && ins.Destination.SameLocation(target));
        }
        else if (node is SequenceNode sn)
        {
            for (int i = sn.Children.Count - 1; i >= 0; i--)
            {
                var found = FindInNode(sn.Children[i], target);
                if (found != null) return found;
            }
        }
        return null;
    }


    private static bool IsAlwaysTrue(IrCondition cond, IrInstruction? instr)
    {
        if (instr == null) return false;
        if (instr.Opcode == IrOpcode.Cmp && instr.Sources.Length >= 2)
        {
            var left = instr.Sources[0];
            var right = instr.Sources[1];

            if (left.Kind == IrOperandKind.Constant && right.Kind == IrOperandKind.Constant)
            {
                long l = left.ConstantValue;
                long r = right.ConstantValue;
                return cond switch
                {
                    IrCondition.Equal => l == r,
                    IrCondition.NotEqual => l != r,
                    IrCondition.SignedLess => l < r,
                    IrCondition.SignedLessEq => l <= r,
                    IrCondition.SignedGreater => l > r,
                    IrCondition.SignedGreaterEq => l >= r,
                    IrCondition.UnsignedBelow => (ulong)l < (ulong)r,
                    IrCondition.UnsignedBelowEq => (ulong)l <= (ulong)r,
                    IrCondition.UnsignedAbove => (ulong)l > (ulong)r,
                    IrCondition.UnsignedAboveEq => (ulong)l >= (ulong)r,
                    _ => false
                };
            }

            if (right.Kind == IrOperandKind.Constant && right.ConstantValue == 0)
            {
                if (cond == IrCondition.UnsignedAboveEq) return true;
            }
        }
        return false;
    }

    private static bool IsAlwaysFalse(IrCondition cond, IrInstruction? instr)
    {
        if (instr == null) return false;
        if (instr.Opcode == IrOpcode.Cmp && instr.Sources.Length >= 2)
        {
            var left = instr.Sources[0];
            var right = instr.Sources[1];

            if (left.Kind == IrOperandKind.Constant && right.Kind == IrOperandKind.Constant)
            {
                long l = left.ConstantValue;
                long r = right.ConstantValue;
                return cond switch
                {
                    IrCondition.Equal => l != r,
                    IrCondition.NotEqual => l == r,
                    IrCondition.SignedLess => l >= r,
                    IrCondition.SignedLessEq => l > r,
                    IrCondition.SignedGreater => l <= r,
                    IrCondition.SignedGreaterEq => l < r,
                    IrCondition.UnsignedBelow => (ulong)l >= (ulong)r,
                    IrCondition.UnsignedBelowEq => (ulong)l > (ulong)r,
                    IrCondition.UnsignedAbove => (ulong)l <= (ulong)r,
                    IrCondition.UnsignedAboveEq => (ulong)l < (ulong)r,
                    _ => false
                };
            }

            if (right.Kind == IrOperandKind.Constant && right.ConstantValue == 0)
            {
                if (cond == IrCondition.UnsignedBelow) return true;
            }
        }
        return false;
    }

    private static void SimplifyNodeCondition(StructuredNode node)
    {
        IrCondition cond = IrCondition.None;
        IrInstruction? instr = null;

        if (node is IfNode ifn) { cond = ifn.Condition; instr = ifn.ConditionInstr; }
        else if (node is WhileNode wn) { cond = wn.Condition; instr = wn.ConditionInstr; }
        else if (node is ForNode fn) { cond = fn.Condition; instr = fn.ConditionInstr; }
        else if (node is DoWhileNode dwn) { cond = dwn.Condition; instr = dwn.ConditionInstr; }

        if (instr == null || cond == IrCondition.None) return;

        if (instr.Opcode == IrOpcode.Cmp && instr.Sources.Length >= 2)
        {
            var left = instr.Sources[0];
            var right = instr.Sources[1];

            if (left.Kind == IrOperandKind.Constant && right.Kind != IrOperandKind.Constant)
            {
                instr.Sources[0] = right;
                instr.Sources[1] = left;
                cond = ReverseConditionOperator(cond);
                (left, right) = (instr.Sources[0], instr.Sources[1]);
            }


            if (right.Kind == IrOperandKind.Constant && right.ConstantValue == 0)
            {
                if (cond == IrCondition.UnsignedBelowEq) 
                    cond = IrCondition.Equal;
                else if (cond == IrCondition.UnsignedAbove) 
                    cond = IrCondition.NotEqual;
            }
        }

        if (node is IfNode ifn2) ifn2.Condition = cond;
        else if (node is WhileNode wn2) wn2.Condition = cond;
        else if (node is ForNode fn2) fn2.Condition = cond;
        else if (node is DoWhileNode dwn2) dwn2.Condition = cond;
    }

    private static IrCondition ReverseConditionOperator(IrCondition cond) => cond switch
    {
        IrCondition.SignedLess => IrCondition.SignedGreater,
        IrCondition.SignedLessEq => IrCondition.SignedGreaterEq,
        IrCondition.SignedGreater => IrCondition.SignedLess,
        IrCondition.SignedGreaterEq => IrCondition.SignedLessEq,
        IrCondition.UnsignedBelow => IrCondition.UnsignedAbove,
        IrCondition.UnsignedBelowEq => IrCondition.UnsignedAboveEq,
        IrCondition.UnsignedAbove => IrCondition.UnsignedBelow,
        IrCondition.UnsignedAboveEq => IrCondition.UnsignedBelowEq,
        _ => cond
    };

    private static StructuredNode? StructureRegion(IrBlock[] blocks, int start, int end,
        HashSet<int> visited, Dictionary<int, LoopInfo> loopMap)
    {
        var seq = new SequenceNode();

        for (int i = start; i < end; i++)
        {
            if (i >= blocks.Length || visited.Contains(i)) continue;

            var block = blocks[i];

         
            if (loopMap.TryGetValue(i, out var loop))
            {
                var loopNode = StructureLoop(blocks, loop, visited, loopMap);
                if (loopNode != null)
                    seq.Children.Add(loopNode);
                continue;
            }

            visited.Add(i);

        
            if (block.IsReturn || block.Terminator?.Opcode == IrOpcode.Return)
            {
                var retInstr = block.Terminator;
                seq.Children.Add(new BlockNode(block));
                if (retInstr?.Opcode == IrOpcode.Return && retInstr.Sources.Length > 0)
                {
                    seq.Children.Add(new ReturnNode { ReturnValue = retInstr.Sources[0] });
                }
                else
                {
                    seq.Children.Add(new ReturnNode());
                }
                continue;
            }

           
            if (block.EndsWithCondBranch && block.Successors.Count == 2)
            {
                var ifNode = StructureIf(blocks, block, visited, loopMap);
                if (ifNode != null)
                {
                    seq.Children.Add(new BlockNode(block)); 
                    seq.Children.Add(ifNode);
                    continue;
                }
            }

            seq.Children.Add(new BlockNode(block));

          
            if (block.Terminator?.Opcode == IrOpcode.Branch && block.Successors.Count == 1)
            {
                int target = block.Successors[0];
                if (!visited.Contains(target) && target > i)
                {
                   
                }
                else if (!visited.Contains(target))
                {
                    seq.Children.Add(new GotoNode { TargetBlockIndex = target });
                }
            }
        }

        if (seq.Children.Count == 0) return null;
        if (seq.Children.Count == 1) return seq.Children[0];
        return seq;
    }

    private static IfNode? StructureIf(IrBlock[] blocks, IrBlock condBlock,
        HashSet<int> visited, Dictionary<int, LoopInfo> loopMap)
    {
        if (condBlock.Successors.Count != 2) return null;

        int fallthroughIdx = condBlock.Successors[0]; 
        int takenIdx = condBlock.Successors[1];

       
        if (fallthroughIdx == takenIdx)
        {
            return null; 
        }

        var terminator = condBlock.Terminator!;
        var condition = terminator.Condition;

        IrInstruction? condInstr = condBlock.LastCmpInstr;

       
        int mergePoint = FindMergePoint(blocks, fallthroughIdx, takenIdx);

        if (mergePoint >= 0)
        {
          
            bool thenVisitable = takenIdx < blocks.Length && !visited.Contains(takenIdx);
            bool elseVisitable = fallthroughIdx < blocks.Length && !visited.Contains(fallthroughIdx);

            StructuredNode? thenBody = null;
            StructuredNode? elseBody = null;

            if (thenVisitable && takenIdx != mergePoint)
                thenBody = StructureRegion(blocks, takenIdx, mergePoint, visited, loopMap);

            if (elseVisitable && fallthroughIdx != mergePoint)
                elseBody = StructureRegion(blocks, fallthroughIdx, mergePoint, visited, loopMap);

            if (thenBody == null && elseBody == null)
                return null;

         
            if (thenBody == null)
            {
                thenBody = elseBody;
                elseBody = null;
                condition = InvertCondition(condition);
            }

            return new IfNode
            {
                Condition = condition,
                ConditionInstr = condInstr,
                ThenBody = thenBody!,
                ElseBody = elseBody,
            };
        }

        if (takenIdx < blocks.Length && !visited.Contains(takenIdx))
        {
            visited.Add(takenIdx);
            return new IfNode
            {
                Condition = condition,
                ConditionInstr = condInstr,
                ThenBody = new BlockNode(blocks[takenIdx]),
            };
        }

        return null;
    }

    private static StructuredNode? StructureLoop(IrBlock[] blocks, LoopInfo loop,
        HashSet<int> visited, Dictionary<int, LoopInfo> loopMap)
    {
        var header = blocks[loop.Header];

        if (loop.IsDoWhile)
        {
            var bodyBlocks = new List<int>(loop.Body);
            bodyBlocks.Sort();

            foreach (int b in bodyBlocks)
                visited.Add(b);

            var backBlock = blocks[loop.BackEdgeSource];
            IrInstruction? condInstr = null;
            IrCondition cond = IrCondition.None;

            if (backBlock.Terminator?.Opcode == IrOpcode.CondBranch)
            {
                cond = backBlock.Terminator.Condition;
                condInstr = backBlock.LastCmpInstr;
            }

            var bodyNode = BuildLoopBody(blocks, bodyBlocks, loopMap, loop.Header);

            return new DoWhileNode
            {
                Condition = cond,
                ConditionInstr = condInstr,
                Body = bodyNode ?? new SequenceNode(),
            };
        }
        else
        {
          
            var bodyBlocks = new List<int>(loop.Body);
            bodyBlocks.Sort();

            foreach (int b in bodyBlocks)
                visited.Add(b);

            IrCondition cond = IrCondition.None;
            IrInstruction? condInstr = null;

            if (header.EndsWithCondBranch)
            {
                cond = header.Terminator!.Condition;
                condInstr = header.LastCmpInstr;
            }

            var bodyNode = BuildLoopBody(blocks, bodyBlocks, loopMap, loop.Header);

            return new WhileNode
            {
                Condition = cond,
                ConditionInstr = condInstr,
                Body = bodyNode ?? new SequenceNode(),
            };
        }
    }

    private static StructuredNode? BuildLoopBody(IrBlock[] blocks, List<int> bodyBlocks,
        Dictionary<int, LoopInfo> loopMap, int currentHeader)
    {
        if (bodyBlocks.Count == 0) return null;
        
        var innerVisited = new HashSet<int>();
        for (int i = 0; i < blocks.Length; i++)
        {
            if (!bodyBlocks.Contains(i))
                innerVisited.Add(i);
        }

        var innerLoopMap = new Dictionary<int, LoopInfo>(loopMap);
        innerLoopMap.Remove(currentHeader);

        int start = bodyBlocks.Min();
        int end = bodyBlocks.Max() + 1;

        return StructureRegion(blocks, start, end, innerVisited, innerLoopMap);
    }

    private static int FindMergePoint(IrBlock[] blocks, int branch1, int branch2)
    {
        var reachable1 = new HashSet<int>();
        var reachable2 = new HashSet<int>();

        CollectReachable(blocks, branch1, reachable1, 100);
        CollectReachable(blocks, branch2, reachable2, 100);

        int best = int.MaxValue;
        foreach (int r in reachable1)
        {
            if (reachable2.Contains(r) && r < best)
                best = r;
        }

        return best == int.MaxValue ? -1 : best;
    }

    private static void CollectReachable(IrBlock[] blocks, int start, HashSet<int> reachable, int maxDepth)
    {
        if (maxDepth <= 0 || start < 0 || start >= blocks.Length) return;
        if (!reachable.Add(start)) return;

        foreach (int s in blocks[start].Successors)
            CollectReachable(blocks, s, reachable, maxDepth - 1);
    }


    public static IrCondition InvertCondition(IrCondition c) => c switch
    {
        IrCondition.Equal => IrCondition.NotEqual,
        IrCondition.NotEqual => IrCondition.Equal,
        IrCondition.SignedLess => IrCondition.SignedGreaterEq,
        IrCondition.SignedLessEq => IrCondition.SignedGreater,
        IrCondition.SignedGreater => IrCondition.SignedLessEq,
        IrCondition.SignedGreaterEq => IrCondition.SignedLess,
        IrCondition.UnsignedBelow => IrCondition.UnsignedAboveEq,
        IrCondition.UnsignedBelowEq => IrCondition.UnsignedAbove,
        IrCondition.UnsignedAbove => IrCondition.UnsignedBelowEq,
        IrCondition.UnsignedAboveEq => IrCondition.UnsignedBelow,
        IrCondition.Sign => IrCondition.NotSign,
        IrCondition.NotSign => IrCondition.Sign,
        IrCondition.Overflow => IrCondition.NotOverflow,
        IrCondition.NotOverflow => IrCondition.Overflow,
        _ => c,
    };
}
