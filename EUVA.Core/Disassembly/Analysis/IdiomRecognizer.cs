// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;

namespace EUVA.Core.Disassembly.Analysis;

public static class IdiomRecognizer
{
    public static void RecognizeIdioms(StructuredNode? root)
    {
        if (root == null) return;
        TransformNode(root);
    }

    private static void TransformNode(StructuredNode node)
    {
        if (node is SequenceNode seq)
        {
            for (int i = 0; i < seq.Children.Count; i++)
            {
                var child = seq.Children[i];
                if (child is DoWhileNode loop)
                {
                    if (TryCollapseMemset(loop, out var memsetCall))
                        seq.Children[i] = memsetCall;
                    else if (TryCollapseMemcpy(loop, out var memcpyCall))
                        seq.Children[i] = memcpyCall;
                }
                TransformNode(seq.Children[i]);
            }
        }
        else if (node is IfNode ifn)
        {
            if (ifn.ThenBody != null) TransformNode(ifn.ThenBody);
            if (ifn.ElseBody != null) TransformNode(ifn.ElseBody);
        }
        else if (node is WhileNode wn) TransformNode(wn.Body);
        else if (node is DoWhileNode dwn) TransformNode(dwn.Body);
        else if (node is ForNode fn) TransformNode(fn.Body);
        else if (node is SwitchNode sn)
        {
            foreach (var c in sn.Cases) TransformNode(c.Body);
            if (sn.DefaultBody != null) TransformNode(sn.DefaultBody);
        }
    }

    private static bool TryCollapseMemset(DoWhileNode loop, out StructuredNode memsetCall)
    {
        memsetCall = null!;
      
        if (loop.Body is SequenceNode seq && seq.Children.Count >= 1)
        {
            IrInstruction? storeInstr = null;
            IrInstruction? incInstr = null;
            
            foreach (var n in seq.Children)
            {
                if (n is BlockNode bb)
                {
                    foreach (var instr in bb.Block.Instructions)
                    {
                        if (instr.IsDead) continue;
                        if (instr.Opcode == IrOpcode.Store) storeInstr = instr;
                        else if (instr.Opcode == IrOpcode.Add && instr.Destination.Kind == IrOperandKind.Register) incInstr = instr;
                    }
                }
            }

            if (storeInstr != null && incInstr != null && loop.ConditionInstr != null)
            {
                if (storeInstr.Destination.Kind == IrOperandKind.Memory && 
                    storeInstr.Destination.MemBase == incInstr.Destination.Register)
                {
                    var ptrArg = IrOperand.Reg(storeInstr.Destination.MemBase, 64);
                    var valArg = storeInstr.Sources[0]; 
                    var countArg = loop.ConditionInstr.Sources.Length >= 2 ? loop.ConditionInstr.Sources[1] : IrOperand.Const(0, 32); 
                    
                    var callInstr = IrInstruction.MakeCall(
                        IrOperand.Reg(Iced.Intel.Register.None, 64), 
                        IrOperand.Expr(IrInstruction.MakeAssign(IrOperand.Reg(Iced.Intel.Register.None, 64), IrOperand.Const(0, 64))), 
                        new[] { ptrArg, valArg, countArg }
                    );
                    
                    callInstr.Comment = "memset_idiom";
                    
                    var newBlock = new IrBlock();
                    newBlock.Instructions.Add(callInstr);
                    memsetCall = new BlockNode(newBlock);
                    return true;
                }
            }
        }
        
        return false;
    }

    private static bool TryCollapseMemcpy(DoWhileNode loop, out StructuredNode memcpyCall)
    {
        memcpyCall = null!;
        return false;
    }
}
