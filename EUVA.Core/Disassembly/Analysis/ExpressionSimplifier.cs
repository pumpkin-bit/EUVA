// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Disassembly.Analysis;

public static class ExpressionSimplifier
{
    
    public static int Simplify(IrBlock[] blocks)
    {
        int simplified = 0;

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead || !instr.DefinesDest) continue;

                if (TrySimplify(instr))
                    simplified++;
            }
        }

        return simplified;
    }

    private static bool TrySimplify(IrInstruction instr)
    {
        if (instr.Sources.Length == 2)
        {
            ref var left = ref instr.Sources[0];
            ref var right = ref instr.Sources[1];

            bool leftConst = left.Kind == IrOperandKind.Constant;
            bool rightConst = right.Kind == IrOperandKind.Constant;

            switch (instr.Opcode)
            {
                
                case IrOpcode.Add when rightConst && right.ConstantValue == 0:
                    ConvertToAssign(instr, left);
                    return true;

                
                case IrOpcode.Add when leftConst && left.ConstantValue == 0:
                    ConvertToAssign(instr, right);
                    return true;

                
                case IrOpcode.Sub when rightConst && right.ConstantValue == 0:
                    ConvertToAssign(instr, left);
                    return true;

                
                case IrOpcode.Sub when left.SameLocation(right) && left.SsaVersion == right.SsaVersion && left.SsaVersion >= 0:
                    ConvertToConst(instr, 0);
                    return true;

                
                case IrOpcode.Mul or IrOpcode.IMul when rightConst && right.ConstantValue == 1:
                    ConvertToAssign(instr, left);
                    return true;

                
                case IrOpcode.Mul or IrOpcode.IMul when leftConst && left.ConstantValue == 1:
                    ConvertToAssign(instr, right);
                    return true;

                
                case IrOpcode.Mul or IrOpcode.IMul when (rightConst && right.ConstantValue == 0) || (leftConst && left.ConstantValue == 0):
                    ConvertToConst(instr, 0);
                    return true;

                
                case IrOpcode.And when (rightConst && right.ConstantValue == 0) || (leftConst && left.ConstantValue == 0):
                    ConvertToConst(instr, 0);
                    return true;

                
                case IrOpcode.And when rightConst && IsAllOnes(right.ConstantValue, instr.BitSize):
                    ConvertToAssign(instr, left);
                    return true;

                
                case IrOpcode.Or when rightConst && right.ConstantValue == 0:
                    ConvertToAssign(instr, left);
                    return true;

                
                case IrOpcode.Or when leftConst && left.ConstantValue == 0:
                    ConvertToAssign(instr, right);
                    return true;

                case IrOpcode.Or or IrOpcode.And when left.SameLocation(right) && left.SsaVersion == right.SsaVersion && left.SsaVersion >= 0:
                    ConvertToAssign(instr, left);
                    return true;

                case IrOpcode.Xor when rightConst && right.ConstantValue == 0:
                    ConvertToAssign(instr, left);
                    return true;

                case IrOpcode.Xor when left.SameLocation(right) && left.SsaVersion == right.SsaVersion && left.SsaVersion >= 0:
                    ConvertToConst(instr, 0);
                    return true;

                
                case IrOpcode.Shl or IrOpcode.Shr or IrOpcode.Sar when rightConst && right.ConstantValue == 0:
                    ConvertToAssign(instr, left);
                    return true;

                
                case IrOpcode.Mul or IrOpcode.IMul when rightConst && IsPowerOfTwo(right.ConstantValue, out int shift):
                    instr.Opcode = IrOpcode.Shl;
                    instr.Sources[1] = IrOperand.Const(shift, 8);
                    return true;
            }
        }

        if (instr.Sources.Length == 1)
        {
            ref var src = ref instr.Sources[0];
  
        }

        return false;
    }

    private static void ConvertToAssign(IrInstruction instr, IrOperand src)
    {
        instr.Opcode = IrOpcode.Assign;
        instr.Sources = new[] { src };
    }

    private static void ConvertToConst(IrInstruction instr, long value)
    {
        instr.Opcode = IrOpcode.Assign;
        instr.Sources = new[] { IrOperand.Const(value, instr.BitSize) };
    }

    private static bool IsAllOnes(long value, byte bitSize) => bitSize switch
    {
        8 => (value & 0xFF) == 0xFF,
        16 => (value & 0xFFFF) == 0xFFFF,
        32 => (value & 0xFFFFFFFF) == 0xFFFFFFFF,
        64 => value == -1,
        _ => false,
    };

    private static bool IsPowerOfTwo(long value, out int shift)
    {
        shift = 0;
        if (value <= 0) return false;
        long v = value;
        while (v > 1)
        {
            if ((v & 1) != 0) return false;
            v >>= 1;
            shift++;
        }
        return true;
    }
}
