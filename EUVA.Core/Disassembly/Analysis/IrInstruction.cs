// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Disassembly.Analysis;

public sealed class IrInstruction
{
    
    public IrOpcode Opcode;
    public IrOperand Destination;
    public IrOperand[] Sources = Array.Empty<IrOperand>();
    public IrCondition Condition;
    public IrInstruction? ConditionInstr;
    public ulong OriginalAddress;
    public byte BitSize = 64;
    public int[]? PhiSourceBlocks;
    public bool HasSideEffects => Opcode is IrOpcode.Store or IrOpcode.Call or IrOpcode.Return;
    public bool DefinesDest => Opcode is not (IrOpcode.Cmp or IrOpcode.Test or IrOpcode.Branch
        or IrOpcode.CondBranch or IrOpcode.Store or IrOpcode.Nop);

    public bool IsDead;
    public string? Comment;

    public static IrInstruction MakeAssign(IrOperand dst, IrOperand src, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Assign,
        Destination = dst,
        Sources = new[] { src },
        BitSize = dst.BitSize,
        OriginalAddress = addr,
    };

    public static IrInstruction MakeBinOp(IrOpcode op, IrOperand dst, IrOperand left, IrOperand right, ulong addr = 0) => new()
    {
        Opcode = op,
        Destination = dst,
        Sources = new[] { left, right },
        BitSize = dst.BitSize,
        OriginalAddress = addr,
    };

    public static IrInstruction MakeUnaryOp(IrOpcode op, IrOperand dst, IrOperand src, ulong addr = 0) => new()
    {
        Opcode = op,
        Destination = dst,
        Sources = new[] { src },
        BitSize = dst.BitSize,
        OriginalAddress = addr,
    };

    public static IrInstruction MakeLoad(IrOperand dst, IrOperand memSrc, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Load,
        Destination = dst,
        Sources = new[] { memSrc },
        BitSize = dst.BitSize,
        OriginalAddress = addr,
    };

    public static IrInstruction MakeStore(IrOperand memDst, IrOperand value, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Store,
        Destination = memDst,
        Sources = new[] { value },
        BitSize = value.BitSize,
        OriginalAddress = addr,
    };

    public static IrInstruction MakeCall(IrOperand dst, IrOperand target, IrOperand[] args, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Call,
        Destination = dst,
        Sources = new[] { target }.Concat(args).ToArray(),
        OriginalAddress = addr,
    };

    public static IrInstruction MakeReturn(IrOperand? value = null, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Return,
        Sources = value.HasValue ? new[] { value.Value } : Array.Empty<IrOperand>(),
        OriginalAddress = addr,
    };

    public static IrInstruction MakeBranch(int targetBlock, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Branch,
        Sources = new[] { IrOperand.BlockLabel(targetBlock) },
        OriginalAddress = addr,
    };

    public static IrInstruction MakeCondBranch(IrCondition cond, int trueBlock, int falseBlock, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.CondBranch,
        Condition = cond,
        Sources = new[] { IrOperand.BlockLabel(trueBlock), IrOperand.BlockLabel(falseBlock) },
        OriginalAddress = addr,
    };

    public static IrInstruction MakePhi(IrOperand dst, IrOperand[] sources, int[] sourceBlocks) => new()
    {
        Opcode = IrOpcode.Phi,
        Destination = dst,
        Sources = sources,
        PhiSourceBlocks = sourceBlocks,
        BitSize = dst.BitSize,
    };

    public static IrInstruction MakeCmp(IrOperand left, IrOperand right, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Cmp,
        Destination = IrOperand.FlagReg(),
        Sources = new[] { left, right },
        OriginalAddress = addr,
    };

    public static IrInstruction MakeTest(IrOperand left, IrOperand right, ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Test,
        Destination = IrOperand.FlagReg(),
        Sources = new[] { left, right },
        OriginalAddress = addr,
    };

    public static IrInstruction MakeNop(ulong addr = 0) => new()
    {
        Opcode = IrOpcode.Nop,
        OriginalAddress = addr,
    };

    public override string ToString()
    {
        var dst = DefinesDest ? $"{Destination} = " : "";
        var srcs = string.Join(", ", Sources.Select(s => s.ToString()));
        var cond = Condition != IrCondition.None ? $" [{Condition}]" : "";
        return $"{dst}{Opcode}{cond}({srcs})";
    }
}
