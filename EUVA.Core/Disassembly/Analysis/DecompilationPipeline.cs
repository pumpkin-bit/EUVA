// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Iced.Intel;
using EUVA.Core.Scripting;

namespace EUVA.Core.Disassembly.Analysis;

public static class FunctionHasher
{
    private static readonly uint[] Crc32Table = GenerateCrc32Table();

    private static uint[] GenerateCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint CalculateCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        return ~crc;
    }

    public static unsafe uint GetNormalizedCrc32(byte* data, int length, int bitness)
    {
        int scanLen = Math.Min(length, 32);
        byte[] buffer = new byte[scanLen];
        for (int i = 0; i < scanLen; i++) buffer[i] = data[i];

        var codeReader = new ByteArrayCodeReader(buffer);
        var decoder = Decoder.Create(bitness, codeReader);
        decoder.IP = 0;

        while (decoder.IP < (ulong)scanLen)
        {
            ulong instrStart = decoder.IP;
            decoder.Decode(out var instr);
            int instrLen = instr.Length;
            
            if (instr.IsInvalid || instrStart + (ulong)instrLen > (ulong)scanLen) 
                break;

            if (instr.IsCallNear || instr.IsJmpNear || instr.IsJccNear)
            {
                if (instrLen >= 5)
                {
                    buffer[instrStart + (ulong)instrLen - 4] = 0;
                    buffer[instrStart + (ulong)instrLen - 3] = 0;
                    buffer[instrStart + (ulong)instrLen - 2] = 0;
                    buffer[instrStart + (ulong)instrLen - 1] = 0;
                }
                else if (instrLen >= 2)
                {
                    buffer[instrStart + (ulong)instrLen - 1] = 0;
                }
            }
            else if (instr.IsIPRelativeMemoryOperand && instrLen >= 4)
            {
                buffer[instrStart + (ulong)instrLen - 4] = 0;
                buffer[instrStart + (ulong)instrLen - 3] = 0;
                buffer[instrStart + (ulong)instrLen - 2] = 0;
                buffer[instrStart + (ulong)instrLen - 1] = 0;
            }
            else if (bitness == 32 && instr.MemoryDisplSize == 4 && instrLen >= 4)
            {
                buffer[instrStart + (ulong)instrLen - 4] = 0;
                buffer[instrStart + (ulong)instrLen - 3] = 0;
                buffer[instrStart + (ulong)instrLen - 2] = 0;
                buffer[instrStart + (ulong)instrLen - 1] = 0;
            }
        }

        return CalculateCrc32(buffer);
    }
}

public sealed class DecompilationPipeline
{
    private readonly int _bitness;
    private readonly Dictionary<ulong, string> _imports;
    private readonly Dictionary<long, string> _strings;
    private readonly Dictionary<string, VariableSymbol> _userRenames;
    private readonly Func<ulong, string>? _stringExtractor;

    public IrBlock[]? LastBlocks { get; private set; }
    public CallingConventionAnalyzer.FunctionSignature? LastSignature { get; private set; }
    public List<LoopInfo>? LastLoops { get; private set; }
    public List<StructReconstructor.RecoveredStruct>? LastStructs { get; private set; }
    public List<VTableDetector.VTableCall>? LastVTables { get; private set; }
    public StructuredNode? LastStructuredAst { get; private set; }
    public Dictionary<(Register, int), TypeInfo>? LastTypeMap { get; private set; }
    public Dictionary<long, string> UserComments { get; set; } = new();

    public Dictionary<string, HashSet<ulong>> GlobalStructs { get; set; } = new();

    public DecompilationPipeline(int bitness,
        Dictionary<ulong, string>? imports = null,
        Dictionary<long, string>? strings = null,
        Dictionary<string, VariableSymbol>? userRenames = null,
        Func<ulong, string>? stringExtractor = null)
    {
        _bitness = bitness;
        _imports = imports ?? new();
        _strings = strings ?? new();
        _userRenames = userRenames ?? new();
        _stringExtractor = stringExtractor;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe PseudocodeLine[] DecompileFunction(BasicBlock[] cfgBlocks,
        byte* fileData, long fileLength, long baseAddress, string? funcName = null, string? summary = null)
    {
        if (cfgBlocks.Length == 0) return Array.Empty<PseudocodeLine>();

        bool isLibraryFunction = false;

        long   _funcKey          = cfgBlocks.Length > 0 ? cfgBlocks[0].StartOffset : -1;
        bool   _identifiedByHash = false;

        if (fileData != null && cfgBlocks.Length > 0 &&
            cfgBlocks[0].ByteLength > 0 && _funcKey >= 0)
        {
            long offset = cfgBlocks[0].StartOffset;
            if (offset >= 0 && offset < fileLength)
            {
                int scanLen = (int)Math.Min(32, fileLength - offset);
                if (scanLen >= 16)
                {
                    uint crc = FunctionHasher.GetNormalizedCrc32(
                        fileData + offset, scanLen, _bitness);

                    if (SignatureCache.FunctionHashesLookup.TryGetValue(crc, out var crcName))
                    {
                        funcName            = crcName;
                        isLibraryFunction   = true;
                        _identifiedByHash   = true;
                        UserComments[_funcKey] = $"[!] Identified (CRC32): {crcName}";
                    }
                    else
                    {
                        UserComments[_funcKey] = $"[?] Unrecognized. CRC32: 0x{crc:X8}";
                    }
                }
            }
        }
        Func<long, int, byte[]> safeMemReader = (offset, size) =>
        {
            if (fileData == null || offset < 0 || size <= 0 || offset + size > fileLength) return Array.Empty<byte>();
            var buf = new byte[size];
            for (int i = 0; i < size; i++) buf[i] = fileData[offset + i];
            return buf;
        };

        Action<long, byte[]> safeMemWriter = (offset, data) =>
        {
            if (fileData == null || data == null || offset < 0 || offset + data.Length > fileLength) return;
            for (int i = 0; i < data.Length; i++) fileData[offset + i] = data[i];
        };

        var ctx = new DecompilerContext(
            blocks: null,
            globalRenames: _userRenames,
            globalStructs: GlobalStructs ?? new(),
            functionAddress: baseAddress,
            fileLength: fileLength,
            userComments: UserComments,
            readMemoryOffset: safeMemReader,
            writeMemoryOffset: safeMemWriter,
            log: ScriptLoader.Instance.OnColorLogMessage
        );

        ScriptLoader.Instance.RunScripts(PassStage.PreLifting, ctx);

        var lifter = new IrLifter(_bitness, _imports, _strings);
        var irBlocks = new IrBlock[cfgBlocks.Length];

        for (int i = 0; i < cfgBlocks.Length; i++)
        {
            ref var cb = ref cfgBlocks[i];
            var irBlock = new IrBlock
            {
                Index = i,
                StartAddress = cb.StartOffset,
                ByteLength = cb.ByteLength,
                IsEntry = cb.IsFirstBlock,
                IsReturn = cb.IsReturn,
            };

            if (cb.ByteLength > 0 && fileData != null)
            {
                long offset = cb.StartOffset;
                if (offset >= 0 && offset + cb.ByteLength <= fileLength)
                {
                    irBlock.Instructions = lifter.LiftRawBlock(
                        fileData + offset, cb.ByteLength, cb.StartOffset);
                }
            }

            if (cb.Successors != null)
                irBlock.Successors.AddRange(cb.Successors);

            irBlocks[i] = irBlock;
        }

        ComputePredecessors(irBlocks);
        ResolveBranchTargets(irBlocks);
        LastBlocks = irBlocks;

        RemoveCanaryChecks(irBlocks);
        CleanupIr(irBlocks);
        DominatorTree.Build(irBlocks);
        DominanceFrontier.Compute(irBlocks);
        SsaBuilder.Build(irBlocks);
        PopulateLastCmp(irBlocks);

        ctx.Blocks = irBlocks;
        ScriptLoader.Instance.RunScripts(PassStage.PreSsa, ctx);

        int maxPasses = isLibraryFunction ? 1 : 10;
        
        for (int pass = 0; pass < maxPasses; pass++)
        {
            int changes = 0;
            changes += ConstantPropagation.Propagate(irBlocks);
            changes += CopyPropagation.Propagate(irBlocks);
            changes += ExpressionSimplifier.Simplify(irBlocks);
            changes += ExpressionInliner.Inline(irBlocks);
            changes += DeadCodeElimination.Eliminate(irBlocks);
            if (changes == 0) break;
        }

        DeadCodeElimination.Compact(irBlocks);
        LivenessAnalysis.Compute(irBlocks);
        ScriptLoader.Instance.RunScripts(PassStage.PostSsa, ctx);

        MatchResult? _fingerprintMatch = null;
        if (!_identifiedByHash && _funcKey >= 0 && FingerprintIndex.Instance.RecordCount > 0)
        {
            var _fp = FingerprintExtractor.Extract(irBlocks);
            _fingerprintMatch = FingerprintIndex.Instance.FindBest(_fp);
        }
        
        LastLoops = LoopDetector.Detect(irBlocks);
        
        ScriptLoader.Instance.RunScripts(PassStage.PreTypeInference, ctx);
        LastVTables = VTableDetector.Detect(irBlocks);
        LastSignature = CallingConventionAnalyzer.AnalyzeFunction(irBlocks, funcName ?? $"sub_{baseAddress:X}");

        if (_fingerprintMatch != null)
        {
            UserComments[_funcKey] = _fingerprintMatch.FormatComment();

            if (_fingerprintMatch.IsHigh)
            {
                funcName = $"{_fingerprintMatch.Record.Lib}::{_fingerprintMatch.Record.Name}";
                isLibraryFunction = true;
                if (LastSignature != null)
                    LastSignature.Name = funcName;
            }
         
        }

        RecoverCallArguments(irBlocks);
        PopulateLastCmp(irBlocks);

        var typeMap = TypeInference.Infer(irBlocks, _imports);
        LastTypeMap = typeMap;
        TransformationPasses.Optimize(irBlocks);
        ScriptLoader.Instance.RunScripts(PassStage.PostTypeInference, ctx);
        LastStructs = StructReconstructor.Reconstruct(irBlocks);
        PostRecoveryCleanup(irBlocks);
        RemoveDeadStackStores(irBlocks);

        LastStructuredAst = ControlFlowStructurer.Structure(irBlocks, LastLoops);
        
        ctx.AstRoot = LastStructuredAst;
        ScriptLoader.Instance.RunScripts(PassStage.PostStructuring, ctx);

        IdiomRecognizer.RecognizeIdioms(LastStructuredAst);

        SemanticGuesser.GuessNames(irBlocks, _strings, _imports, _userRenames, _stringExtractor);

        var emitter = new PseudocodeEmitter(_imports, _strings, _userRenames, _stringExtractor);
        emitter.SetTypeMap(typeMap);
        emitter.SetSignature(LastSignature);
        emitter.SetStructs(LastStructs);
        emitter.SetVTables(LastVTables);
        emitter.SetSummary(summary);
        emitter.SetUserComments(UserComments);

        return emitter.Emit(LastStructuredAst, irBlocks);
    }

    public PseudocodeLine[] ReEmit(string? funcName = null, string? summary = null)
    {
        if (LastStructuredAst == null || LastBlocks == null || LastTypeMap == null) return Array.Empty<PseudocodeLine>();

        var emitter = new PseudocodeEmitter(_imports, _strings, _userRenames, _stringExtractor);
        emitter.SetTypeMap(LastTypeMap);
        emitter.SetSignature(LastSignature);
        emitter.SetStructs(LastStructs);
        emitter.SetVTables(LastVTables);
        emitter.SetSummary(summary);
        emitter.SetUserComments(UserComments);

        return emitter.Emit(LastStructuredAst, LastBlocks);
    }
    
    public void UpdateUserRenames(Dictionary<string, VariableSymbol> renames)
    {
        if (ReferenceEquals(_userRenames, renames)) return; 
        _userRenames.Clear();
        foreach (var kv in renames) _userRenames[kv.Key] = kv.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe PseudocodeLine[] DecompileBlock(byte* data, int length,
        long baseAddress, bool isFirstBlock)
    {
        if (length <= 0 || data == null) return Array.Empty<PseudocodeLine>();

        var lifter = new IrLifter(_bitness, _imports, _strings);
        var instructions = lifter.LiftRawBlock(data, length, baseAddress);

        if (instructions.Count == 0)
            return Array.Empty<PseudocodeLine>();

        var irBlock = new IrBlock
        {
            Index = 0,
            StartAddress = baseAddress,
            ByteLength = length,
            IsEntry = isFirstBlock,
            Instructions = instructions,
        };

        var blocks = new[] { irBlock };

        CleanupIr(blocks);
        OptimizeBlockLocally(irBlock);
        RecoverCallArguments(blocks);
        PopulateLastCmp(blocks);

        var emitter = new PseudocodeEmitter(_imports, _strings, _userRenames, _stringExtractor);
        return emitter.EmitBlock(irBlock);
    }

    private static void CleanupIr(IrBlock[] blocks)
    {
        for (int bi = 0; bi < blocks.Length; bi++)
        {
            var block = blocks[bi];
            var instrs = block.Instructions;
            if (instrs.Count == 0) continue;

            for (int i = 0; i < instrs.Count - 2; i++)
            {
                var load = instrs[i];
                var op = instrs[i + 1];
                var store = instrs[i + 2];

                if (load.Opcode == IrOpcode.Load &&
                    load.Destination.Kind == IrOperandKind.Register &&
                    load.Destination.Register == Register.None &&
                    op.DefinesDest &&
                    op.Destination.Kind == IrOperandKind.Register &&
                    op.Destination.Register == Register.None)
                {
                    load.IsDead = true;
                    op.IsDead = true;

                    if (op.Opcode is IrOpcode.Add or IrOpcode.Sub or IrOpcode.And
                        or IrOpcode.Or or IrOpcode.Xor or IrOpcode.Shl or IrOpcode.Shr
                        or IrOpcode.Sar)
                    {
                        var lastSrc = op.Sources.Length >= 2 ? op.Sources[1] : op.Sources[0];
                        store.Opcode = op.Opcode;
                        store.Sources = new[] { load.Sources[0], lastSrc };
                        i += 2;
                        continue;
                    }
                }
            }

            foreach (var instr in instrs)
            {
                if (instr.IsDead) continue;
                if (instr.DefinesDest &&
                    instr.Destination.Kind == IrOperandKind.Register &&
                    instr.Destination.Register == Register.None &&
                    instr.Opcode != IrOpcode.Call)
                {
                    instr.IsDead = true;
                }
            }

            if (block.IsEntry) FilterPrologue(instrs);
            if (block.IsReturn) FilterEpilogue(instrs);

            foreach (var instr in instrs)
            {
                if (instr.IsDead) continue;
                if ((instr.Opcode == IrOpcode.Sub || instr.Opcode == IrOpcode.Add) &&
                    instr.Destination.Kind == IrOperandKind.Register &&
                    IrOperand.GetCanonical(instr.Destination.Register) == Register.RSP)
                {
                    instr.IsDead = true;
                    continue;
                }
                if (instr.Opcode == IrOpcode.Assign &&
                    instr.Destination.Kind == IrOperandKind.Register &&
                    instr.Sources.Length == 1 &&
                    instr.Sources[0].Kind == IrOperandKind.Register)
                {
                    var dc = IrOperand.GetCanonical(instr.Destination.Register);
                    var sc = IrOperand.GetCanonical(instr.Sources[0].Register);
                    if ((dc == Register.RBP && sc == Register.RSP) || (dc == Register.RSP && sc == Register.RBP))
                    {
                        instr.IsDead = true;
                    }
                }
            }

            FilterPushPop(instrs);

            int firstPaddingIdx = -1;
            int paddingCount = 0;
            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.IsDead) continue;

                bool isPadding = instr.Opcode == IrOpcode.Add && instr.BitSize == 8 &&
                                 instr.Destination.Kind == IrOperandKind.Memory &&
                                 instr.Sources.Length >= 2 &&
                                 instr.Sources[1].Kind == IrOperandKind.Register &&
                                 IrOperand.GetCanonical(instr.Destination.MemBase) == Register.RAX &&
                                 IrOperand.GetCanonical(instr.Sources[1].Register) == Register.RAX;

                if (isPadding)
                {
                    if (paddingCount == 0) firstPaddingIdx = i;
                    paddingCount++;
                }
                else
                {
                    if (paddingCount > 2)
                    {
                        for (int j = firstPaddingIdx; j < i; j++)
                            if (instrs[j].Opcode == IrOpcode.Add) instrs[j].IsDead = true;
                    }
                    paddingCount = 0;
                }
            }
            if (paddingCount > 2)
            {
                for (int j = firstPaddingIdx; j < instrs.Count; j++)
                    if (instrs[j].Opcode == IrOpcode.Add) instrs[j].IsDead = true;
            }

            if (block.Successors.Count == 2 && block.Successors[0] == block.Successors[1])
            {
                block.Successors.RemoveAt(1);
                var condBranch = instrs.LastOrDefault(i => i.Opcode == IrOpcode.CondBranch && !i.IsDead);
                if (condBranch != null)
                {
                    condBranch.Opcode = IrOpcode.Branch;
                    condBranch.Sources = new[] { condBranch.Sources[0] };
                    condBranch.Condition = IrCondition.None;
                }
            }
        }
    }

    private static void FilterPrologue(List<IrInstruction> instrs)
    {
        for (int i = 0; i < Math.Min(instrs.Count, 8); i++)
        {
            var instr = instrs[i];
            if (instr.IsDead) continue;

            switch (instr.Opcode)
            {
                case IrOpcode.Store when IsStackPtr(instr.Destination):
                    instr.IsDead = true;
                    break;
                case IrOpcode.Sub when instr.Destination.Kind == IrOperandKind.Register
                    && IrOperand.GetCanonical(instr.Destination.Register) == Register.RSP:
                    instr.IsDead = true;
                    break;
                case IrOpcode.Assign when instr.Destination.Kind == IrOperandKind.Register:
                {
                    var dc = IrOperand.GetCanonical(instr.Destination.Register);
                    if (dc == Register.RBP || dc == Register.RSP) instr.IsDead = true;
                    break;
                }
                default:
                    if (instr.Opcode != IrOpcode.Nop) return; 
                    break;
            }
        }
    }

    private static void FilterEpilogue(List<IrInstruction> instrs)
    {
        for (int i = instrs.Count - 1; i >= Math.Max(0, instrs.Count - 8); i--)
        {
            var instr = instrs[i];
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Return) continue;

            switch (instr.Opcode)
            {
                case IrOpcode.Load when instr.Destination.Kind == IrOperandKind.Register:
                {
                    var dc = IrOperand.GetCanonical(instr.Destination.Register);
                    if (dc == Register.RBP || IsCalleeSaved(dc)) instr.IsDead = true;
                    break;
                }
                case IrOpcode.Add when instr.Destination.Kind == IrOperandKind.Register
                    && IrOperand.GetCanonical(instr.Destination.Register) == Register.RSP:
                    instr.IsDead = true;
                    break;
                case IrOpcode.Assign when instr.Destination.Kind == IrOperandKind.Register:
                {
                    var dc = IrOperand.GetCanonical(instr.Destination.Register);
                    if (dc == Register.RSP || dc == Register.RBP) instr.IsDead = true;
                    break;
                }
            }
        }
    }

    private static void FilterPushPop(List<IrInstruction> instrs)
    {
        foreach (var instr in instrs)
        {
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Store && IsStackPtr(instr.Destination) &&
                instr.Sources.Length > 0 && instr.Sources[0].Kind == IrOperandKind.Register)
            {
                var sc = IrOperand.GetCanonical(instr.Sources[0].Register);
                if (IsCalleeSaved(sc) || sc == Register.RBP) instr.IsDead = true;
            }
            if (instr.Opcode == IrOpcode.Load && instr.Destination.Kind == IrOperandKind.Register)
            {
                var dc = IrOperand.GetCanonical(instr.Destination.Register);
                if (IsCalleeSaved(dc) || dc == Register.RBP)
                {
                    if (instr.Sources.Length > 0 && IsStackPtr(instr.Sources[0]))
                        instr.IsDead = true;
                }
            }
        }
    }

    private static void RemoveDeadStackStores(IrBlock[] blocks)
    {
        var readOffsets = new HashSet<long>();
        bool readsUnknownStack = false;

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.Opcode == IrOpcode.Load && instr.Sources.Length > 0 && instr.Sources[0].Kind == IrOperandKind.Memory)
                {
                    var canon = IrOperand.GetCanonical(instr.Sources[0].MemBase);
                    if (canon == Register.RSP || canon == Register.RBP)
                    {
                        readOffsets.Add(instr.Sources[0].MemIndex == Register.None ? instr.Sources[0].MemDisplacement : long.MinValue);
                    }
                }
                else if (instr.Opcode == IrOpcode.Call)
                {
                    readsUnknownStack = true;
                }
            }
        }

        if (readsUnknownStack) return; 

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.Opcode == IrOpcode.Store && instr.Destination.Kind == IrOperandKind.Memory)
                {
                    var canon = IrOperand.GetCanonical(instr.Destination.MemBase);
                    if (canon == Register.RSP || canon == Register.RBP)
                    {
                        long offset = instr.Destination.MemIndex == Register.None ? instr.Destination.MemDisplacement : long.MinValue;
                        if (!readOffsets.Contains(offset))
                        {
                            instr.IsDead = true;
                        }
                    }
                }
            }
        }
    }

    private static void OptimizeBlockLocally(IrBlock block)
    {
        var instrs = block.Instructions;
        var regConstants = new Dictionary<Register, long>();
        foreach (var instr in instrs)
        {
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Assign && instr.Destination.Kind == IrOperandKind.Register &&
                instr.Sources.Length == 1 && instr.Sources[0].Kind == IrOperandKind.Constant &&
                instr.Condition == IrCondition.None)
            {
                var canon = IrOperand.GetCanonical(instr.Destination.Register);
                if (canon != Register.None) regConstants[canon] = instr.Sources[0].ConstantValue;
            }
            else if (instr.DefinesDest && instr.Destination.Kind == IrOperandKind.Register)
            {
                var canon = IrOperand.GetCanonical(instr.Destination.Register);
                regConstants.Remove(canon);
            }
            for (int j = 0; j < instr.Sources.Length; j++)
            {
                if (instr.Sources[j].Kind == IrOperandKind.Register)
                {
                    var canon = IrOperand.GetCanonical(instr.Sources[j].Register);
                    if (regConstants.TryGetValue(canon, out long val))
                        instr.Sources[j] = IrOperand.Const(val, instr.Sources[j].BitSize);
                }
            }
        }

        var regCopies = new Dictionary<Register, IrOperand>();
        foreach (var instr in instrs)
        {
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Assign && instr.Destination.Kind == IrOperandKind.Register &&
                instr.Sources.Length == 1 && instr.Condition == IrCondition.None)
            {
                var canon = IrOperand.GetCanonical(instr.Destination.Register);
                if (canon != Register.None) regCopies[canon] = instr.Sources[0];
            }
            else if (instr.DefinesDest && instr.Destination.Kind == IrOperandKind.Register)
            {
                var canon = IrOperand.GetCanonical(instr.Destination.Register);
                regCopies.Remove(canon);
            }
        }

        foreach (var instr in instrs)
        {
            if (instr.IsDead) continue;
            if (instr.Opcode == IrOpcode.Assign && instr.Destination.Kind == IrOperandKind.Register &&
                instr.Sources.Length == 1 && instr.Sources[0].Kind == IrOperandKind.Register &&
                instr.Condition == IrCondition.None)
            {
                if (IrOperand.GetCanonical(instr.Destination.Register) == IrOperand.GetCanonical(instr.Sources[0].Register))
                {
                    instr.IsDead = true;
                }
            }
        }

        foreach (var instr in instrs)
        {
            if (instr.IsDead) continue;
            if (instr.Sources.Length < 2) continue;
            if ((instr.Opcode == IrOpcode.Add || instr.Opcode == IrOpcode.Sub) &&
                instr.Sources[1].Kind == IrOperandKind.Constant && instr.Sources[1].ConstantValue == 0)
            {
                if (instr.Destination.SameLocation(instr.Sources[0])) instr.IsDead = true;
            }
            if (instr.Opcode == IrOpcode.And && instr.Sources[1].Kind == IrOperandKind.Constant &&
                instr.Sources[1].ConstantValue == -1)
            {
                if (instr.Destination.SameLocation(instr.Sources[0])) instr.IsDead = true;
            }
        }
    }

    private static void ComputePredecessors(IrBlock[] blocks)
    {
        foreach (var block in blocks) block.Predecessors.Clear();
        for (int i = 0; i < blocks.Length; i++)
        {
            foreach (int s in blocks[i].Successors)
            {
                if (s >= 0 && s < blocks.Length) blocks[s].Predecessors.Add(i);
            }
        }
    }

    private static void ResolveBranchTargets(IrBlock[] blocks)
    {
        var addrToBlock = new Dictionary<long, int>();
        for (int i = 0; i < blocks.Length; i++) addrToBlock[blocks[i].StartAddress] = i;

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode == IrOpcode.Branch || instr.Opcode == IrOpcode.CondBranch)
                {
                    for (int i = 0; i < instr.Sources.Length; i++)
                    {
                        if (instr.Sources[i].Kind == IrOperandKind.Constant)
                        {
                            long addr = instr.Sources[i].ConstantValue;
                            if (addrToBlock.TryGetValue(addr, out int blockIdx))
                                instr.Sources[i] = IrOperand.BlockLabel(blockIdx);
                        }
                    }
                }
            }
        }
    }

    private void RemoveCanaryChecks(IrBlock[] blocks)
    {
        var trapBlocks = new HashSet<int>();
        for (int i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            if (block.Instructions.Count > 5) continue;
            bool hasTrap = false;
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode == IrOpcode.Call && instr.Sources.Length > 0 && instr.Sources[0].Kind == IrOperandKind.Constant)
                {
                    long target = instr.Sources[0].ConstantValue;
                    if (_imports != null && _imports.TryGetValue((ulong)target, out var name))
                    {
                        if (name.Contains("cookie") || name.Contains("stack_chk_fail") || name.Contains("report_gsfailure"))
                        {
                            hasTrap = true;
                            break;
                        }
                    }
                }
            }
            if (hasTrap) trapBlocks.Add(i);
        }

        foreach (var block in blocks)
        {
            if (block.Successors.Count == 2)
            {
                int s0 = block.Successors[0];
                int s1 = block.Successors[1];
                bool t0 = trapBlocks.Contains(s0);
                bool t1 = trapBlocks.Contains(s1);

                if (t0 || t1)
                {
                    int safeIdx = t0 ? s1 : s0;
                    block.Successors.Clear();
                    block.Successors.Add(safeIdx);

                    for (int i = block.Instructions.Count - 1; i >= 0; i--)
                    {
                        var instr = block.Instructions[i];
                        if (instr.Opcode == IrOpcode.CondBranch && !instr.IsDead)
                        {
                            instr.Opcode = IrOpcode.Branch;
                            instr.Sources = new[] { IrOperand.BlockLabel(safeIdx) };
                            instr.Condition = IrCondition.None;
                            break;
                        }
                    }
                }
            }
        }
    }

    private void PopulateLastCmp(IrBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            block.LastCmpInstr = FindLastFlagSetter(blocks, block);
            foreach (var instr in block.Instructions)
            {
                if (instr.Condition != IrCondition.None)
                {
                    instr.ConditionInstr = FindLastFlagSetter(blocks, block, instr);
                }
            }
        }
    }

    public static IrInstruction? FindLastFlagSetter(IrBlock[] blocks, IrBlock startBlock, IrInstruction? startInstr = null)
    {
        int startIndex = startBlock.Instructions.Count - 1;
        if (startInstr != null)
        {
            int idx = startBlock.Instructions.IndexOf(startInstr);
            startIndex = idx >= 0 ? idx - 1 : startIndex;
        }

        for (int i = startIndex; i >= 0; i--)
        {
            var prev = startBlock.Instructions[i];
            if (prev.IsDead) continue;
            if (IsFlagSetter(prev.Opcode)) return prev;
            if (prev.Opcode == IrOpcode.Call) break;
        }

        var visited = new HashSet<int>();
        var queue = new Queue<(IrBlock Block, int Depth)>();
        queue.Enqueue((startBlock, 0));
        visited.Add(startBlock.Index);

        while (queue.Count > 0)
        {
            var (curr, depth) = queue.Dequeue();
            if (depth > 5) continue;

            int scanStart = curr == startBlock ? startIndex : curr.Instructions.Count - 1;

            for (int i = scanStart; i >= 0; i--)
            {
                var prev = curr.Instructions[i];
                if (prev.IsDead) continue;
                if (IsFlagSetter(prev.Opcode)) return prev;
                if (prev.Opcode == IrOpcode.Call) break; 
            }

            foreach (var predIdx in curr.Predecessors)
            {
                if (visited.Add(predIdx))
                    queue.Enqueue((blocks[predIdx], depth + 1));
            }
        }
        return null;
    }

    private static bool IsFlagSetter(IrOpcode opcode)
    {
        return opcode is IrOpcode.Cmp or IrOpcode.Test or IrOpcode.Add or IrOpcode.Sub 
            or IrOpcode.And or IrOpcode.Or or IrOpcode.Xor or IrOpcode.Shl or IrOpcode.Shr or IrOpcode.Sar;
    }

    private void RecoverCallArguments(IrBlock[] blocks)
    {
        for (int bi = 0; bi < blocks.Length; bi++)
        {
            var block = blocks[bi];
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (instr.Opcode != IrOpcode.Call) continue;

                var args = CallingConventionAnalyzer.RecoverCallArguments(blocks, bi, i, _bitness);
                if (args.Length > 0)
                {
                    var newSources = new IrOperand[1 + args.Length];
                    newSources[0] = instr.Sources[0];
                    for (int j = 0; j < args.Length; j++)
                    {
                        var arg = args[j];
                        if (arg.SourceOperand != null)
                            newSources[j + 1] = arg.SourceOperand.Value;
                        else if (arg.SourceRegister != Register.None)
                            newSources[j + 1] = IrOperand.Reg(arg.SourceRegister, (byte)_bitness);
                        else
                            newSources[j + 1] = IrOperand.Stack(arg.StackOffset, (byte)_bitness);
                        
                        newSources[j + 1].Name = arg.Name;
                        newSources[j + 1].Type = arg.Type;
                    }
                    instr.Sources = newSources;
                }
            }
        }
    }

    private void PostRecoveryCleanup(IrBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            var regCopies = new Dictionary<Register, IrInstruction>();

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (instr.IsDead) continue;

                if (instr.Opcode == IrOpcode.Assign && instr.Destination.Kind == IrOperandKind.Register &&
                    instr.Sources.Length == 1 && instr.Condition == IrCondition.None)
                {
                    var canon = IrOperand.GetCanonical(instr.Destination.Register);
                    if (canon != Register.None) regCopies[canon] = instr;
                }
                else if (instr.DefinesDest && instr.Destination.Kind == IrOperandKind.Register)
                {
                    var canon = IrOperand.GetCanonical(instr.Destination.Register);
                    regCopies.Remove(canon);
                }

                if (instr.Opcode == IrOpcode.Call && instr.Sources.Length > 1)
                {
                    for (int j = 1; j < instr.Sources.Length; j++)
                    {
                        if (instr.Sources[j].Kind == IrOperandKind.Register)
                        {
                            var canon = IrOperand.GetCanonical(instr.Sources[j].Register);
                            if (regCopies.TryGetValue(canon, out var copyInstr))
                            {
                                instr.Sources[j] = copyInstr.Sources[0]; 
                                copyInstr.IsDead = true; 
                            }
                        }
                    }
                }
            }
        }
        DeadCodeElimination.Compact(blocks);
    }

    private static bool IsStackPtr(in IrOperand op)
    {
        if (op.Kind == IrOperandKind.Memory) return IrOperand.GetCanonical(op.MemBase) == Register.RSP;
        return op.Kind == IrOperandKind.Register && IrOperand.GetCanonical(op.Register) == Register.RSP;
    }

    private static bool IsCalleeSaved(Register canonical) =>
        canonical is Register.RBX or Register.R12 or Register.R13 or Register.R14 or Register.R15 or Register.RSI or Register.RDI;
}