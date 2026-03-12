// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public sealed class IrLifter
{
    private readonly int _bitness;
    private readonly Dictionary<ulong, string> _imports;
    private readonly Dictionary<long, string> _strings;
    private int _nextTempId;

    public IrLifter(int bitness, Dictionary<ulong, string>? imports = null, Dictionary<long, string>? strings = null)
    {
        _bitness = bitness;
        _imports = imports ?? new();
        _strings = strings ?? new();
    }

    
    private IrOperand MakeTemp(byte bitSize)
    {
        var op = IrOperand.Reg(Register.None, bitSize);
        op.SsaVersion = --_nextTempId; 
        return op;
    }


    public List<IrInstruction> LiftBlock(ReadOnlySpan<Instruction> instructions)
    {
        var result = new List<IrInstruction>(instructions.Length);

        for (int i = 0; i < instructions.Length; i++)
        {
            ref readonly var instr = ref instructions[i];
            var lifted = LiftInstruction(in instr, i, instructions);
            if (lifted != null)
                result.AddRange(lifted);
        }

        return result;
    }

    public unsafe List<IrInstruction> LiftRawBlock(byte* data, int length, long baseAddress)
    {
        if (length <= 0) return new List<IrInstruction>();

        var reader = new UnsafePointerCodeReader();
        reader.Reset(data, length);
        var decoder = Decoder.Create(_bitness, reader, (ulong)baseAddress);
        ulong endIP = (ulong)baseAddress + (ulong)length;

        var instructions = new List<Instruction>(32);
        while (decoder.IP < endIP)
        {
            decoder.Decode(out var instr);
            if (!instr.IsInvalid)
                instructions.Add(instr);
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(instructions);
        return LiftBlock(span);
    }

    private IrInstruction[]? LiftInstruction(in Instruction instr, int idx, ReadOnlySpan<Instruction> ctx)
    {
        var addr = instr.IP;

        return instr.Mnemonic switch
        {
            
            Mnemonic.Mov => LiftMov(in instr, addr),
            Mnemonic.Movzx => LiftMovExtend(in instr, addr, IrOpcode.ZeroExtend),
            Mnemonic.Movsx or Mnemonic.Movsxd => LiftMovExtend(in instr, addr, IrOpcode.SignExtend),
            Mnemonic.Lea => LiftLea(in instr, addr),
            Mnemonic.Xchg => LiftXchg(in instr, addr),
            Mnemonic.Cmovae or Mnemonic.Cmova or Mnemonic.Cmovb or Mnemonic.Cmovbe
                or Mnemonic.Cmove or Mnemonic.Cmovne or Mnemonic.Cmovg or Mnemonic.Cmovge
                or Mnemonic.Cmovl or Mnemonic.Cmovle or Mnemonic.Cmovs or Mnemonic.Cmovns
                => LiftCmov(in instr, addr),

            
            Mnemonic.Add => LiftBinOp(in instr, IrOpcode.Add, addr),
            Mnemonic.Sub => LiftBinOp(in instr, IrOpcode.Sub, addr),
            Mnemonic.Imul => LiftImul(in instr, addr),
            Mnemonic.Mul => LiftMul(in instr, addr),
            Mnemonic.Idiv => LiftDiv(in instr, IrOpcode.IDiv, addr),
            Mnemonic.Div => LiftDiv(in instr, IrOpcode.Div, addr),
            Mnemonic.Inc => LiftIncDec(in instr, IrOpcode.Add, addr),
            Mnemonic.Dec => LiftIncDec(in instr, IrOpcode.Sub, addr),
            Mnemonic.Neg => LiftUnaryOp(in instr, IrOpcode.Neg, addr),
            Mnemonic.Cdq or Mnemonic.Cqo or Mnemonic.Cdqe or Mnemonic.Cwde => null, 

            
            Mnemonic.And => LiftBinOp(in instr, IrOpcode.And, addr),
            Mnemonic.Or => LiftBinOp(in instr, IrOpcode.Or, addr),
            Mnemonic.Xor => LiftXor(in instr, addr),
            Mnemonic.Not => LiftUnaryOp(in instr, IrOpcode.Not, addr),
            Mnemonic.Shl or Mnemonic.Sal => LiftBinOp(in instr, IrOpcode.Shl, addr),
            Mnemonic.Shr => LiftBinOp(in instr, IrOpcode.Shr, addr),
            Mnemonic.Sar => LiftBinOp(in instr, IrOpcode.Sar, addr),
            Mnemonic.Rol => LiftBinOp(in instr, IrOpcode.Rol, addr),
            Mnemonic.Ror => LiftBinOp(in instr, IrOpcode.Ror, addr),

            
            Mnemonic.Cmp => LiftCmp(in instr, addr),
            Mnemonic.Test => LiftTest(in instr, addr),

            
            Mnemonic.Push => LiftPush(in instr, addr),
            Mnemonic.Pop => LiftPop(in instr, addr),

            
            Mnemonic.Call => LiftCall(in instr, addr),
            Mnemonic.Ret or Mnemonic.Retf => LiftReturn(in instr, addr),
            Mnemonic.Jmp => LiftJmp(in instr, addr),

            
            Mnemonic.Je => LiftJcc(in instr, IrCondition.Equal, addr),
            Mnemonic.Jne => LiftJcc(in instr, IrCondition.NotEqual, addr),
            Mnemonic.Jl => LiftJcc(in instr, IrCondition.SignedLess, addr),
            Mnemonic.Jle => LiftJcc(in instr, IrCondition.SignedLessEq, addr),
            Mnemonic.Jg => LiftJcc(in instr, IrCondition.SignedGreater, addr),
            Mnemonic.Jge => LiftJcc(in instr, IrCondition.SignedGreaterEq, addr),
            Mnemonic.Jb => LiftJcc(in instr, IrCondition.UnsignedBelow, addr),
            Mnemonic.Jbe => LiftJcc(in instr, IrCondition.UnsignedBelowEq, addr),
            Mnemonic.Ja => LiftJcc(in instr, IrCondition.UnsignedAbove, addr),
            Mnemonic.Jae => LiftJcc(in instr, IrCondition.UnsignedAboveEq, addr),
            Mnemonic.Js => LiftJcc(in instr, IrCondition.Sign, addr),
            Mnemonic.Jns => LiftJcc(in instr, IrCondition.NotSign, addr),
            Mnemonic.Jo => LiftJcc(in instr, IrCondition.Overflow, addr),
            Mnemonic.Jno => LiftJcc(in instr, IrCondition.NotOverflow, addr),

            Mnemonic.Sete => LiftSetcc(in instr, IrCondition.Equal, addr),
            Mnemonic.Setne => LiftSetcc(in instr, IrCondition.NotEqual, addr),
            Mnemonic.Setl => LiftSetcc(in instr, IrCondition.SignedLess, addr),
            Mnemonic.Setle => LiftSetcc(in instr, IrCondition.SignedLessEq, addr),
            Mnemonic.Setg => LiftSetcc(in instr, IrCondition.SignedGreater, addr),
            Mnemonic.Setge => LiftSetcc(in instr, IrCondition.SignedGreaterEq, addr),
            Mnemonic.Setb => LiftSetcc(in instr, IrCondition.UnsignedBelow, addr),
            Mnemonic.Setbe => LiftSetcc(in instr, IrCondition.UnsignedBelowEq, addr),
            Mnemonic.Seta => LiftSetcc(in instr, IrCondition.UnsignedAbove, addr),
            Mnemonic.Setae => LiftSetcc(in instr, IrCondition.UnsignedAboveEq, addr),

            
            Mnemonic.Nop or Mnemonic.Fnop or Mnemonic.Int3 or Mnemonic.Endbr32 or Mnemonic.Endbr64 => null,

            
            Mnemonic.Leave => LiftLeave(addr),

            
            Mnemonic.Movsb or Mnemonic.Movsw or Mnemonic.Movsd or Mnemonic.Movsq
                or Mnemonic.Stosb or Mnemonic.Stosw or Mnemonic.Stosd or Mnemonic.Stosq
                or Mnemonic.Scasb or Mnemonic.Scasw or Mnemonic.Scasd or Mnemonic.Scasq
                or Mnemonic.Lodsb or Mnemonic.Lodsw or Mnemonic.Lodsd or Mnemonic.Lodsq
                => LiftStringInstruction(in instr, addr),

            
            Mnemonic.Bswap or Mnemonic.Bsf or Mnemonic.Bsr
                or Mnemonic.Bt or Mnemonic.Btc or Mnemonic.Btr or Mnemonic.Bts
                or Mnemonic.Popcnt or Mnemonic.Lzcnt or Mnemonic.Tzcnt
                => LiftUnhandled(in instr, addr),

            
            Mnemonic.Xadd or Mnemonic.Cmpxchg or Mnemonic.Cmpxchg8b or Mnemonic.Cmpxchg16b
                => LiftUnhandled(in instr, addr),

            
            Mnemonic.Cpuid or Mnemonic.Rdtsc or Mnemonic.Rdtscp or Mnemonic.Syscall
                or Mnemonic.Ud2 or Mnemonic.Mfence or Mnemonic.Lfence or Mnemonic.Sfence
                => LiftUnhandled(in instr, addr),

            
            Mnemonic.Fld or Mnemonic.Fstp or Mnemonic.Fst or Mnemonic.Fild
                or Mnemonic.Fistp or Mnemonic.Fadd or Mnemonic.Faddp or Mnemonic.Fsub
                or Mnemonic.Fsubp or Mnemonic.Fmul or Mnemonic.Fmulp or Mnemonic.Fdiv
                or Mnemonic.Fdivp or Mnemonic.Fcom or Mnemonic.Fcomp or Mnemonic.Fcompp
                or Mnemonic.Fcomi or Mnemonic.Fcomip or Mnemonic.Fucomi or Mnemonic.Fucomip
                or Mnemonic.Fxch or Mnemonic.Finit or Mnemonic.Fninit
                or Mnemonic.Fldcw or Mnemonic.Fnstcw or Mnemonic.Fnstsw
                => LiftUnhandled(in instr, addr),

            
            Mnemonic.Movaps or Mnemonic.Movups or Mnemonic.Movapd or Mnemonic.Movupd
                or Mnemonic.Movdqa or Mnemonic.Movdqu or Mnemonic.Movss or Mnemonic.Movsd
                or Mnemonic.Movq or Mnemonic.Movd or Mnemonic.Movlps or Mnemonic.Movhps
                or Mnemonic.Addps or Mnemonic.Addpd or Mnemonic.Addss or Mnemonic.Addsd
                or Mnemonic.Subps or Mnemonic.Subpd or Mnemonic.Subss or Mnemonic.Subsd
                or Mnemonic.Mulps or Mnemonic.Mulpd or Mnemonic.Mulss or Mnemonic.Mulsd
                or Mnemonic.Divps or Mnemonic.Divpd or Mnemonic.Divss or Mnemonic.Divsd
                or Mnemonic.Xorps or Mnemonic.Xorpd or Mnemonic.Andps or Mnemonic.Andpd
                or Mnemonic.Orps or Mnemonic.Orpd
                or Mnemonic.Pxor or Mnemonic.Pcmpeqb or Mnemonic.Pcmpeqd or Mnemonic.Pcmpeqw
                or Mnemonic.Pmovmskb or Mnemonic.Pshufd or Mnemonic.Pshufb
                or Mnemonic.Punpcklbw or Mnemonic.Punpcklwd or Mnemonic.Punpckldq
                or Mnemonic.Comiss or Mnemonic.Comisd or Mnemonic.Ucomiss or Mnemonic.Ucomisd
                or Mnemonic.Cvtsi2ss or Mnemonic.Cvtsi2sd or Mnemonic.Cvtss2sd or Mnemonic.Cvtsd2ss
                or Mnemonic.Cvttss2si or Mnemonic.Cvttsd2si
                or Mnemonic.Sqrtss or Mnemonic.Sqrtsd or Mnemonic.Sqrtps or Mnemonic.Sqrtpd
                or Mnemonic.Maxss or Mnemonic.Maxsd or Mnemonic.Minss or Mnemonic.Minsd
                or Mnemonic.Shufps or Mnemonic.Shufpd or Mnemonic.Unpcklps or Mnemonic.Unpckhps
                => LiftUnhandled(in instr, addr),

            
            _ => LiftUnhandled(in instr, addr),
        };
    }

    private IrInstruction[] LiftMov(in Instruction instr, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        var src = LiftOperand(in instr, 1);

        
        if (dst.Kind == IrOperandKind.Memory || dst.Kind == IrOperandKind.StackSlot)
            return new[] { IrInstruction.MakeStore(dst, src, addr) };

        
        if (src.Kind == IrOperandKind.Memory || src.Kind == IrOperandKind.StackSlot)
            return new[] { IrInstruction.MakeLoad(dst, src, addr) };

        
        return new[] { IrInstruction.MakeAssign(dst, src, addr) };
    }

    private IrInstruction[] LiftMovExtend(in Instruction instr, ulong addr, IrOpcode extOp)
    {
        var dst = LiftOperand(in instr, 0);
        var src = LiftOperand(in instr, 1);

        
        if (src.Kind == IrOperandKind.Memory || src.Kind == IrOperandKind.StackSlot)
        {
            return new[] { new IrInstruction
            {
                Opcode = extOp,
                Destination = dst,
                Sources = new[] { src },
                BitSize = dst.BitSize,
                OriginalAddress = addr,
            }};
        }

        return new[] { new IrInstruction
        {
            Opcode = extOp,
            Destination = dst,
            Sources = new[] { src },
            BitSize = dst.BitSize,
            OriginalAddress = addr,
        }};
    }

    private IrInstruction[] LiftLea(in Instruction instr, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);

        
        
        var memBase = instr.MemoryBase;
        var memIndex = instr.MemoryIndex;
        var disp = (long)instr.MemoryDisplacement64;
        int scale = instr.MemoryIndexScale;

        if (memBase == Register.None && memIndex == Register.None)
        {
            
            return new[] { IrInstruction.MakeAssign(dst, IrOperand.Const(disp, dst.BitSize), addr) };
        }

        if (memBase == Register.RIP || memBase == Register.EIP)
        {
            
            long effectiveAddr = (long)instr.NextIP + disp;
            return new[] { IrInstruction.MakeAssign(dst, IrOperand.Const(effectiveAddr, dst.BitSize), addr) };
        }

        if (memIndex == Register.None && disp == 0)
        {
            
            return new[] { IrInstruction.MakeAssign(dst, IrOperand.Reg(memBase, dst.BitSize), addr) };
        }

        if (memIndex == Register.None)
        {
            
            return new[] { IrInstruction.MakeBinOp(IrOpcode.Add, dst,
                IrOperand.Reg(memBase, dst.BitSize),
                IrOperand.Const(disp, dst.BitSize), addr) };
        }

        var result = new List<IrInstruction>();
        var current = IrOperand.Reg(memIndex, dst.BitSize);

        if (scale > 1)
        {
            var tmp1 = MakeTemp(dst.BitSize);
            result.Add(IrInstruction.MakeBinOp(IrOpcode.Shl, tmp1,
                IrOperand.Reg(memIndex, dst.BitSize),
                IrOperand.Const(scale switch { 2 => 1, 4 => 2, 8 => 3, _ => 0 }, 8), addr));
            current = tmp1;
        }

        if (memBase != Register.None)
        {
            var tmp2 = MakeTemp(dst.BitSize);
            result.Add(IrInstruction.MakeBinOp(IrOpcode.Add, tmp2,
                IrOperand.Reg(memBase, dst.BitSize), current, addr));
            current = tmp2;
        }

        if (disp != 0)
        {
            result.Add(IrInstruction.MakeBinOp(IrOpcode.Add, dst, current,
                IrOperand.Const(disp, dst.BitSize), addr));
        }
        else
        {
            result.Add(IrInstruction.MakeAssign(dst, current, addr));
        }

        return result.ToArray();
    }

    private IrInstruction[] LiftBinOp(in Instruction instr, IrOpcode op, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        var src = LiftOperand(in instr, 1);

        if (dst.Kind == IrOperandKind.Memory || dst.Kind == IrOperandKind.StackSlot)
        {
            
            var tmp = MakeTemp(dst.BitSize);
            return new[]
            {
                IrInstruction.MakeLoad(tmp, dst, addr),
                IrInstruction.MakeBinOp(op, tmp, tmp, src, addr),
                IrInstruction.MakeStore(dst, tmp, addr),
            };
        }

        return new[] { IrInstruction.MakeBinOp(op, dst, dst, src, addr) };
    }

    private IrInstruction[] LiftXor(in Instruction instr, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        var src = LiftOperand(in instr, 1);

        
        if (dst.Kind == IrOperandKind.Register && src.Kind == IrOperandKind.Register
            && dst.CanonicalRegister == src.CanonicalRegister)
        {
            return new[] { IrInstruction.MakeAssign(dst, IrOperand.Const(0, dst.BitSize), addr) };
        }

        return LiftBinOp(in instr, IrOpcode.Xor, addr);
    }

    private IrInstruction[] LiftImul(in Instruction instr, ulong addr)
    {
        if (instr.OpCount == 3)
        {
            
            var dst = LiftOperand(in instr, 0);
            var src = LiftOperand(in instr, 1);
            var imm = LiftOperand(in instr, 2);
            return new[] { IrInstruction.MakeBinOp(IrOpcode.IMul, dst, src, imm, addr) };
        }
        if (instr.OpCount == 2)
        {
            
            var dst = LiftOperand(in instr, 0);
            var src = LiftOperand(in instr, 1);
            return new[] { IrInstruction.MakeBinOp(IrOpcode.IMul, dst, dst, src, addr) };
        }
        
        var op = LiftOperand(in instr, 0);
        byte bs = op.BitSize;
        return new[]
        {
            IrInstruction.MakeBinOp(IrOpcode.IMul,
                IrOperand.Reg(bs == 32 ? Register.EAX : Register.RAX, bs),
                IrOperand.Reg(bs == 32 ? Register.EAX : Register.RAX, bs), op, addr),
        };
    }

    private IrInstruction[] LiftMul(in Instruction instr, ulong addr)
    {
        var op = LiftOperand(in instr, 0);
        byte bs = op.BitSize;
        return new[]
        {
            IrInstruction.MakeBinOp(IrOpcode.Mul,
                IrOperand.Reg(bs == 32 ? Register.EAX : Register.RAX, bs),
                IrOperand.Reg(bs == 32 ? Register.EAX : Register.RAX, bs), op, addr),
        };
    }

    private IrInstruction[] LiftDiv(in Instruction instr, IrOpcode op, ulong addr)
    {
        var divisor = LiftOperand(in instr, 0);
        byte bs = divisor.BitSize;
        var ax = IrOperand.Reg(bs == 32 ? Register.EAX : Register.RAX, bs);
        var dx = IrOperand.Reg(bs == 32 ? Register.EDX : Register.RDX, bs);
        return new[]
        {
            IrInstruction.MakeBinOp(op, ax, ax, divisor, addr),
            IrInstruction.MakeBinOp(IrOpcode.Mod, dx, ax, divisor, addr),
        };
    }

    private IrInstruction[] LiftIncDec(in Instruction instr, IrOpcode op, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        if (dst.Kind == IrOperandKind.Memory || dst.Kind == IrOperandKind.StackSlot)
        {
            var tmp = MakeTemp(dst.BitSize);
            return new[]
            {
                IrInstruction.MakeLoad(tmp, dst, addr),
                IrInstruction.MakeBinOp(op, tmp, tmp, IrOperand.Const(1, dst.BitSize), addr),
                IrInstruction.MakeStore(dst, tmp, addr),
            };
        }
        return new[] { IrInstruction.MakeBinOp(op, dst, dst, IrOperand.Const(1, dst.BitSize), addr) };
    }

    private IrInstruction[] LiftUnaryOp(in Instruction instr, IrOpcode op, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        return new[] { IrInstruction.MakeUnaryOp(op, dst, dst, addr) };
    }

    private IrInstruction[] LiftCmp(in Instruction instr, ulong addr)
    {
        var left = LiftOperand(in instr, 0);
        var right = LiftOperand(in instr, 1);
        return new[] { IrInstruction.MakeCmp(left, right, addr) };
    }

    private IrInstruction[] LiftTest(in Instruction instr, ulong addr)
    {
        var left = LiftOperand(in instr, 0);
        var right = LiftOperand(in instr, 1);
        return new[] { IrInstruction.MakeTest(left, right, addr) };
    }

    private IrInstruction[] LiftPush(in Instruction instr, ulong addr)
    {
        var src = LiftOperand(in instr, 0);
        
        byte ptrSize = (byte)(_bitness == 64 ? 64 : 32);
        var rsp = IrOperand.Reg(_bitness == 64 ? Register.RSP : Register.ESP, ptrSize);
        return new[]
        {
            IrInstruction.MakeBinOp(IrOpcode.Sub, rsp, rsp,
                IrOperand.Const(ptrSize / 8, ptrSize), addr),
            IrInstruction.MakeStore(
                IrOperand.Mem(rsp.Register, Register.None, 1, 0, ptrSize),
                src, addr),
        };
    }

    private IrInstruction[] LiftPop(in Instruction instr, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        byte ptrSize = (byte)(_bitness == 64 ? 64 : 32);
        var rsp = IrOperand.Reg(_bitness == 64 ? Register.RSP : Register.ESP, ptrSize);
        return new[]
        {
            IrInstruction.MakeLoad(dst,
                IrOperand.Mem(rsp.Register, Register.None, 1, 0, ptrSize), addr),
            IrInstruction.MakeBinOp(IrOpcode.Add, rsp, rsp,
                IrOperand.Const(ptrSize / 8, ptrSize), addr),
        };
    }

    private IrInstruction[] LiftCall(in Instruction instr, ulong addr)
    {
        var retReg = IrOperand.Reg(_bitness == 64 ? Register.RAX : Register.EAX,
            (byte)(_bitness == 64 ? 64 : 32));

        IrOperand target;
        if (instr.Op0Kind == OpKind.NearBranch64 || instr.Op0Kind == OpKind.NearBranch32 || instr.Op0Kind == OpKind.NearBranch16)
        {
            ulong targetAddr = instr.NearBranchTarget;
            
            if (_imports.TryGetValue(targetAddr, out var importName))
                target = IrOperand.Const((long)targetAddr, 64);
            else
                target = IrOperand.Const((long)targetAddr, 64);
            target.Name = _imports.TryGetValue(targetAddr, out var name) ? name : $"sub_{targetAddr:X}";
        }
        else
        {
            target = LiftOperand(in instr, 0);
        }

        return new[] { IrInstruction.MakeCall(retReg, target, Array.Empty<IrOperand>(), addr) };
    }

    private IrInstruction[] LiftReturn(in Instruction instr, ulong addr)
    {
        var retReg = IrOperand.Reg(_bitness == 64 ? Register.RAX : Register.EAX,
            (byte)(_bitness == 64 ? 64 : 32));
        return new[] { IrInstruction.MakeReturn(retReg, addr) };
    }

    private IrInstruction[] LiftJmp(in Instruction instr, ulong addr)
    {
        
        
        if (instr.Op0Kind == OpKind.NearBranch64 || instr.Op0Kind == OpKind.NearBranch32 || instr.Op0Kind == OpKind.NearBranch16)
        {
            return new[] { new IrInstruction
            {
                Opcode = IrOpcode.Branch,
                Sources = new[] { IrOperand.Const((long)instr.NearBranchTarget, 64) },
                OriginalAddress = addr,
            }};
        }

        
        var target = LiftOperand(in instr, 0);
        return new[] { new IrInstruction
        {
            Opcode = IrOpcode.Branch,
            Sources = new[] { target },
            OriginalAddress = addr,
        }};
    }

    private IrInstruction[] LiftJcc(in Instruction instr, IrCondition cond, ulong addr)
    {
        
        long target = (long)instr.NearBranchTarget;
        long fallthrough = (long)instr.NextIP;
        return new[] { new IrInstruction
        {
            Opcode = IrOpcode.CondBranch,
            Condition = cond,
            Sources = new[]
            {
                IrOperand.Const(target, 64),      
                IrOperand.Const(fallthrough, 64),  
            },
            OriginalAddress = addr,
        }};
    }

    private IrInstruction[] LiftSetcc(in Instruction instr, IrCondition cond, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        return new[] { new IrInstruction
        {
            Opcode = IrOpcode.Assign,
            Destination = dst,
            Sources = new[] { IrOperand.FlagReg() },
            Condition = cond,
            BitSize = 8,
            OriginalAddress = addr,
        }};
    }

    private IrInstruction[] LiftXchg(in Instruction instr, ulong addr)
    {
        var op0 = LiftOperand(in instr, 0);
        var op1 = LiftOperand(in instr, 1);
        
        var tmp = MakeTemp(op0.BitSize);
        return new[]
        {
            IrInstruction.MakeAssign(tmp, op0, addr),
            IrInstruction.MakeAssign(op0, op1, addr),
            IrInstruction.MakeAssign(op1, tmp, addr),
        };
    }

    private IrInstruction[] LiftCmov(in Instruction instr, ulong addr)
    {
        var dst = LiftOperand(in instr, 0);
        var src = LiftOperand(in instr, 1);
        var cond = instr.Mnemonic switch
        {
            Mnemonic.Cmove => IrCondition.Equal,
            Mnemonic.Cmovne => IrCondition.NotEqual,
            Mnemonic.Cmovl => IrCondition.SignedLess,
            Mnemonic.Cmovle => IrCondition.SignedLessEq,
            Mnemonic.Cmovg => IrCondition.SignedGreater,
            Mnemonic.Cmovge => IrCondition.SignedGreaterEq,
            Mnemonic.Cmovb => IrCondition.UnsignedBelow,
            Mnemonic.Cmovbe => IrCondition.UnsignedBelowEq,
            Mnemonic.Cmova => IrCondition.UnsignedAbove,
            Mnemonic.Cmovae => IrCondition.UnsignedAboveEq,
            Mnemonic.Cmovs => IrCondition.Sign,
            Mnemonic.Cmovns => IrCondition.NotSign,
            _ => IrCondition.None,
        };
        
        return new[] { new IrInstruction
        {
            Opcode = IrOpcode.Assign,
            Destination = dst,
            Sources = new[] { src, dst },
            Condition = cond,
            BitSize = dst.BitSize,
            OriginalAddress = addr,
        }};
    }

    private IrInstruction[] LiftLeave(ulong addr)
    {
        byte ps = (byte)(_bitness == 64 ? 64 : 32);
        var sp = IrOperand.Reg(_bitness == 64 ? Register.RSP : Register.ESP, ps);
        var bp = IrOperand.Reg(_bitness == 64 ? Register.RBP : Register.EBP, ps);
        
        return new[]
        {
            IrInstruction.MakeAssign(sp, bp, addr),
            IrInstruction.MakeLoad(bp,
                IrOperand.Mem(sp.Register, Register.None, 1, 0, ps), addr),
            IrInstruction.MakeBinOp(IrOpcode.Add, sp, sp,
                IrOperand.Const(ps / 8, ps), addr),
        };
    }

    private IrInstruction[] LiftUnhandled(in Instruction instr, ulong addr)
    {
        
        var formatter = new NasmFormatter();
        var output = new StringOutput();
        formatter.Format(in instr, output);
        return new[] { new IrInstruction
        {
            Opcode = IrOpcode.Unknown,
            OriginalAddress = addr,
            Comment = output.ToStringAndReset(),
        }};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IrOperand LiftOperand(in Instruction instr, int opIndex)
    {
        var kind = instr.GetOpKind(opIndex);
        byte bitSize = GetOperandSize(in instr, opIndex);

        switch (kind)
        {
            case OpKind.Register:
                var reg = instr.GetOpRegister(opIndex);
                return IrOperand.Reg(reg, bitSize);

            case OpKind.Immediate8:
            case OpKind.Immediate8_2nd:
            case OpKind.Immediate16:
            case OpKind.Immediate32:
            case OpKind.Immediate64:
            case OpKind.Immediate8to16:
            case OpKind.Immediate8to32:
            case OpKind.Immediate8to64:
            case OpKind.Immediate32to64:
                long imm = (long)instr.GetImmediate(opIndex);
                return IrOperand.Const(imm, bitSize);

            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                return IrOperand.Const((long)instr.NearBranchTarget, 64);

            case OpKind.Memory:
                return LiftMemoryOperand(in instr, bitSize);

            default:
                return IrOperand.Const(0, bitSize);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IrOperand LiftMemoryOperand(in Instruction instr, byte bitSize)
    {
        var baseReg = instr.MemoryBase;
        var indexReg = instr.MemoryIndex;
        int scale = instr.MemoryIndexScale;
        long disp = (long)instr.MemoryDisplacement64;

        
        if (baseReg == Register.RIP || baseReg == Register.EIP)
        {
            long effectiveAddr = (long)instr.NextIP + disp;
            return IrOperand.Mem(Register.None, Register.None, 1, effectiveAddr, bitSize);
        }

        var canonical = IrOperand.GetCanonical(baseReg);
        if (indexReg == Register.None &&
            (canonical == Register.RBP || canonical == Register.RSP))
        {
            
            return IrOperand.Stack((int)disp, bitSize);
        }

        return IrOperand.Mem(baseReg, indexReg, scale, disp, bitSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetOperandSize(in Instruction instr, int opIndex)
    {
        var kind = instr.GetOpKind(opIndex);
        if (kind == OpKind.Register)
        {
            var reg = instr.GetOpRegister(opIndex);
            return GetRegisterSize(reg);
        }

        if (kind == OpKind.Memory)
        {
            return instr.MemorySize switch
            {
                MemorySize.UInt8 or MemorySize.Int8 => 8,
                MemorySize.UInt16 or MemorySize.Int16 => 16,
                MemorySize.UInt32 or MemorySize.Int32 or MemorySize.Float32 => 32,
                MemorySize.UInt64 or MemorySize.Int64 or MemorySize.Float64 => 64,
                MemorySize.UInt128 or MemorySize.Int128 or MemorySize.Float128 => 128,
                _ => (byte)(_bitness == 64 ? 64 : 32),
            };
        }

        
        return (byte)(_bitness == 64 ? 64 : 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetRegisterSize(Register reg)
    {
        
        if (reg is Register.AL or Register.AH or Register.BL or Register.BH
            or Register.CL or Register.CH or Register.DL or Register.DH
            or Register.SIL or Register.DIL or Register.SPL or Register.BPL
            or Register.R8L or Register.R9L or Register.R10L or Register.R11L
            or Register.R12L or Register.R13L or Register.R14L or Register.R15L)
            return 8;

        
        if (reg is Register.AX or Register.BX or Register.CX or Register.DX
            or Register.SI or Register.DI or Register.SP or Register.BP
            or Register.R8W or Register.R9W or Register.R10W or Register.R11W
            or Register.R12W or Register.R13W or Register.R14W or Register.R15W)
            return 16;

        
        if (reg is Register.EAX or Register.EBX or Register.ECX or Register.EDX
            or Register.ESI or Register.EDI or Register.ESP or Register.EBP
            or Register.R8D or Register.R9D or Register.R10D or Register.R11D
            or Register.R12D or Register.R13D or Register.R14D or Register.R15D)
            return 32;

        return 64;
    }

    private IrInstruction[] LiftStringInstruction(in Instruction instr, ulong addr)
    {
        bool isRep = instr.HasRepPrefix || instr.HasRepePrefix;
        bool isRepne = instr.HasRepnePrefix;

        if (!isRep && !isRepne)
            return LiftUnhandled(in instr, addr);

        byte ptrSize = (byte)(_bitness == 64 ? 64 : 32);
        var rdi = IrOperand.Reg(_bitness == 64 ? Register.RDI : Register.EDI, ptrSize);
        var rsi = IrOperand.Reg(_bitness == 64 ? Register.RSI : Register.ESI, ptrSize);
        var rcx = IrOperand.Reg(_bitness == 64 ? Register.RCX : Register.ECX, ptrSize);
        var rax = IrOperand.Reg(_bitness == 64 ? Register.RAX : Register.EAX, ptrSize);

        var retReg = IrOperand.Reg(_bitness == 64 ? Register.RAX : Register.EAX, ptrSize);

        int sizePerIter = instr.Mnemonic switch
        {
            Mnemonic.Movsb or Mnemonic.Stosb or Mnemonic.Scasb or Mnemonic.Lodsb => 1,
            Mnemonic.Movsw or Mnemonic.Stosw or Mnemonic.Scasw or Mnemonic.Lodsw => 2,
            Mnemonic.Movsd or Mnemonic.Stosd or Mnemonic.Scasd or Mnemonic.Lodsd => 4,
            Mnemonic.Movsq or Mnemonic.Stosq or Mnemonic.Scasq or Mnemonic.Lodsq => 8,
            _ => 1
        };

        var countArg = sizePerIter == 1 
            ? rcx 
            : IrOperand.Expr(IrInstruction.MakeBinOp(IrOpcode.Mul, MakeTemp(ptrSize), rcx, IrOperand.Const(sizePerIter, ptrSize), addr));

        if (instr.Mnemonic is Mnemonic.Movsb or Mnemonic.Movsw or Mnemonic.Movsd or Mnemonic.Movsq)
        {
            var target = IrOperand.Const(0, 64);
            target.Name = "memcpy";
            var call = IrInstruction.MakeCall(retReg, target, new[] { rdi, rsi, countArg }, addr);
            return new[] { call };
        }
        else if (instr.Mnemonic is Mnemonic.Stosb or Mnemonic.Stosw or Mnemonic.Stosd or Mnemonic.Stosq)
        {
            var target = IrOperand.Const(0, 64);
            target.Name = "memset";
            var alArg = IrOperand.Reg(Register.AL, 8); 
            var call = IrInstruction.MakeCall(retReg, target, new[] { rdi, alArg, countArg }, addr);
            return new[] { call };
        }
        else if (instr.Mnemonic is Mnemonic.Scasb or Mnemonic.Scasw or Mnemonic.Scasd or Mnemonic.Scasq)
        {
            var target = IrOperand.Const(0, 64);
            target.Name = isRepne ? "memchr" : "scas_idiom";
            var alArg = IrOperand.Reg(Register.AL, 8);
            var call = IrInstruction.MakeCall(retReg, target, new[] { rdi, alArg, countArg }, addr);
            return new[] { call };
        }
        
        return LiftUnhandled(in instr, addr);
    }
}
