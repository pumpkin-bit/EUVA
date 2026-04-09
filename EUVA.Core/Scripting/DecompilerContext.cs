// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using EUVA.Core.Disassembly;
using EUVA.Core.Disassembly.Analysis;

namespace EUVA.Core.Scripting;

public sealed class DecompilerContext
{
    public IrBlock[]? Blocks { get; set; }
    public StructuredNode? AstRoot { get; set; }
    public Dictionary<string, VariableSymbol> GlobalRenames { get; }
    public Dictionary<string, HashSet<ulong>> GlobalStructs { get; }
    public Dictionary<long, string>? UserComments { get; }
    public PseudocodeEmitter? Emitter { get; }
    
    public long FunctionAddress { get; }
    public long FileLength { get; }
    public Func<long, int, byte[]>? ReadMemoryOffset { get; }
    public Action<long, byte[]>? WriteMemoryOffset { get; }
    public Action<string, string>? Log { get; }
    
    public DecompilerContext(
        IrBlock[]? blocks,
        Dictionary<string, VariableSymbol> globalRenames,
        Dictionary<string, HashSet<ulong>> globalStructs,
        long functionAddress,
        long fileLength = 0,
        Dictionary<long, string>? userComments = null,
        Func<long, int, byte[]>? readMemoryOffset = null,
        Action<long, byte[]>? writeMemoryOffset = null,
        Action<string, string>? log = null,
        PseudocodeEmitter? emitter = null,
        StructuredNode? astRoot = null)
    {
        Blocks = blocks;
        GlobalRenames = globalRenames;
        GlobalStructs = globalStructs;
        FunctionAddress = functionAddress;
        FileLength = fileLength;
        UserComments = userComments;
        ReadMemoryOffset = readMemoryOffset;
        WriteMemoryOffset = writeMemoryOffset;
        Log = log;
        Emitter = emitter;
        AstRoot = astRoot;
    }
}
