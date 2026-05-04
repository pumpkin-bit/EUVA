// SPDX-License-Identifier: GPL-3.0-or-later

using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public static class NamingConventions
{
    public static string GetRegisterName(Register reg)
    {
        if (reg == Register.None) return "unkn";
        var canonical = IrOperand.GetCanonical(reg);
        return canonical switch
        {
            Register.RAX => "rax",
            Register.RCX => "rcx",
            Register.RDX => "rdx",
            Register.R8 => "r8",
            Register.R9 => "r9",
            Register.RBX => "v1",
            Register.RSI => "v2",
            Register.RDI => "v3",
            Register.RBP => "v4",
            Register.RSP => "rsp",
            Register.R10 => "v5",
            Register.R11 => "v6",
            Register.R12 => "v7",
            Register.R13 => "v8",
            Register.R14 => "v9",
            Register.R15 => "v10",
            _ => reg.ToString().ToLowerInvariant(),
        };
    }

    public static string GetStackVariableName(int offset)
    {
        if (offset < 0)
            return $"var_{-offset:X}";
        if (offset >= 0 && offset < 0x28)
            return $"spill_{offset:X}";
        return $"arg_{offset:X}";
    }

    public static string GetVariableName(IrOperand op)
    {
        if (op.Name != null) return op.Name;

        if (op.Kind == IrOperandKind.Register)
        {
            var canonical = IrOperand.GetCanonical(op.Register);
            if ((canonical == Register.RCX || canonical == Register.RDI) && 
                op.Type.BaseType == PrimitiveType.Struct && op.Type.PointerLevel > 0)
            {
                return "this";
            }
            return GetRegisterName(op.Register);
        }

        return op.Kind switch
        {
            IrOperandKind.StackSlot => GetStackVariableName(op.StackOffset),
            _ => op.SsaVersion != 0 
                ? $"tmp_{Math.Abs(op.SsaVersion)}" : "tmp"
        };
    }
}
