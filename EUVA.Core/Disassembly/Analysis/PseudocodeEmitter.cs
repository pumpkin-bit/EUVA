// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public sealed class PseudocodeEmitter
{
    private readonly Dictionary<ulong, string> _imports;
    private readonly Dictionary<string, string> _userRenames;
    private CallingConventionAnalyzer.FunctionSignature? _signature;
    private List<StructReconstructor.RecoveredStruct> _structs = new();
    private List<VTableDetector.VTableCall> _vtables = new();
    private int _indentLevel;
    private IrBlock? _currentEmitBlock;
    private IrBlock[]? _blocks;

    public PseudocodeEmitter(
        Dictionary<ulong, string>? imports = null,
        Dictionary<string, string>? userRenames = null)
    {
        _imports = imports ?? new();
        _userRenames = userRenames ?? new();
    }

    public void SetSignature(CallingConventionAnalyzer.FunctionSignature sig) => _signature = sig;
    public void SetStructs(List<StructReconstructor.RecoveredStruct> structs) => _structs = structs;
    public void SetVTables(List<VTableDetector.VTableCall>? vtables) => _vtables = vtables ?? new();

    public PseudocodeLine[] Emit(StructuredNode root, IrBlock[] blocks)
    {
        _blocks = blocks;
        var lines = new List<PseudocodeLine>();
        _indentLevel = 0;

        EmitFunctionHeader(lines);

        lines.Add(MakeLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel = 1;


        EmitLocalDecls(lines, blocks);

        EmitNode(root, lines, blocks);

        _indentLevel = 0;
        lines.Add(MakeLine("}", PseudocodeSyntax.Punctuation));

        return lines.ToArray();
    }

    public PseudocodeLine[] EmitBlock(IrBlock block)
    {
        _blocks = new[] { block };
        var lines = new List<PseudocodeLine>();
        _indentLevel = 0;

        var instrs = block.Instructions;
        IrInstruction? lastCmp = null;

        for (int i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Nop) continue;
            if (instr.Opcode == IrOpcode.Phi) continue;

            if (instr.DefinesDest &&
                instr.Destination.Kind == IrOperandKind.Register &&
                instr.Destination.Register == Iced.Intel.Register.None &&
                instr.Opcode != IrOpcode.Call)
                continue;

          
            if ((instr.Opcode is IrOpcode.Add or IrOpcode.Sub) &&
                instr.Destination.Kind == IrOperandKind.Register &&
                IrOperand.GetCanonical(instr.Destination.Register) == Iced.Intel.Register.RSP)
                continue;

            if (instr.Opcode == IrOpcode.Assign &&
                instr.Destination.Kind == IrOperandKind.Register &&
                instr.Sources.Length == 1 &&
                instr.Sources[0].Kind == IrOperandKind.Register)
            {
                var dc = IrOperand.GetCanonical(instr.Destination.Register);
                var sc = IrOperand.GetCanonical(instr.Sources[0].Register);
                if ((dc == Iced.Intel.Register.RBP && sc == Iced.Intel.Register.RSP) ||
                    (dc == Iced.Intel.Register.RSP && sc == Iced.Intel.Register.RBP))
                    continue;
            }

            if (instr.Opcode == IrOpcode.Cmp || instr.Opcode == IrOpcode.Test)
            {
                lastCmp = instr;
                continue; 
            }

            if (instr.Opcode == IrOpcode.CondBranch)
            {
                string condText = FormatConditionForBranch(instr.Condition, lastCmp);
                string target = instr.Sources.Length > 0
                    ? FormatBranchTarget(instr.Sources[0]) : "???";
                string line = $"if ({condText}) goto {target};";
                lines.Add(new PseudocodeLine(line, new[]
                {
                    new PseudocodeSpan(0, 3, PseudocodeSyntax.Keyword),
                    new PseudocodeSpan(4, condText.Length + 2, PseudocodeSyntax.Text),
                    new PseudocodeSpan(line.IndexOf("goto"), 4, PseudocodeSyntax.Keyword),
                }));
                lastCmp = null;
                continue;
            }

            if (instr.Opcode == IrOpcode.Branch)
            {
                string target = instr.Sources.Length > 0
                    ? FormatBranchTarget(instr.Sources[0]) : "???";
                string line = $"goto {target};";
                lines.Add(new PseudocodeLine(line, new[]
                {
                    new PseudocodeSpan(0, 4, PseudocodeSyntax.Keyword),
                    new PseudocodeSpan(5, target.Length, PseudocodeSyntax.Text),
                }));
                continue;
            }

            if (lastCmp != null)
            {
                lastCmp = null;
            }

            EmitInstruction(instr, lines, block);
        }

        return lines.ToArray();
    }

    private string FormatBranchTarget(IrOperand op)
    {
        if (op.Kind == IrOperandKind.Label)
            return $"block_{op.BlockIndex}";
        if (op.Kind == IrOperandKind.Constant)
            return $"loc_{(ulong)op.ConstantValue:X}";
        return FormatOperand(op);
    }

    private string FormatConditionForBranch(IrCondition cond, IrInstruction? cmpInstr)
    {
        if (cmpInstr != null && cmpInstr.Sources.Length >= 2)
        {
            string left = FormatOperand(cmpInstr.Sources[0]);
            string right = FormatOperand(cmpInstr.Sources[1]);
            bool isTest = cmpInstr.Opcode == IrOpcode.Test;
            string op = FormatConditionOperator(cond, isTest);

          
            if (isTest && left == right)
                return $"{left} {op}";

            return $"{left} {op} {right}";
        }
        return FormatConditionCode(cond);
    }


    private void EmitNode(StructuredNode node, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        switch (node)
        {
            case SequenceNode seq:
                foreach (var child in seq.Children)
                    EmitNode(child, lines, blocks);
                break;

            case BlockNode bn:
                EmitBlockInstructions(bn.Block, lines);
                break;

            case IfNode ifn:
                EmitIf(ifn, lines, blocks);
                break;

            case WhileNode wn:
                EmitWhile(wn, lines, blocks);
                break;

            case ForNode fn:
                EmitFor(fn, lines, blocks);
                break;

            case DoWhileNode dwn:
                EmitDoWhile(dwn, lines, blocks);
                break;

            case SwitchNode sw:
                EmitSwitch(sw, lines, blocks);
                break;

            case ReturnNode ret:
                EmitReturn(ret, lines);
                break;

            case GotoNode gt:
                EmitGoto(gt, lines);
                break;
        }
    }

    private void EmitBlockInstructions(IrBlock block, List<PseudocodeLine> lines)
    {
        _currentEmitBlock = block;
        foreach (var instr in block.Instructions)
        {
            if (instr.IsDead) continue;
            if (instr.Opcode is IrOpcode.Branch or IrOpcode.CondBranch) continue;
            if (instr.Opcode is IrOpcode.Cmp or IrOpcode.Test) continue;
            if (ShouldSkipInstruction(instr)) continue;
            EmitInstruction(instr, lines, block);
        }
    }

    private static bool ShouldSkipInstruction(IrInstruction instr)
    {
     
        if (instr.Opcode is IrOpcode.Branch or IrOpcode.CondBranch
            or IrOpcode.Cmp or IrOpcode.Test or IrOpcode.Nop)
            return true;

        if (instr.DefinesDest &&
            instr.Destination.Kind == IrOperandKind.Register &&
            instr.Destination.Register == Iced.Intel.Register.None &&
            instr.Opcode != IrOpcode.Call)
            return true;

        if (instr.DefinesDest && instr.Destination.Kind == IrOperandKind.Register)
        {
            var dc = IrOperand.GetCanonical(instr.Destination.Register);
            if (dc == Iced.Intel.Register.RSP || dc == Iced.Intel.Register.RBP)
            {
                if (instr.Opcode == IrOpcode.Assign && instr.Sources.Length == 1 &&
                    instr.Sources[0].Kind == IrOperandKind.Register)
                {
                    var sc = IrOperand.GetCanonical(instr.Sources[0].Register);
                    if (sc == Iced.Intel.Register.RSP || sc == Iced.Intel.Register.RBP)
                        return true;
                }
                if (instr.Opcode is IrOpcode.Add or IrOpcode.Sub &&
                    dc == Iced.Intel.Register.RSP)
                    return true;
            }
        }

        if (instr.Opcode == IrOpcode.Phi) return true;

        return false;
    }

    private void EmitIf(IfNode ifn, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        string condText = FormatCondition(ifn.Condition, ifn.ConditionInstr);
        lines.Add(MakeKeywordLine($"if ({condText})"));
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(ifn.ThenBody, lines, blocks);
        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));

        if (ifn.ElseBody != null)
        {
            lines.Add(MakeKeywordLine("else"));
            lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
            _indentLevel++;
            EmitNode(ifn.ElseBody, lines, blocks);
            _indentLevel--;
            lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));
        }
    }

    private void EmitWhile(WhileNode wn, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        string condText = FormatCondition(wn.Condition, wn.ConditionInstr);
        lines.Add(MakeKeywordLine($"while ({condText})"));
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(wn.Body, lines, blocks);
        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));
    }

    private void EmitFor(ForNode fn, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        string initText = fn.InitInstr != null ? FormatExpression(fn.InitInstr, forceExpression: false) : "";
        string condText = FormatCondition(fn.Condition, fn.ConditionInstr);
        string stepText = fn.StepInstr != null ? FormatExpression(fn.StepInstr, forceExpression: true) : "";
        
        lines.Add(MakeKeywordLine($"for ({initText}; {condText}; {stepText})"));
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(fn.Body, lines, blocks);
        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));
    }

    private void EmitDoWhile(DoWhileNode dwn, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        lines.Add(MakeKeywordLine("do"));
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(dwn.Body, lines, blocks);
        _indentLevel--;
        string condText = FormatCondition(dwn.Condition, dwn.ConditionInstr);
        lines.Add(MakeKeywordLine($"}} while ({condText});"));
    }

    private void EmitSwitch(SwitchNode sw, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        string val = FormatOperand(sw.SwitchValue);
        lines.Add(MakeKeywordLine($"switch ({val})"));
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;

        foreach (var (caseVal, body) in sw.Cases)
        {
            lines.Add(MakeKeywordLine($"case {caseVal}:"));
            _indentLevel++;
            EmitNode(body, lines, blocks);
            lines.Add(MakeKeywordLine("break;"));
            _indentLevel--;
        }

        if (sw.DefaultBody != null)
        {
            lines.Add(MakeKeywordLine("default:"));
            _indentLevel++;
            EmitNode(sw.DefaultBody, lines, blocks);
            lines.Add(MakeKeywordLine("break;"));
            _indentLevel--;
        }

        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));
    }

    private void EmitReturn(ReturnNode ret, List<PseudocodeLine> lines)
    {
        if (ret.ReturnValue.HasValue)
        {
            string val = FormatOperand(ret.ReturnValue.Value);
            lines.Add(MakeKeywordLine($"return {val};"));
        }
        else
        {
            lines.Add(MakeKeywordLine("return;"));
        }
    }

    private void EmitGoto(GotoNode gt, List<PseudocodeLine> lines)
    {
        lines.Add(MakeKeywordLine($"goto block_{gt.TargetBlockIndex};"));
    }

    private void EmitInstruction(IrInstruction instr, List<PseudocodeLine> lines, IrBlock currentBlock)
    {
        string text = FormatInstructionStatement(instr, currentBlock);
        if (string.IsNullOrEmpty(text)) return;

        var spans = BuildSpans(text, instr);
        string indented = GetIndent() + text;
        lines.Add(new PseudocodeLine(indented, spans));
    }

    private string FormatInstructionStatement(IrInstruction instr, IrBlock currentBlock)
    {
        string expr = FormatExpression(instr, currentBlock, forceExpression: false);
        if (string.IsNullOrEmpty(expr)) return "";

        if (!expr.StartsWith("//") && !expr.EndsWith(";"))
            return expr + ";";
        
        return expr;
    }

    private string FormatExpression(IrInstruction instr, IrBlock? currentBlock = null, bool forceExpression = false)
    {
        var block = currentBlock ?? _currentEmitBlock;
        bool definesDest = instr.DefinesDest && !forceExpression;
        switch (instr.Opcode)
        {
            case IrOpcode.Nop:
                return "";

            case IrOpcode.Assign:
                {
                    string dst = FormatOperand(instr.Destination);
                    string src = FormatOperand(instr.Sources[0]);
                    if (instr.Condition != IrCondition.None)
                    {
                        IrInstruction? condInstr = instr.ConditionInstr;

                        string condStr = FormatCondition(instr.Condition, condInstr);
                        
                        if (instr.Sources.Length >= 2)
                        {
                            string val = $"{condStr} ? {src} : {FormatOperand(instr.Sources[1])}";
                            if (definesDest) return $"{dst} = {val}";
                            return val;
                        }
                        else
                        {
                           
                            if (definesDest) return $"{dst} = {condStr}";
                            return condStr;
                        }
                    }
                    if (definesDest) return $"{dst} = {src}";
                    return src;
                }

            case IrOpcode.Add: return FormatBinOp(instr, "+", definesDest);
            case IrOpcode.Sub: return FormatBinOp(instr, "-", definesDest);
            case IrOpcode.Mul or IrOpcode.IMul: return FormatBinOp(instr, "*", definesDest);
            case IrOpcode.Div or IrOpcode.IDiv: return FormatBinOp(instr, "/", definesDest);
            case IrOpcode.Mod: return FormatBinOp(instr, "%", definesDest);
            case IrOpcode.And: return FormatBinOp(instr, "&", definesDest);
            case IrOpcode.Or: return FormatBinOp(instr, "|", definesDest);
            case IrOpcode.Xor: return FormatBinOp(instr, "^", definesDest);
            case IrOpcode.Shl: return FormatBinOp(instr, "<<", definesDest);
            case IrOpcode.Shr or IrOpcode.Sar: return FormatBinOp(instr, ">>", definesDest);

            case IrOpcode.Neg:
                return FormatUnaryOp(instr, "-", instr.Sources[0], definesDest);

            case IrOpcode.Not:
                return FormatUnaryOp(instr, "~", instr.Sources[0], definesDest);

            case IrOpcode.Load:
                {
                    string dst = FormatOperand(instr.Destination);
                    if (instr.Destination.Kind == IrOperandKind.Register &&
                        instr.Destination.Register == Register.None)
                        return "";
                    string src = FormatMemAccess(instr.Sources[0]);
                    return definesDest ? $"{dst} = {src}" : src;
                }

            case IrOpcode.Store:
                {
                    string dst = FormatMemAccess(instr.Destination);
                    if (instr.Sources.Length >= 2)
                    {
                        string val = FormatOperand(instr.Sources[1]);
                        return $"{dst} = {val}";
                    }
                    string val1 = FormatOperand(instr.Sources[0]);
                    return $"{dst} = {val1}";
                }

            case IrOpcode.Call:
                return FormatCall(instr, definesDest);

            case IrOpcode.Return:
                if (instr.Sources.Length > 0)
                    return $"return {FormatOperand(instr.Sources[0])}";
                return "return";

            case IrOpcode.Phi:
                {
                    string dst = FormatOperand(instr.Destination);
                    var srcs = instr.Sources.Select((s, i) =>
                    {
                        int blk = instr.PhiSourceBlocks != null && i < instr.PhiSourceBlocks.Length
                            ? instr.PhiSourceBlocks[i] : -1;
                        return $"{FormatOperand(s)}/*b{blk}*/";
                    });
                    return $"// {dst} = φ({string.Join(", ", srcs)})";
                }

            case IrOpcode.ZeroExtend:
            case IrOpcode.SignExtend:
            case IrOpcode.Truncate:
                {
                    string dst = FormatOperand(instr.Destination);
                    string src = FormatOperand(instr.Sources[0]);
                    bool sameType = instr.Destination.Type != TypeInfo.Unknown && instr.Destination.Type == instr.Sources[0].Type;
                    string castStr = sameType ? "" : $"({TypeName(instr.Destination.BitSize, instr.Destination.Type)})";
                    return definesDest ? $"{dst} = {castStr}{src}" : $"{castStr}{src}";
                }

            case IrOpcode.Cmp:
                return $"// cmp {FormatOperand(instr.Sources[0])}, {FormatOperand(instr.Sources[1])}";

            case IrOpcode.Test:
                return $"// test {FormatOperand(instr.Sources[0])}, {FormatOperand(instr.Sources[1])}";
                
            case IrOpcode.StackAlloc:
                return $"{FormatOperand(instr.Destination)} = alloca({FormatOperand(instr.Sources[0])})";

            case IrOpcode.Rol:
                return definesDest ? $"{FormatOperand(instr.Destination)} = _rotl({FormatOperand(instr.Sources[0])}, {FormatOperand(instr.Sources[1])})" : $"_rotl({FormatOperand(instr.Sources[0])}, {FormatOperand(instr.Sources[1])})";
            case IrOpcode.Ror:
                return definesDest ? $"{FormatOperand(instr.Destination)} = _rotr({FormatOperand(instr.Sources[0])}, {FormatOperand(instr.Sources[1])})" : $"_rotr({FormatOperand(instr.Sources[0])}, {FormatOperand(instr.Sources[1])})";

            default:
                if (instr.Comment != null)
                    return $"// {instr.Comment} @ 0x{instr.OriginalAddress:X}";
                return $"// unhandled instruction {instr.Opcode} @ 0x{instr.OriginalAddress:X}";
        }
    }

    private string FormatUnaryOp(IrInstruction instr, string op, IrOperand src, bool definesDest)
    {
        string expr = $"{op}{FormatOperand(src)}";
        if (definesDest)
            return $"{FormatOperand(instr.Destination)} = {expr}";
        return expr;
    }

    private string FormatBinOp(IrInstruction instr, string op, bool definesDest)
    {
        if (instr.Sources.Length < 2) return $"// incomplete {instr.Opcode}";

        if (instr.Destination.Kind is IrOperandKind.Memory or IrOperandKind.StackSlot)
        {
            string memDst = FormatMemAccess(instr.Destination);
            string rightOp = FormatOperand(instr.Sources[1]);
            return $"{memDst} {op}= {rightOp}";
        }

        string dst = FormatOperand(instr.Destination);
        string left = FormatOperand(instr.Sources[0]);
        string right = FormatOperand(instr.Sources[1]);
        
        bool destTypeDiffers = instr.Destination.Type != TypeInfo.Unknown && 
                               instr.Destination.Type != instr.Sources[0].Type &&
                               instr.Destination.Type != instr.Sources[1].Type;

        string castPrefix = "";
        if (destTypeDiffers && definesDest)
        {
            castPrefix = $"({TypeName(instr.Destination.BitSize, instr.Destination.Type)})";
        }

        string expr = $"{castPrefix}({left} {op} {right})";
        
      
        if (string.IsNullOrEmpty(castPrefix))
            expr = $"{left} {op} {right}";

        if (!definesDest)
            return $"({expr})";

        if (dst == left && string.IsNullOrEmpty(castPrefix))
        {
            if (op == "+" && right == "1") return $"{dst}++";
            if (op == "-" && right == "1") return $"{dst}--";
            if (op == "+" && right == left) return $"{dst} *= 2";
            return $"{dst} {op}= {right}";
        }

        return $"{dst} = {expr}";
    }

    private string FormatCall(IrInstruction instr, bool definesDest)
    {
        if (instr.Sources.Length == 0) return "// call ???";

        string target = FormatCallTarget(instr.Sources[0]);
        var args = new List<string>();

       
        for (int i = 1; i < instr.Sources.Length; i++)
        {
            try
            {
                args.Add(FormatOperand(instr.Sources[i]));
            }
            catch
            {
                args.Add("unknown_arg");
            }
        }

        string methodCall;

       
        VTableDetector.VTableCall? vtMatch = null;
        foreach (var v in _vtables)
        {
            if (v.InstructionIndex >= 0 && instr.OriginalAddress > 0)
            {
                vtMatch = v;
                break;
            }
        }

        if (vtMatch != null && args.Count > 0)
        {
          
            string thisPtr = args[0];
            args.RemoveAt(0);
            string argStr = args.Count > 0 ? string.Join(", ", args) : "";
            methodCall = $"{thisPtr}->{vtMatch.MethodName}({argStr})";
        }
        else
        {
            string argStr = args.Count > 0 ? string.Join(", ", args) : "";
            methodCall = $"{target}({argStr})";
        }

        string dst = FormatOperand(instr.Destination);

     
        if (definesDest && instr.Destination.Kind == IrOperandKind.Register && instr.Destination.Register != Register.None)
            return $"{dst} = {methodCall}";

        return methodCall;
    }

    private string FormatOperand(IrOperand op)
    {
        switch (op.Kind)
        {
            case IrOperandKind.Register:
                string regName = FormatRegisterName(op);
                return ApplyRename(regName);

            case IrOperandKind.Constant:
                return FormatConstant(op.ConstantValue, op.BitSize);

            case IrOperandKind.StackSlot:
                string varName = FormatStackVar(op.StackOffset, op.SsaVersion);
                return ApplyRename(varName);

            case IrOperandKind.Memory:
                return FormatMemAccess(op);

            case IrOperandKind.Flag:
                return "flags";

            case IrOperandKind.Label:
                return $"block_{op.BlockIndex}";

            case IrOperandKind.Expression:
                if (op.Expression == null) return "?";
                return FormatExpression(op.Expression, null, forceExpression: true);

            default:
                return "?";
        }
    }

    private string FormatRegisterName(IrOperand op)
    {
        if (op.Name != null) return op.Name;


        if (op.Register == Register.None)
            return op.SsaVersion != 0 ? $"tmp_{Math.Abs(op.SsaVersion)}" : "tmp";

        var canonical = op.CanonicalRegister;
        string baseName = canonical switch
        {
            Register.RAX => "rax",
            Register.RCX => "a1",
            Register.RDX => "a2",
            Register.R8 => "a3",
            Register.R9 => "a4",
            Register.RBX => "v1",
            Register.RSI => "v2",
            Register.RDI => "v3",
            Register.RBP => "rbp",
            Register.RSP => "rsp",
            Register.R10 => "t1",
            Register.R11 => "t2",
            Register.R12 => "v4",
            Register.R13 => "v5",
            Register.R14 => "v6",
            Register.R15 => "v7",
            _ => op.Register.ToString().ToLowerInvariant(),
        };

        return baseName;
    }

    private string FormatStackVar(int offset, int ssaVersion)
    {
        string name;
        if (offset < 0)
            name = $"var_{-offset:X}";
        else if (offset >= 0 && offset < 0x28)
            name = $"spill_{offset:X}";
        else
            name = $"arg_{offset:X}";

        return name;
    }

    private string FormatConstant(long value, byte bitSize)
    {
        if (value == 0) return "0";
        if (value == 1) return "1";
        if (value == -1) return "-1";

        if (value >= -999 && value <= 999)
            return value.ToString();

        string? known = value switch
        {
            0x80000000 => "GENERIC_READ",
            0x40000000 => "GENERIC_WRITE",
            0xC0000000u => "GENERIC_READ | GENERIC_WRITE",
            0x1000 => "MEM_COMMIT",
            0x2000 => "MEM_RESERVE",
            0x3000 => "MEM_COMMIT | MEM_RESERVE",
            _ => null,
        };
        if (known != null) return known;

        if (bitSize >= 32 && (value > 0x10000 || value < -0x10000))
        {
            if (value < 0)
                return $"(void*)-0x{-value:X}";

            if (_imports != null && _imports.TryGetValue((ulong)value, out var name))
            {
                int bang = name.IndexOf('!');
                return "&" + (bang >= 0 ? name.Substring(bang + 1) : name);
            }
            return $"&g_Data_{value:X}";
        }

      
        if (value < 0)
            return $"-0x{-value:X}";
        return $"0x{value:X}";
    }

    private string FormatMemAccess(IrOperand op)
    {
        if (op.Kind == IrOperandKind.StackSlot)
            return FormatStackVar(op.StackOffset, op.SsaVersion);

        if (op.Kind != IrOperandKind.Memory)
            return FormatOperand(op);

        if (op.MemBase == Register.None && op.MemIndex == Register.None && op.MemDisplacement != 0)
        {
           
            ulong addr = (ulong)op.MemDisplacement;
            if (_imports.TryGetValue(addr, out var name))
            {
                int bang = name.IndexOf('!');
                return bang >= 0 ? name.Substring(bang + 1) : name;
            }
            return $"g_0x{op.MemDisplacement:X}";
        }

        if (op.MemBase != Register.None && op.MemIndex == Register.None && op.MemDisplacement != 0)
        {
            var canonical = IrOperand.GetCanonical(op.MemBase);
            if (canonical != Register.RSP && canonical != Register.RBP)
            {
                string baseName = FormatRegisterName(new IrOperand
                {
                    Kind = IrOperandKind.Register,
                    Register = op.MemBase,
                    BitSize = 64,
                    SsaVersion = op.SsaVersion,
                });

             
                foreach (var st in _structs)
                {
                    if (st.Fields.TryGetValue(op.MemDisplacement, out var field))
                        return $"{ApplyRename(baseName)}->{field.Name}";
                }
            }
        }

        var parts = new List<string>();
        string? baseStr = null;
        string? idxStr = null;

        if (op.MemBase != Register.None)
        {
            baseStr = FormatRegisterName(new IrOperand
            {
                Kind = IrOperandKind.Register,
                Register = op.MemBase,
                BitSize = 64,
                SsaVersion = op.SsaVersion,
            });
            
            if (op.MemIndex == Register.None && op.MemDisplacement != 0)
            {
                foreach (var st in _structs)
                {
                    if (st.Fields.TryGetValue(op.MemDisplacement, out var field))
                        return $"{ApplyRename(baseStr)}->{field.Name}";
                }
            }

            baseStr = ApplyRename(baseStr);
            parts.Add(baseStr);
        }
        if (op.MemIndex != Register.None)
        {
            idxStr = FormatRegisterName(new IrOperand
            {
                Kind = IrOperandKind.Register,
                Register = op.MemIndex,
                BitSize = 64,
                SsaVersion = -1,
            });
            idxStr = ApplyRename(idxStr);
            parts.Add(op.MemScale > 1 ? $"{idxStr} * {op.MemScale}" : idxStr);
        }

       
        if (baseStr != null && idxStr != null && op.MemDisplacement == 0)
        {
            string scaleStr = op.MemScale > 1 ? $" * {op.MemScale}" : "";
            return $"{baseStr}[{idxStr}{scaleStr}]";
        }


        if (baseStr != null && idxStr == null && op.MemDisplacement == 0)
            return $"*{baseStr}";

        if (op.MemDisplacement != 0)
        {
            if (op.MemDisplacement < 0)
                parts.Add($"- 0x{-op.MemDisplacement:X}");
            else
                parts.Add($"+ 0x{op.MemDisplacement:X}");
        }

        if (parts.Count == 0)
            return $"*({TypeName(op.BitSize, op.Type)}*)nullptr";

        string addrExpr = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            if (parts[i].StartsWith("+ ") || parts[i].StartsWith("- "))
                addrExpr += $" {parts[i]}";
            else
                addrExpr += $" + {parts[i]}";
        }

        if (addrExpr.StartsWith("+ "))
            addrExpr = addrExpr.Substring(2);

        return $"*({TypeName(op.BitSize, op.Type)}*)({addrExpr})";
    }

    private string FormatCallTarget(IrOperand op)
    {
        if (op.Name != null)
        {
            int bangIdx = op.Name.IndexOf('!');
            return bangIdx >= 0 ? op.Name.Substring(bangIdx + 1) : op.Name;
        }

        if (op.Kind == IrOperandKind.Constant)
        {
            ulong addr = (ulong)op.ConstantValue;
            if (_imports.TryGetValue(addr, out var name))
            {
                int bangIdx = name.IndexOf('!');
                return bangIdx >= 0 ? name.Substring(bangIdx + 1) : name;
            }
            return $"sub_{addr:X}";
        }

        return FormatOperand(op);
    }

    private string FormatCondition(IrCondition cond, IrInstruction? condInstr)
    {
        if (condInstr != null)
        {
            if (condInstr.Opcode == IrOpcode.Cmp && condInstr.Sources.Length >= 2)
            {
                string left = FormatOperand(condInstr.Sources[0]);
                string right = FormatOperand(condInstr.Sources[1]);
                string normalOp = FormatConditionOperator(cond, false);
                return $"{left} {normalOp} {right}";
            }
            else if (condInstr.Opcode == IrOpcode.Test && condInstr.Sources.Length >= 2)
            {
                string left = FormatOperand(condInstr.Sources[0]);
                string right = FormatOperand(condInstr.Sources[1]);
                string op = cond switch { IrCondition.Equal => "== 0", IrCondition.NotEqual => "!= 0", IrCondition.Sign => "< 0", IrCondition.NotSign => ">= 0", _ => FormatConditionCode(cond) + " 0" };
                if (left == right)
                    return $"{left} {op}";
                return $"({left} & {right}) {op}";
            }
            else if (condInstr.DefinesDest)
            {
                string dst = FormatOperand(condInstr.Destination);
                string op = cond switch { 
                    IrCondition.Equal => "== 0", IrCondition.NotEqual => "!= 0", 
                    IrCondition.SignedLess => "< 0", IrCondition.SignedLessEq => "<= 0", 
                    IrCondition.SignedGreater => "> 0", IrCondition.SignedGreaterEq => ">= 0", 
                    IrCondition.Sign => "< 0", IrCondition.NotSign => ">= 0", 
                    IrCondition.UnsignedAbove => "!= 0", IrCondition.UnsignedBelowEq => "== 0",
                    _ => FormatConditionCode(cond) + " 0" 
                };
                return $"{dst} {op}";
            }
        }

        return FormatConditionCode(cond);
    }

    private static string FormatConditionOperator(IrCondition cond, bool isTest) => isTest switch
    {
        true => cond switch
        {
            IrCondition.Equal => "== 0",     
            IrCondition.NotEqual => "!= 0", 
            _ => FormatConditionCode(cond),
        },
        false => cond switch
        {
            IrCondition.Equal => "==",
            IrCondition.NotEqual => "!=",
            IrCondition.SignedLess => "<",
            IrCondition.SignedLessEq => "<=",
            IrCondition.SignedGreater => ">",
            IrCondition.SignedGreaterEq => ">=",
            IrCondition.UnsignedBelow => "<u",
            IrCondition.UnsignedBelowEq => "<=u",
            IrCondition.UnsignedAbove => ">u",
            IrCondition.UnsignedAboveEq => ">=u",
            _ => FormatConditionCode(cond),
        },
    };

    private static string FormatConditionCode(IrCondition cond) => cond switch
    {
        IrCondition.Equal => "equal",
        IrCondition.NotEqual => "not_equal",
        IrCondition.SignedLess => "signed_less",
        IrCondition.SignedLessEq => "signed_less_eq",
        IrCondition.SignedGreater => "signed_greater",
        IrCondition.SignedGreaterEq => "signed_greater_eq",
        IrCondition.UnsignedBelow => "unsigned_below",
        IrCondition.UnsignedBelowEq => "unsigned_below_eq",
        IrCondition.UnsignedAbove => "unsigned_above",
        IrCondition.UnsignedAboveEq => "unsigned_above_eq",
        IrCondition.Sign => "sign",
        IrCondition.NotSign => "not_sign",
        IrCondition.Overflow => "overflow",
        IrCondition.NotOverflow => "not_overflow",
        _ => "???",
    };

    private static string TypeName(byte bitSize, TypeInfo type) 
    {
        if (type != TypeInfo.Unknown) return FormatInferredType(type);
        return bitSize switch
        {
            8 => "uint8_t",
            16 => "uint16_t",
            32 => "uint32_t",
            64 => "uint64_t",
            _ => "void",
        };
    }


    private void EmitFunctionHeader(List<PseudocodeLine> lines)
    {
        if (_signature != null)
        {
            var sb = new StringBuilder();
            sb.Append(FormatInferredType(_signature.ReturnType));
            sb.Append($" {ApplyRename(_signature.Name)}(");

            for (int i = 0; i < _signature.Arguments.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var arg = _signature.Arguments[i];
                sb.Append(FormatInferredType(arg.Type));
                sb.Append($" {ApplyRename(arg.Name)}");
            }

            sb.Append(')');

            string text = sb.ToString();
            var spans = new[]
            {
                new PseudocodeSpan(0, text.Length, PseudocodeSyntax.Function),
            };
            lines.Add(new PseudocodeLine(text, spans));
        }
        else
        {
            lines.Add(new PseudocodeLine("void sub_unknown()", new[]
            {
                new PseudocodeSpan(0, 5, PseudocodeSyntax.Type),
                new PseudocodeSpan(5, 15, PseudocodeSyntax.Function),
            }));
        }
    }

    private void EmitLocalDecls(List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        var declaredVars = new HashSet<string>();
        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead || !instr.DefinesDest) continue;
                if (instr.Opcode == IrOpcode.Phi) continue;

                var dst = instr.Destination;
                if (dst.Kind == IrOperandKind.StackSlot)
                {
                    string name = FormatStackVar(dst.StackOffset, 0);
                    name = ApplyRename(name);
                    if (declaredVars.Add(name))
                    {
                        string type = FormatInferredType(dst.Type != TypeInfo.Unknown ? dst.Type : new TypeInfo { BaseType = PrimitiveType.UInt64 });
                        string decl = $"    {type} {name};";
                        lines.Add(new PseudocodeLine(decl, new[]
                        {
                            new PseudocodeSpan(4, type.Length, PseudocodeSyntax.Type),
                            new PseudocodeSpan(5 + type.Length, name.Length, PseudocodeSyntax.Variable),
                        }));
                    }
                }
            }
        }

        if (declaredVars.Count > 0)
            lines.Add(PseudocodeLine.Empty);
    }

    private static string FormatInferredType(TypeInfo type) 
    {
        if (type.PointerLevel > 0)
        {
            var baseT = new TypeInfo { BaseType = type.BaseType, PointerLevel = 0 };
            return FormatInferredType(baseT) + new string('*', type.PointerLevel);
        }

        return type.BaseType switch
        {
            PrimitiveType.Int8 => "int8_t",
            PrimitiveType.UInt8 => "uint8_t",
            PrimitiveType.Int16 => "int16_t",
            PrimitiveType.UInt16 => "uint16_t",
            PrimitiveType.Int32 => "int",
            PrimitiveType.UInt32 => "unsigned int",
            PrimitiveType.Int64 => "int64_t",
            PrimitiveType.UInt64 => "uint64_t",
            PrimitiveType.Float32 => "float",
            PrimitiveType.Float64 => "double",
            PrimitiveType.Bool => "bool",
            PrimitiveType.Void => "void",
            PrimitiveType.Struct => "struct",
            _ => "uint64_t",
        };
    }

    private string ApplyRename(string name) =>
        _userRenames.TryGetValue(name, out var renamed) ? renamed : name;

    private string GetIndent() => new string(' ', _indentLevel * 4);

    private PseudocodeLine MakeLine(string text, PseudocodeSyntax kind) =>
        new(GetIndent() + text, new[] { new PseudocodeSpan(0, text.Length + _indentLevel * 4, kind) });

    private PseudocodeLine MakeIndentedLine(string text, PseudocodeSyntax kind) =>
        MakeLine(text, kind);

    private PseudocodeLine MakeKeywordLine(string text)
    {
        string indented = GetIndent() + text;
        return new PseudocodeLine(indented, new[]
        {
            new PseudocodeSpan(0, indented.Length, PseudocodeSyntax.Keyword),
        });
    }

    private PseudocodeSpan[] BuildSpans(string text, IrInstruction instr)
    {
        if (instr.Opcode is IrOpcode.Phi or IrOpcode.Cmp or IrOpcode.Test or IrOpcode.Unknown || text.StartsWith("//"))
        {
            return new[] { new PseudocodeSpan(0, _indentLevel * 4 + text.Length, PseudocodeSyntax.Comment) };
        }

        int offset = _indentLevel * 4;
        var spans = new List<PseudocodeSpan>();
        
        var regex = new System.Text.RegularExpressions.Regex(
            @"(?<String>""[^""]*"")|(?<Comment>//.*)|(?<Number>\b0x[0-9a-fA-F]+\b|\b\d+\b)|(?<Keyword>\b(if|else|while|do|for|switch|case|default|break|continue|return|goto|alloca|sizeof)\b)|(?<Type>\b(int8_t|uint8_t|int16_t|uint16_t|int32_t|uint32_t|int64_t|uint64_t|float|double|bool|void|int|unsigned|struct)\b)|(?<Method>\b[a-zA-Z_]\w*(?=\s*\())|(?<Var>\b[a-zA-Z_]\w*\b)|(?<Punct>[{}()\[\].,;])|(?<Op>[+\-*/%&|^~<>=!?:]+)"
        );

        foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
        {
            if (m.Groups["Comment"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Comment));
            }
            else if (m.Groups["String"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.String));
            }
            else if (m.Groups["Number"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Number));
            }
            else if (m.Groups["Keyword"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Keyword));
            }
            else if (m.Groups["Type"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Type));
            }
            else if (m.Groups["Method"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Function));
            }
            else if (m.Groups["Var"].Success)
            {
                string val = m.Groups["Var"].Value;
                if (val.StartsWith("loc_") || val.StartsWith("block_") || val.StartsWith("g_0x"))
                    spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Address));
                else
                    spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Variable));
            }
            else if (m.Groups["Op"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Operator));
            }
            else if (m.Groups["Punct"].Success)
            {
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Punctuation));
            }
        }

        if (spans.Count == 0)
            spans.Add(new PseudocodeSpan(0, _indentLevel * 4 + text.Length, PseudocodeSyntax.Text));

        return spans.ToArray();
    }
}
