// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public sealed class PseudocodeEmitter
{
    private readonly Dictionary<ulong, string> _imports;
    private readonly Dictionary<long, string> _strings;
    private readonly Dictionary<string, VariableSymbol> _userRenames;
    private readonly Func<ulong, string>? _stringExtractor;
    private CallingConventionAnalyzer.FunctionSignature? _signature;
    private List<StructReconstructor.RecoveredStruct> _structs = new();
    private List<VTableDetector.VTableCall> _vtables = new();
    private Dictionary<(Register, int), TypeInfo> _typeMap = new();
    private int _indentLevel;
    private IrBlock? _currentEmitBlock;
    private IrBlock[]? _blocks;

    private readonly HashSet<string> _aiNames = new();
    private string? _summary;

    public PseudocodeEmitter(
        Dictionary<ulong, string>? imports = null,
        Dictionary<long, string>? strings = null,
        Dictionary<string, VariableSymbol>? userRenames = null,
        Func<ulong, string>? stringExtractor = null)
    {
        _imports         = imports       ?? new();
        _strings         = strings       ?? new();
        _userRenames     = userRenames   ?? new();
        _stringExtractor = stringExtractor;

        foreach (var sym in _userRenames.Values)
            if (sym.IsAiGenerated) _aiNames.Add(sym.Name);
    }

    public void SetSignature(CallingConventionAnalyzer.FunctionSignature sig) => _signature = sig;
    public void SetStructs(List<StructReconstructor.RecoveredStruct> structs) => _structs = structs;
    public void SetVTables(List<VTableDetector.VTableCall>? vtables) => _vtables = vtables ?? new();
    public void SetTypeMap(Dictionary<(Register, int), TypeInfo> types) => _typeMap = types;
    public void SetSummary(string? summary) => _summary = summary;

    private Dictionary<long, string> _userComments = new();
    public void SetUserComments(Dictionary<long, string> comments) => _userComments = comments;


    public PseudocodeLine[] Emit(StructuredNode root, IrBlock[] blocks)
    {
        _blocks = blocks;
        var lines = new List<PseudocodeLine>();
        _indentLevel = 0;

        if (!string.IsNullOrEmpty(_summary))
        {
            lines.Add(new PseudocodeLine("/* AI SUMMARY:", new[] { new PseudocodeSpan(0, 14, PseudocodeSyntax.Comment) }));
            foreach (var sl in WordWrap(_summary, 80))
                lines.Add(new PseudocodeLine("   " + sl, new[] { new PseudocodeSpan(0, sl.Length + 3, PseudocodeSyntax.Comment) }));
            lines.Add(new PseudocodeLine("*/", new[] { new PseudocodeSpan(0, 2, PseudocodeSyntax.Comment) }));
            lines.Add(new PseudocodeLine("", Array.Empty<PseudocodeSpan>()));
        }

        long funcAddr = blocks.Length > 0 ? blocks[0].StartAddress : -1;

        if (funcAddr != -1 && _userComments.TryGetValue(funcAddr, out var funcLevelComment))
        {
            string commentLine = "// " + funcLevelComment;
            lines.Add(new PseudocodeLine(commentLine,
                new[] { new PseudocodeSpan(0, commentLine.Length, PseudocodeSyntax.Comment) }));
        }

        EmitFunctionHeader(lines, funcAddr);
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
        var lines   = new List<PseudocodeLine>();
        _indentLevel = 0;

        var instrs  = block.Instructions;
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
                string target   = instr.Sources.Length > 0 ? FormatBranchTarget(instr.Sources[0]) : "???";
                string line     = $"if ({condText}) goto {target};";
                lines.Add(new PseudocodeLine(line, new[]
                {
                    new PseudocodeSpan(0, 3, PseudocodeSyntax.Keyword),
                    new PseudocodeSpan(4, condText.Length + 2, PseudocodeSyntax.Text),
                    new PseudocodeSpan(line.IndexOf("goto"), 4, PseudocodeSyntax.Keyword),
                }, (long)instr.OriginalAddress));
                lastCmp = null;
                continue;
            }

            if (instr.Opcode == IrOpcode.Branch)
            {
                string target = instr.Sources.Length > 0 ? FormatBranchTarget(instr.Sources[0]) : "???";
                string line   = $"goto {target};";
                lines.Add(new PseudocodeLine(line, new[]
                {
                    new PseudocodeSpan(0, 4, PseudocodeSyntax.Keyword),
                    new PseudocodeSpan(5, target.Length, PseudocodeSyntax.Text),
                }, (long)instr.OriginalAddress));
                continue;
            }

            if (lastCmp != null) lastCmp = null;

            EmitInstruction(instr, lines, block);
        }

        return lines.ToArray();
    }


    private string FormatBranchTarget(IrOperand op)
    {
        if (op.Kind == IrOperandKind.Label)    return $"block_{op.BlockIndex}";
        if (op.Kind == IrOperandKind.Constant) return $"loc_{(ulong)op.ConstantValue:X}";
        return FormatOperand(op);
    }

    private string FormatConditionForBranch(IrCondition cond, IrInstruction? cmpInstr)
    {
        if (cmpInstr != null && cmpInstr.Sources.Length >= 2)
        {
            string left  = FormatOperand(cmpInstr.Sources[0], cmpInstr);
            string right = FormatOperand(cmpInstr.Sources[1], cmpInstr);
            bool isTest  = cmpInstr.Opcode == IrOpcode.Test;
            string op    = FormatConditionOperator(cond, isTest);
            if (isTest && left == right) return $"{left} {op}";
            return $"{left} {op} {right}";
        }
        return FormatConditionCode(cond);
    }

    private void EmitNode(StructuredNode node, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        switch (node)
        {
            case SequenceNode seq:
                foreach (var child in seq.Children) EmitNode(child, lines, blocks);
                break;
            case BlockNode bn:      EmitBlockInstructions(bn.Block, lines); break;
            case IfNode ifn:        EmitIf(ifn, lines, blocks); break;
            case WhileNode wn:      EmitWhile(wn, lines, blocks); break;
            case ForNode fn:        EmitFor(fn, lines, blocks); break;
            case DoWhileNode dwn:   EmitDoWhile(dwn, lines, blocks); break;
            case SwitchNode sw:     EmitSwitch(sw, lines, blocks); break;
            case ReturnNode ret:    EmitReturn(ret, lines); break;
            case GotoNode gt:       EmitGoto(gt, lines); break;
        }
    }

    private void EmitBlockInstructions(IrBlock block, List<PseudocodeLine> lines)
    {
        _currentEmitBlock = block;
        var instrs = block.Instructions;
        for (int i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (instr.IsDead) continue;
            if (instr.Opcode is IrOpcode.Branch or IrOpcode.CondBranch) continue;
            if (instr.Opcode is IrOpcode.Cmp or IrOpcode.Test) continue;
            if (ShouldSkipInstruction(instr)) continue;

            if (i + 1 < instrs.Count && (instr.Opcode == IrOpcode.Load || instr.Opcode == IrOpcode.Store))
            {
                var next = instrs[i + 1];
                if (!next.IsDead && next.Opcode == IrOpcode.Add &&
                    next.Sources.Length == 2 && next.Sources[1].Kind == IrOperandKind.Constant)
                {
                    var ptr = instr.Opcode == IrOpcode.Load ? instr.Sources[0] : instr.Destination;
                    if (ptr.Kind == IrOperandKind.Memory && ptr.MemBase != Register.None &&
                        ptr.MemIndex == Register.None && ptr.MemDisplacement == 0)
                    {
                        var incReg = next.Destination.Register;
                        if (IrOperand.GetCanonical(ptr.MemBase) == IrOperand.GetCanonical(incReg))
                        {
                            string stmt    = FormatInstructionStatement(instr, block);
                            string ptrName = FormatOperand(new IrOperand { Kind = IrOperandKind.Register, Register = ptr.MemBase, SsaVersion = ptr.MemBaseSsaVersion }, instr);
                            ptrName = ApplyRename(ptrName);
                            if (stmt.Contains(ptrName))
                            {
                                stmt = stmt.Replace(ptrName, $"{ptrName}++");
                                EmitFormattedLine(stmt, lines, instr);
                                next.IsDead = true;
                                continue;
                            }
                        }
                    }
                }
            }

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
                if (instr.Opcode is IrOpcode.Add or IrOpcode.Sub && dc == Iced.Intel.Register.RSP)
                    return true;
            }
        }

        return instr.Opcode == IrOpcode.Phi;
    }

    private void EmitIf(IfNode ifn, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        string condText = FormatCondition(ifn.Condition, ifn.ConditionInstr);
        EmitFormattedLine($"if ({condText})", lines, ifn.ConditionInstr);
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(ifn.ThenBody, lines, blocks);
        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));

        if (ifn.ElseBody != null)
        {
            EmitFormattedLine("else", lines, null);
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
        EmitFormattedLine($"while ({condText})", lines, wn.ConditionInstr);
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
        EmitFormattedLine($"for ({initText}; {condText}; {stepText})", lines, fn.ConditionInstr);
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(fn.Body, lines, blocks);
        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));
    }

    private void EmitDoWhile(DoWhileNode dwn, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        EmitFormattedLine("do", lines, null);
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;
        EmitNode(dwn.Body, lines, blocks);
        _indentLevel--;
        string condText = FormatCondition(dwn.Condition, dwn.ConditionInstr);
        EmitFormattedLine($"}} while ({condText});", lines, dwn.ConditionInstr);
    }

    private void EmitSwitch(SwitchNode sw, List<PseudocodeLine> lines, IrBlock[] blocks)
    {
        string val = FormatOperand(sw.SwitchValue);
        EmitFormattedLine($"switch ({val})", lines, null);
        lines.Add(MakeIndentedLine("{", PseudocodeSyntax.Punctuation));
        _indentLevel++;

        foreach (var (caseVal, body) in sw.Cases)
        {
            EmitFormattedLine($"case {caseVal}:", lines, null);
            _indentLevel++;
            EmitNode(body, lines, blocks);
            EmitFormattedLine("break;", lines, null);
            _indentLevel--;
        }

        if (sw.DefaultBody != null)
        {
            EmitFormattedLine("default:", lines, null);
            _indentLevel++;
            EmitNode(sw.DefaultBody, lines, blocks);
            EmitFormattedLine("break;", lines, null);
            _indentLevel--;
        }

        _indentLevel--;
        lines.Add(MakeIndentedLine("}", PseudocodeSyntax.Punctuation));
    }

    private void EmitReturn(ReturnNode ret, List<PseudocodeLine> lines)
    {
        if (_currentEmitBlock != null && _currentEmitBlock.Successors.Count == 0 &&
            _currentEmitBlock.Instructions.Any(i => !i.IsDead && i.Opcode == IrOpcode.Return))
            return;

        if (ret.ReturnValue.HasValue)
            EmitFormattedLine($"return {FormatOperand(ret.ReturnValue.Value)};", lines, null);
        else
            EmitFormattedLine("return;", lines, null);
    }

    private void EmitGoto(GotoNode gt, List<PseudocodeLine> lines)
        => EmitFormattedLine($"goto block_{gt.TargetBlockIndex};", lines, null);

    private void EmitInstruction(IrInstruction instr, List<PseudocodeLine> lines, IrBlock currentBlock)
    {
        string text = FormatInstructionStatement(instr, currentBlock);
        if (string.IsNullOrEmpty(text)) return;
        EmitFormattedLine(text, lines, instr);
    }

    private void EmitFormattedLine(string text, List<PseudocodeLine> lines, IrInstruction? instrForSpans)
    {
        if (string.IsNullOrEmpty(text)) return;
        var    spans    = BuildSpans(text, instrForSpans ?? new IrInstruction { Opcode = IrOpcode.Nop });
        string indented = GetIndent() + text;
        long   addr     = instrForSpans != null ? (long)instrForSpans.OriginalAddress : -1L;

        if (addr != -1 && _userComments.TryGetValue(addr, out var comment))
        {
            int    commentStart = indented.Length;
            string commentText  = " // " + comment;
            indented += commentText;
            var newSpans = new List<PseudocodeSpan>(spans);
            newSpans.Add(new PseudocodeSpan(commentStart, commentText.Length, PseudocodeSyntax.Comment));
            spans = newSpans.ToArray();
        }

        lines.Add(new PseudocodeLine(indented, spans, addr));
    }


    private int GetPrecedence(IrOpcode opcode) => opcode switch
    {
        IrOpcode.Mul or IrOpcode.IMul or IrOpcode.Div or IrOpcode.IDiv or IrOpcode.Mod => 3,
        IrOpcode.Add or IrOpcode.Sub => 4,
        IrOpcode.Shl or IrOpcode.Shr or IrOpcode.Sar => 5,
        IrOpcode.Cmp or IrOpcode.Test => 6,
        IrOpcode.And => 8,
        IrOpcode.Xor => 9,
        IrOpcode.Or  => 10,
        _ => 15
    };

    private string FormatInstructionStatement(IrInstruction instr, IrBlock currentBlock)
    {
        string expr = FormatExpression(instr, currentBlock, forceExpression: false);
        if (string.IsNullOrEmpty(expr)) return "";
        if (!expr.StartsWith("//") && !expr.EndsWith(";")) return expr + ";";
        return expr;
    }

    private string FormatExpression(IrInstruction instr, IrBlock? currentBlock = null, bool forceExpression = false)
    {
        bool definesDest = instr.DefinesDest && !forceExpression;
        switch (instr.Opcode)
        {
            case IrOpcode.Nop: return "";

            case IrOpcode.Assign:
            {
                string dst = FormatOperand(instr.Destination, instr);
                string src = FormatOperand(instr.Sources[0], instr);
                if (instr.Condition != IrCondition.None)
                {
                    string condStr = FormatCondition(instr.Condition, instr.ConditionInstr);
                    if (instr.Sources.Length >= 2)
                    {
                        string val = $"{condStr} ? {src} : {FormatOperand(instr.Sources[1], instr)}";
                        return definesDest ? $"{dst} = {val}" : val;
                    }
                    return definesDest ? $"{dst} = {condStr}" : condStr;
                }
                return definesDest ? $"{dst} = {src}" : src;
            }

            case IrOpcode.Add:  return FormatBinOp(instr, "+",  definesDest);
            case IrOpcode.Sub:  return FormatBinOp(instr, "-",  definesDest);
            case IrOpcode.Mul or IrOpcode.IMul: return FormatBinOp(instr, "*", definesDest);
            case IrOpcode.Div or IrOpcode.IDiv: return FormatBinOp(instr, "/", definesDest);
            case IrOpcode.Mod:  return FormatBinOp(instr, "%",  definesDest);
            case IrOpcode.And:  return FormatBinOp(instr, "&",  definesDest);
            case IrOpcode.Or:   return FormatBinOp(instr, "|",  definesDest);
            case IrOpcode.Xor:  return FormatBinOp(instr, "^",  definesDest);
            case IrOpcode.Shl:  return FormatBinOp(instr, "<<", definesDest);
            case IrOpcode.Shr or IrOpcode.Sar: return FormatBinOp(instr, ">>", definesDest);

            case IrOpcode.Neg: return FormatUnaryOp(instr, "-", instr.Sources[0], definesDest);
            case IrOpcode.Not: return FormatUnaryOp(instr, "~", instr.Sources[0], definesDest);

            case IrOpcode.Load:
            {
                string dst = FormatOperand(instr.Destination, instr);
                if (instr.Destination.Kind == IrOperandKind.Register &&
                    instr.Destination.Register == Register.None) return "";
                string src = FormatMemAccess(instr.Sources[0], instr);
                return definesDest ? $"{dst} = {src}" : src;
            }

            case IrOpcode.Store:
            {
                string dst = FormatMemAccess(instr.Destination, instr);
                if (instr.Sources.Length >= 2)
                    return $"{dst} = {FormatOperand(instr.Sources[1], instr)}";
                return $"{dst} = {FormatOperand(instr.Sources[0], instr)}";
            }

            case IrOpcode.Call: return FormatCall(instr, definesDest);

            case IrOpcode.Return:
                return instr.Sources.Length > 0
                    ? $"return {FormatOperand(instr.Sources[0], instr)}"
                    : "return";

            case IrOpcode.Phi:
            {
                string dst  = FormatOperand(instr.Destination, instr);
                var    srcs = instr.Sources.Select((s, i) =>
                {
                    int blk = instr.PhiSourceBlocks != null && i < instr.PhiSourceBlocks.Length
                        ? instr.PhiSourceBlocks[i] : -1;
                    return $"{FormatOperand(s, instr)}/*b{blk}*/";
                });
                return $"// {dst} = φ({string.Join(", ", srcs)})";
            }

            case IrOpcode.ZeroExtend:
            case IrOpcode.SignExtend:
            case IrOpcode.Truncate:
            {
                string dst      = FormatOperand(instr.Destination, instr);
                string src      = FormatOperand(instr.Sources[0], instr);
                bool   sameType = instr.Destination.Type != TypeInfo.Unknown && instr.Destination.Type == instr.Sources[0].Type;
                string castStr  = sameType ? "" : $"({TypeName(instr.Destination.BitSize, instr.Destination.Type)})";
                return definesDest ? $"{dst} = {castStr}{src}" : $"{castStr}{src}";
            }

            case IrOpcode.Cmp:   return $"// cmp {FormatOperand(instr.Sources[0], instr)}, {FormatOperand(instr.Sources[1], instr)}";
            case IrOpcode.Test:  return $"// test {FormatOperand(instr.Sources[0], instr)}, {FormatOperand(instr.Sources[1], instr)}";

            case IrOpcode.StackAlloc:
                return $"{FormatOperand(instr.Destination, instr)} = alloca({FormatOperand(instr.Sources[0], instr)})";

            case IrOpcode.Rol:
                return definesDest
                    ? $"{FormatOperand(instr.Destination, instr)} = _rotl({FormatOperand(instr.Sources[0], instr)}, {FormatOperand(instr.Sources[1], instr)})"
                    : $"_rotl({FormatOperand(instr.Sources[0], instr)}, {FormatOperand(instr.Sources[1], instr)})";

            case IrOpcode.Ror:
                return definesDest
                    ? $"{FormatOperand(instr.Destination, instr)} = _rotr({FormatOperand(instr.Sources[0], instr)}, {FormatOperand(instr.Sources[1], instr)})"
                    : $"_rotr({FormatOperand(instr.Sources[0], instr)}, {FormatOperand(instr.Sources[1], instr)})";

            case IrOpcode.Bswap:
                return definesDest
                    ? $"{FormatOperand(instr.Destination, instr)} = __builtin_bswap{instr.BitSize}({FormatOperand(instr.Sources[0], instr)})"
                    : $"__builtin_bswap{instr.BitSize}({FormatOperand(instr.Sources[0], instr)})";

            default:
                if (instr.Comment != null) return $"// {instr.Comment} @ 0x{instr.OriginalAddress:X}";
                return $"// unhandled instruction {instr.Opcode} @ 0x{instr.OriginalAddress:X}";
        }
    }

    private string FormatUnaryOp(IrInstruction instr, string op, IrOperand src, bool definesDest)
    {
        string expr = $"{op}{FormatOperand(src, instr)}";
        return definesDest ? $"{FormatOperand(instr.Destination, instr)} = {expr}" : expr;
    }

    private string FormatBinOp(IrInstruction instr, string op, bool definesDest)
    {
        if (instr.Sources.Length < 2) return $"// incomplete {instr.Opcode}";

        if (instr.Destination.Kind is IrOperandKind.Memory or IrOperandKind.StackSlot)
            return $"{FormatMemAccess(instr.Destination, instr)} {op}= {FormatOperand(instr.Sources[1], instr)}";

        string dst   = FormatOperand(instr.Destination, instr);
        string left  = FormatOperand(instr.Sources[0], instr);
        string right = FormatOperand(instr.Sources[1], instr);

        bool destTypeDiffers = instr.Destination.Type != TypeInfo.Unknown &&
                               instr.Destination.Type != instr.Sources[0].Type &&
                               instr.Destination.Type != instr.Sources[1].Type;

        string castPrefix = "";
        if (destTypeDiffers && definesDest)
            castPrefix = $"({TypeName(instr.Destination.BitSize, instr.Destination.Type)})";

        int myPrec = GetPrecedence(instr.Opcode);
        if (instr.Sources[0].Kind == IrOperandKind.Expression)
        {
            int leftPrec = GetPrecedence(instr.Sources[0].Expression!.Opcode);
            if (leftPrec > myPrec) left = $"({left})";
        }
        if (instr.Sources[1].Kind == IrOperandKind.Expression)
        {
            int rightPrec = GetPrecedence(instr.Sources[1].Expression!.Opcode);
            if (rightPrec >= myPrec) right = $"({right})";
        }

        string expr = string.IsNullOrEmpty(castPrefix)
            ? $"{left} {op} {right}"
            : $"{castPrefix}({left} {op} {right})";

        if (!definesDest) return expr;

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

        string target = FormatCallTarget(instr.Sources[0], instr);
        var    args   = new List<string>();
        for (int i = 1; i < instr.Sources.Length; i++)
        {
            try { args.Add(FormatOperand(instr.Sources[i], instr)); }
            catch { args.Add("unknown_arg"); }
        }

        string methodCall;

        VTableDetector.VTableCall? vtMatch = null;
        foreach (var v in _vtables)
            if (v.InstructionIndex >= 0 && instr.OriginalAddress > 0) { vtMatch = v; break; }

        if (vtMatch != null && args.Count > 0)
        {
            string thisPtr = args[0];
            args.RemoveAt(0);
            methodCall = $"{thisPtr}->{vtMatch.MethodName}({(args.Count > 0 ? string.Join(", ", args) : "")})";
        }
        else
        {
            var    op0        = instr.Sources[0];
            bool   isKnownApi = false;
            ulong  apiAddr    = 0;

            if (op0.Kind == IrOperandKind.Memory && op0.MemBase == Register.None && op0.MemDisplacement != 0)
                apiAddr = (ulong)op0.MemDisplacement;
            else if (op0.Kind == IrOperandKind.Constant)
                apiAddr = (ulong)op0.ConstantValue;
            else if (op0.Kind == IrOperandKind.Expression && op0.Expression != null &&
                     op0.Expression.Opcode == IrOpcode.Load && op0.Expression.Sources.Length == 1)
            {
                var src0 = op0.Expression.Sources[0];
                if (src0.Kind == IrOperandKind.Memory && src0.MemBase == Register.None && src0.MemDisplacement != 0)
                    apiAddr = (ulong)src0.MemDisplacement;
                else if (src0.Kind == IrOperandKind.Constant)
                    apiAddr = (ulong)src0.ConstantValue;
            }
            else if (op0.Kind == IrOperandKind.Expression && op0.Expression != null &&
                     op0.Expression.Opcode == IrOpcode.Assign && op0.Expression.Sources.Length == 1)
            {
                var src0 = op0.Expression.Sources[0];
                if (src0.Kind == IrOperandKind.Memory && src0.MemBase == Register.None && src0.MemDisplacement != 0)
                    apiAddr = (ulong)src0.MemDisplacement;
                else if (src0.Kind == IrOperandKind.Constant)
                    apiAddr = (ulong)src0.ConstantValue;
            }

            if (apiAddr != 0 && _imports.TryGetValue(apiAddr, out var apiName))
            {
                isKnownApi = true;
                target     = apiName;
            }

            if (op0.Kind is IrOperandKind.Register or IrOperandKind.Expression
                         or IrOperandKind.StackSlot or IrOperandKind.Memory && !isKnownApi)
            {
                string retType  = definesDest && instr.Destination.Type != TypeInfo.Unknown ? TypeName(instr.Destination.BitSize, instr.Destination.Type) : "void*";
                var    argTypes = new List<string>();
                for (int i = 1; i < instr.Sources.Length; i++)
                {
                    string t = instr.Sources[i].Type != TypeInfo.Unknown ? TypeName(instr.Sources[i].BitSize, instr.Sources[i].Type) : "void*";
                    argTypes.Add(t);
                }
                string sigArgs = argTypes.Count > 0 ? string.Join(", ", argTypes) : "void*";
                target = $"(({retType} (*)({sigArgs})){target})";
            }

            if (isKnownApi && args.Count > 0)
            {
                if (target.EndsWith("::SetErrorMode", StringComparison.OrdinalIgnoreCase))
                {
                    if (args[0] == "0x8001" || args[0] == "32769")
                        args[0] = "SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX";
                }
                else if (target.EndsWith("::ExitWindowsEx", StringComparison.OrdinalIgnoreCase))
                {
                    if      (args[0] == "0x2" || args[0] == "2") args[0] = "EWX_REBOOT";
                    else if (args[0] == "0x8" || args[0] == "8") args[0] = "EWX_POWEROFF";
                }
                else if (target.EndsWith("::GetVersion", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Count > 0 && (args[0] == "0x6" || args[0] == "6"))
                        args[0] = "_WIN32_WINNT_VISTA";
                }
            }

            methodCall = $"{target}({(args.Count > 0 ? string.Join(", ", args) : "")})";
        }

        string dst = FormatOperand(instr.Destination, instr);
        if (definesDest && instr.Destination.Kind == IrOperandKind.Register && instr.Destination.Register != Register.None)
            return $"{dst} = {methodCall}";

        return methodCall;
    }

    private string FormatOperand(IrOperand op, out bool isAi, IrInstruction? instr = null)
    {
        isAi = false;

        if (op.Kind == IrOperandKind.Register && op.Register != Register.None)
        {
            Register canon  = IrOperand.GetCanonical(op.Register);
            string   ssaKey = $"reg_{canon}_{op.SsaVersion}";
            if (_userRenames.TryGetValue(ssaKey, out var ssaSym))
            {
                isAi = ssaSym.IsAiGenerated;
                return ssaSym.Name + (isAi ? " /* AI */" : "");
            }
        }

        string name = op.Kind switch
        {
            IrOperandKind.Constant   => FormatConstant(op.ConstantValue, op.BitSize, instr),
            IrOperandKind.Register   => FormatRegisterName(op),
            IrOperandKind.Memory     => FormatMemAccess(op, instr),
            IrOperandKind.StackSlot  => FormatStackVar(op.StackOffset, op.SsaVersion),
            IrOperandKind.Expression => FormatExpression(op.Expression!, null, forceExpression: true),
            IrOperandKind.Flag       => "flags",
            IrOperandKind.Label      => $"block_{op.BlockIndex}",
            _                        => "unknown_op"
        };

        return op.Kind switch
        {
            IrOperandKind.Register or IrOperandKind.StackSlot => GetRenamed(name, out isAi),
            _ => name
        };
    }

    private string FormatOperand(IrOperand op, IrInstruction? instr = null) => FormatOperand(op, out _, instr);

    private string FormatRegisterName(IrOperand op) => NamingConventions.GetVariableName(op);

    private string FormatStackVar(int offset, int ssaVersion) => NamingConventions.GetStackVariableName(offset);


    
    private string FormatConstant(long value, byte bitSize, IrInstruction? instr = null)
    {
        if (value == 0)  return "0";
        if (value == 1)  return "1";
        if (value == -1) return "-1";
        if (value >= -999 && value <= 999) return value.ToString();


        string? knownConst = SignatureCache.GetNameForConstant(unchecked((ulong)value));
        if (knownConst == null && bitSize <= 32)
            knownConst = SignatureCache.GetNameForConstant((ulong)(uint)value);
        if (knownConst != null) return knownConst;

        if (instr != null && (
            instr.Opcode == IrOpcode.And || instr.Opcode == IrOpcode.Or || 
            instr.Opcode == IrOpcode.Xor || instr.Opcode == IrOpcode.Shl || 
            instr.Opcode == IrOpcode.Shr || instr.Opcode == IrOpcode.Sar || 
            instr.Opcode == IrOpcode.Test))
        {
            if (value < 0) return $"-0x{-value:X}";
            return $"0x{value:X}";
        }

        ulong uvVal = (ulong)value;
        if (value > 0)
        {
            if ((uvVal & (uvVal - 1)) == 0) return $"0x{value:X}";
            if (uvVal == 0xFF || uvVal == 0xFFFF || uvVal == 0xFFFFFFFF) return $"0x{value:X}";
        }

        if (bitSize >= 32 && uvVal >= 0x20202020) 
        {
            string? inlineStr = TryGetInlineAscii(uvVal);
            if (inlineStr != null)
            {
                return $"0x{value:X} /* \"{inlineStr}\" */";
            }
        }

        if (bitSize >= 32 && (value > 0x10000 || value < -0x10000))
        {
            if (value < 0)
                return $"(void*)-0x{-value:X}";

            if (_imports.TryGetValue((ulong)value, out var impName))
            {
                int bang = impName.IndexOf('!');
                return "&" + (bang >= 0 ? impName.Substring(bang + 1) : impName);
            }

            ulong uv = (ulong)value;
            if (uv >= 0x7FFF0000 || (uv & 0xFF000000) == 0xFF000000 || uv == 0xBFFFFFFF)
                return $"0x{uv:X}";

            if (_stringExtractor != null)
            {
                string extracted = _stringExtractor((ulong)value);
                if (!string.IsNullOrEmpty(extracted))
                {
                    string esc = extracted
                        .Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
                    return $"\"{esc}\"";
                }
            }

            return $"&g_Data_{value:X}";
        }

        if (value < 0) return $"-0x{-value:X}";
        return $"0x{value:X}";
    }

    private string FormatMemAccess(IrOperand op, IrInstruction? instr = null)
    {
        if (op.Kind == IrOperandKind.StackSlot)
            return ApplyRename(FormatStackVar(op.StackOffset, op.SsaVersion));

        if (op.Kind != IrOperandKind.Memory)
            return FormatOperand(op, instr);

        string baseStr = "";
        if (op.MemBase != Register.None)
        {
            baseStr = !string.IsNullOrEmpty(op.MemBaseName)
                ? op.MemBaseName
                : FormatRegisterName(new IrOperand { Kind = IrOperandKind.Register, Register = op.MemBase, SsaVersion = op.MemBaseSsaVersion });
            baseStr = ApplyRename(baseStr);
        }

        string indexStr  = "";
        bool   omitScale = false;
        if (op.MemIndex != Register.None)
        {
            indexStr = !string.IsNullOrEmpty(op.MemIndexName)
                ? op.MemIndexName
                : FormatRegisterName(new IrOperand { Kind = IrOperandKind.Register, Register = op.MemIndex, SsaVersion = op.MemIndexSsaVersion });
            indexStr = ApplyRename(indexStr);

            if (op.MemBase != Register.None && op.MemScale > 1)
            {
                var baseType = GetOperandType(op.MemBase, op.MemBaseSsaVersion);
                if (baseType.PointerLevel > 0 && GetTypeSize(baseType.BaseType) == op.MemScale)
                    omitScale = true;
                else if (op.MemScale is 2 or 4 or 8)
                {
                    var targetPt    = op.MemScale switch { 2 => PrimitiveType.UInt16, 4 => PrimitiveType.UInt32, 8 => PrimitiveType.UInt64, _ => PrimitiveType.UInt8 };
                    string ptrType  = FormatInferredType(new TypeInfo { BaseType = targetPt, PointerLevel = 1 });
                    baseStr         = $"(({ptrType}){baseStr})";
                    omitScale       = true;
                }
            }
        }

        if (op.MemIndex != Register.None && op.MemDisplacement == 0)
        {
            string b         = !string.IsNullOrEmpty(baseStr) ? baseStr : "0";
            string scaleStr  = (op.MemScale > 1 && !omitScale) ? $" * {op.MemScale}" : "";
            return $"{b}[{indexStr}{scaleStr}]";
        }

        if (op.MemBase == Register.None && op.MemIndex == Register.None && op.MemDisplacement != 0)
        {
            ulong addr = (ulong)op.MemDisplacement;

            if (_imports.TryGetValue(addr, out var impName))
            {
                int bang = impName.IndexOf('!');
                return bang >= 0 ? impName.Substring(bang + 1) : impName;
            }

            _stringExtractor?.Invoke(addr);
            if (_strings.TryGetValue((long)addr, out var str))
            {
                if (str.StartsWith("/* STR_ERR")) return $"{str} g_0x{addr:X}";
                string esc = str.Replace("\\", "\\\\").Replace("\"", "\\\"")
                               .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
                return $"\"{esc}\"";
            }

            string globalKey = $"g_0x{op.MemDisplacement:X}";
            if (_userRenames.TryGetValue(globalKey, out var globalSym))
                return globalSym.Name + (globalSym.IsAiGenerated ? " /* AI */" : "");

            return globalKey;
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(baseStr))
        {
            var canonical = IrOperand.GetCanonical(op.MemBase);
            if (canonical != Register.RSP && canonical != Register.RBP &&
                op.MemIndex == Register.None && op.MemDisplacement != 0)
            {
                var baseKey = (IrOperand.GetCanonical(op.MemBase), op.MemBaseSsaVersion);
                if (_typeMap.TryGetValue(baseKey, out var baseType) &&
                    baseType.BaseType == PrimitiveType.Struct && baseType.TypeName != null)
                {
                    var stTyped = _structs.FirstOrDefault(s => s.Name == baseType.TypeName);
                    if (stTyped != null && stTyped.Fields.TryGetValue(op.MemDisplacement, out var fieldTyped))
                        return $"{baseStr}->{fieldTyped.Name}";

                    string? knownFieldTyped = SignatureCache.GetFieldName(baseType.TypeName, (int)op.MemDisplacement);
                    if (knownFieldTyped != null)
                        return $"{baseStr}->{knownFieldTyped}";
                }

                foreach (var st in _structs)
                    if (st.Fields.TryGetValue(op.MemDisplacement, out var field))
                        return $"{baseStr}->{field.Name}";

                string? knownFieldFallback = SignatureCache.GetFieldName(null, (int)op.MemDisplacement);
                if (knownFieldFallback != null)
                    return $"{baseStr}->{knownFieldFallback}";
            }
            parts.Add(baseStr);
        }

        if (!string.IsNullOrEmpty(indexStr))
            parts.Add(op.MemScale > 1 && !omitScale ? $"{indexStr} * {op.MemScale}" : indexStr);

        if (!string.IsNullOrEmpty(baseStr) && !string.IsNullOrEmpty(indexStr) && op.MemDisplacement == 0)
        {
            string scaleStr = (op.MemScale > 1 && !omitScale) ? $" * {op.MemScale}" : "";
            return $"{baseStr}[{indexStr}{scaleStr}]";
        }

        if (!string.IsNullOrEmpty(baseStr) && string.IsNullOrEmpty(indexStr) && op.MemDisplacement == 0)
            return $"*{baseStr}";

        if (op.MemDisplacement != 0)
            parts.Add(op.MemDisplacement < 0 ? $"- 0x{-op.MemDisplacement:X}" : $"0x{op.MemDisplacement:X}");

        if (parts.Count == 0) return $"*({TypeName(op.BitSize, op.Type)}*)nullptr";

        string addrExpr = parts[0];
        if (parts.Count > 1)
        {
            var sb = new StringBuilder();
            sb.Append(parts[0]);
            for (int i = 1; i < parts.Count; i++)
                sb.Append(parts[i].StartsWith("-") ? $" {parts[i]}" : $" + {parts[i]}");
            addrExpr = $"({sb})";
        }

        string typeName = TypeName(op.BitSize, op.Type);
        if (!typeName.EndsWith("*")) typeName += "*";
        return $"*({typeName}){addrExpr}";
    }


    private string FormatCallTarget(IrOperand op, IrInstruction? instr = null)
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
        return FormatOperand(op, instr);
    }

    private string FormatCondition(IrCondition cond, IrInstruction? condInstr)
    {
        if (condInstr != null)
        {
            if (condInstr.Opcode == IrOpcode.Cmp && condInstr.Sources.Length >= 2)
            {
                string left  = FormatOperand(condInstr.Sources[0], out _, condInstr);
                string right = FormatOperand(condInstr.Sources[1], out _, condInstr);
                return $"{left} {FormatConditionOperator(cond, false)} {right}";
            }
            if (condInstr.Opcode == IrOpcode.Test && condInstr.Sources.Length >= 2)
            {
                string left  = FormatOperand(condInstr.Sources[0], out _, condInstr);
                string right = FormatOperand(condInstr.Sources[1], out _, condInstr);
                string op    = cond switch
                {
                    IrCondition.Equal    => "== 0",
                    IrCondition.NotEqual => "!= 0",
                    IrCondition.Sign     => "< 0",
                    IrCondition.NotSign  => ">= 0",
                    _                    => FormatConditionCode(cond) + " 0"
                };
                return left == right ? $"{left} {op}" : $"({left} & {right}) {op}";
            }
            if (condInstr.DefinesDest)
            {
                string dst = FormatOperand(condInstr.Destination, out _, condInstr);
                string op  = cond switch
                {
                    IrCondition.Equal          => "== 0",
                    IrCondition.NotEqual       => "!= 0",
                    IrCondition.SignedLess      => "< 0",
                    IrCondition.SignedLessEq    => "<= 0",
                    IrCondition.SignedGreater   => "> 0",
                    IrCondition.SignedGreaterEq => ">= 0",
                    IrCondition.Sign            => "< 0",
                    IrCondition.NotSign         => ">= 0",
                    IrCondition.UnsignedAbove   => "!= 0",
                    IrCondition.UnsignedBelowEq => "== 0",
                    _                           => FormatConditionCode(cond) + " 0"
                };
                return $"{dst} {op}";
            }
        }
        return $"_cond({FormatConditionCode(cond)})";
    }

    private static string FormatConditionOperator(IrCondition cond, bool isTest) => isTest
        ? cond switch
        {
            IrCondition.Equal    => "== 0",
            IrCondition.NotEqual => "!= 0",
            _                    => FormatConditionCode(cond),
        }
        : cond switch
        {
            IrCondition.Equal          => "==",
            IrCondition.NotEqual       => "!=",
            IrCondition.SignedLess      => "<",
            IrCondition.SignedLessEq    => "<=",
            IrCondition.SignedGreater   => ">",
            IrCondition.SignedGreaterEq => ">=",
            IrCondition.UnsignedBelow   => "<u",
            IrCondition.UnsignedBelowEq => "<=u",
            IrCondition.UnsignedAbove   => ">u",
            IrCondition.UnsignedAboveEq => ">=u",
            _                           => FormatConditionCode(cond),
        };

    private static string FormatConditionCode(IrCondition cond) => cond switch
    {
        IrCondition.Equal          => "==",
        IrCondition.NotEqual       => "!=",
        IrCondition.SignedLess      => "<",
        IrCondition.SignedLessEq    => "<=",
        IrCondition.SignedGreater   => ">",
        IrCondition.SignedGreaterEq => ">=",
        IrCondition.UnsignedBelow   => "<",
        IrCondition.UnsignedBelowEq => "<=",
        IrCondition.UnsignedAbove   => ">",
        IrCondition.UnsignedAboveEq => ">=",
        IrCondition.Sign            => "< 0",
        IrCondition.NotSign         => ">= 0",
        IrCondition.Overflow        => "overflow",
        IrCondition.NotOverflow     => "no_overflow",
        _                           => "???",
    };

    private static string TypeName(byte bitSize, TypeInfo type)
    {
        if (type != TypeInfo.Unknown) return FormatInferredType(type);
        return bitSize switch
        {
            8  => "uint8_t",
            16 => "uint16_t",
            32 => "uint32_t",
            64 => "uint64_t",
            _  => "void",
        };
    }

    private void EmitFunctionHeader(List<PseudocodeLine> lines, long address)
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
            lines.Add(new PseudocodeLine(text, new[] { new PseudocodeSpan(0, text.Length, PseudocodeSyntax.Function) }, address));
        }
        else
        {
            lines.Add(new PseudocodeLine("void sub_unknown()", new[]
            {
                new PseudocodeSpan(0, 5,  PseudocodeSyntax.Type),
                new PseudocodeSpan(5, 15, PseudocodeSyntax.Function),
            }, address));
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
                if (dst.Kind != IrOperandKind.StackSlot) continue;

                string name = ApplyRename(FormatStackVar(dst.StackOffset, 0));
                if (!declaredVars.Add(name)) continue;

                string type = FormatInferredType(dst.Type != TypeInfo.Unknown
                    ? dst.Type
                    : new TypeInfo { BaseType = PrimitiveType.UInt64 });
                string decl = $"    {type} {name};";
                lines.Add(new PseudocodeLine(decl, new[]
                {
                    new PseudocodeSpan(4,              type.Length, PseudocodeSyntax.Type),
                    new PseudocodeSpan(5 + type.Length, name.Length, PseudocodeSyntax.Variable),
                }));
            }
        }
        if (declaredVars.Count > 0) lines.Add(PseudocodeLine.Empty);
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
            PrimitiveType.Int8    => "int8_t",
            PrimitiveType.UInt8   => "uint8_t",
            PrimitiveType.Int16   => "int16_t",
            PrimitiveType.UInt16  => "uint16_t",
            PrimitiveType.Int32   => "int",
            PrimitiveType.UInt32  => "unsigned int",
            PrimitiveType.Int64   => "int64_t",
            PrimitiveType.UInt64  => "uint64_t",
            PrimitiveType.Float32 => "float",
            PrimitiveType.Float64 => "double",
            PrimitiveType.Bool    => "bool",
            PrimitiveType.Void    => "void",
            PrimitiveType.Struct  => "struct",
            _                     => "uint64_t",
        };
    }

    private string GetRenamed(string name, out bool isAi)
    {
        isAi = false;
        int maxDepth = 10;
        while (_userRenames.TryGetValue(name, out var renamed) && maxDepth-- > 0)
        {
            isAi = renamed.IsAiGenerated;
            name = renamed.Name;
        }
        return name + (isAi ? " /* AI */" : "");
    }

    private string ApplyRename(string name) => GetRenamed(name, out _);

    private string GetIndent() => new string(' ', _indentLevel * 4);

    private PseudocodeLine MakeLine(string text, PseudocodeSyntax kind) =>
        new(GetIndent() + text, new[] { new PseudocodeSpan(0, text.Length + _indentLevel * 4, kind) });

    private PseudocodeLine MakeIndentedLine(string text, PseudocodeSyntax kind) => MakeLine(text, kind);

    private PseudocodeSpan[] BuildSpans(string text, IrInstruction instr)
    {
        if (text.StartsWith("//") || instr.Opcode == IrOpcode.Phi)
            return new[] { new PseudocodeSpan(0, _indentLevel * 4 + text.Length, PseudocodeSyntax.Comment) };

        int offset = _indentLevel * 4;
        var spans  = new List<PseudocodeSpan>();
        var regex  = new System.Text.RegularExpressions.Regex(
            @"(?<String>""[^""]*"")|(?<AiComment>/\*\s*AI\s*\*/)|(?<Comment>//.*)|(?<Number>\b0x[0-9a-fA-F]+\b|\b\d+\b)|(?<Keyword>\b(if|else|while|do|for|switch|case|default|break|continue|return|goto|alloca|sizeof)\b)|(?<Type>\b(int8_t|uint8_t|int16_t|uint16_t|int32_t|uint32_t|int64_t|uint64_t|float|double|bool|void|int|unsigned|struct)\b)|(?<Method>\b[a-zA-Z_]\w*(?=\s*\())|(?<Var>\b[a-zA-Z_]\w*\b)|(?<Punct>[{}()\[\].,;])|(?<Op>[+\-*/%&|^~<>=!?:]+)");

        foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
        {
            if (m.Groups["Comment"].Success || m.Groups["AiComment"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Comment));
            else if (m.Groups["String"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.String));
            else if (m.Groups["Number"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Number));
            else if (m.Groups["Keyword"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Keyword));
            else if (m.Groups["Type"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Type));
            else if (m.Groups["Method"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Function));
            else if (m.Groups["Var"].Success)
            {
                string val = m.Groups["Var"].Value;
                if (val.StartsWith("loc_") || val.Contains("sub_") || val.StartsWith("block_") || val.StartsWith("g_0x"))
                    spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Address));
                else if (_aiNames.Contains(val))
                    spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.VariableAi));
                else
                    spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Variable));
            }
            else if (m.Groups["Op"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Operator));
            else if (m.Groups["Punct"].Success)
                spans.Add(new PseudocodeSpan(offset + m.Index, m.Length, PseudocodeSyntax.Punctuation));
        }

        if (spans.Count == 0)
            spans.Add(new PseudocodeSpan(0, _indentLevel * 4 + text.Length, PseudocodeSyntax.Text));

        return spans.ToArray();
    }

    private TypeInfo GetOperandType(Register reg, int ssa)
    {
        if (_typeMap.TryGetValue((IrOperand.GetCanonical(reg), ssa), out var t)) return t;
        if (_blocks == null) return TypeInfo.Unknown;
        foreach (var block in _blocks)
            foreach (var instr in block.Instructions)
                if (instr.DefinesDest &&
                    IrOperand.GetCanonical(instr.Destination.Register) == IrOperand.GetCanonical(reg) &&
                    instr.Destination.SsaVersion == ssa)
                    return instr.Destination.Type;
        return TypeInfo.Unknown;
    }

    private static int GetTypeSize(PrimitiveType type) => type switch
    {
        PrimitiveType.Int8  or PrimitiveType.UInt8  => 1,
        PrimitiveType.Int16 or PrimitiveType.UInt16 => 2,
        PrimitiveType.Int32 or PrimitiveType.UInt32 or PrimitiveType.Float32 => 4,
        PrimitiveType.Int64 or PrimitiveType.UInt64 or PrimitiveType.Float64 => 8,
        _ => 1
    };

    private static List<string> WordWrap(string text, int maxLen)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (var line in text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            if (line.Length <= maxLen) { result.Add(line); continue; }
            string rem = line;
            while (rem.Length > maxLen)
            {
                int wi = rem.LastIndexOf(' ', maxLen);
                if (wi == -1) wi = maxLen;
                result.Add(rem.Substring(0, wi).TrimEnd());
                rem = rem.Substring(wi).TrimStart();
            }
            if (rem.Length > 0) result.Add(rem);
        }
        return result;
    }

    private static string? TryGetInlineAscii(ulong value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        var chars = new List<char>();

        foreach (byte b in bytes)
        {
            if (b == 0) continue; 
            if (b < 0x20 || b > 0x7E) return null; 
            chars.Add((char)b);
        }

        if (chars.Count >= 4)
        {
            return new string(chars.ToArray());
        }
        return null;
    }
}