// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using EUVA.Core.Disassembly.Analysis;

namespace EUVA.Core.Disassembly;

public sealed class PseudocodeGenerator
{
    
    private int _bitness = 64;
    private readonly Dictionary<ulong, string> _imports = new();
    private readonly Dictionary<long, string> _strings = new();
    private readonly List<(long Start, long End)> _dataSections = new();
    private Dictionary<string, VariableSymbol>? _globalRenames;
    private Dictionary<string, HashSet<ulong>>? _globalStructs;
    private string _currentFuncName = "sub_0";
    public string? AiFunctionSummary { get; set; }
    private Func<ulong, string>? _stringExtractor;

    
    private DecompilationPipeline? _pipeline;

    
    public void SetImports(Dictionary<ulong, string> imports)
    {
        _imports.Clear();
        foreach (var kv in imports)
            _imports[kv.Key] = kv.Value;
        _pipeline = null; 
    }

    
    public void SetStrings(Dictionary<long, string> strings)
    {
        _strings.Clear();
        foreach (var kv in strings)
            _strings[kv.Key] = kv.Value;
        _pipeline = null;
    }

    
    public void SetDataSections(List<(long Start, long End)> sections)
    {
        _dataSections.Clear();
        _dataSections.AddRange(sections);
    }

    
    public void SetCurrentFunction(string funcName) => _currentFuncName = funcName;

    public void SetGlobalRenames(Dictionary<string, VariableSymbol> globalRenames)
    {
        _globalRenames = globalRenames;
        _pipeline = null;
    }

    public void SetGlobalContext(Dictionary<string, VariableSymbol> renames, Dictionary<string, HashSet<ulong>> structs)
    {
        _globalRenames = renames;
        _globalStructs = structs;
        _pipeline = null;
    }

    public void SetStringExtractor(Func<ulong, string> extractor)
    {
        _stringExtractor = extractor;
        _pipeline = null;
    }

    
    public IReadOnlyDictionary<string, VariableSymbol>? UserRenames => _globalRenames;

    public event EventHandler<VariableSymbol>? RenameApplied;

    public void ApplyRename(string oldName, string newName, bool isAiGenerated = false)
    {
        _globalRenames ??= new();
        var sym = new VariableSymbol(oldName, newName, isAiGenerated);
        _globalRenames[oldName] = sym;
        _pipeline?.UpdateUserRenames(_globalRenames);
        RenameApplied?.Invoke(this, sym);
    }

    public void ClearAiRenames()
    {
        AiFunctionSummary = null;
        if (_globalRenames == null) return;
        var toRemove = _globalRenames.Where(kv => kv.Value.IsAiGenerated).Select(kv => kv.Key).ToList();
        foreach (var k in toRemove) _globalRenames.Remove(k);
        _pipeline = null;
    }

    public bool TryGetGlobalRename(string name, out VariableSymbol renamed)
    {
        if (_globalRenames != null && _globalRenames.TryGetValue(name, out renamed)) return true;
        renamed = new VariableSymbol(name, name);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe PseudocodeLine[] Generate(byte* data, int dataLength, long baseAddress,
        int bitness, bool isFirstBlock, byte* fullFileData = null, long fullFileLength = 0)
    {
        _bitness = bitness;
        if (dataLength <= 0) return Array.Empty<PseudocodeLine>();

        EnsurePipeline();

        return _pipeline!.DecompileBlock(data, dataLength, baseAddress, isFirstBlock);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe PseudocodeLine[] DecompileFunction(BasicBlock[]? blocks,
        byte* fileData, long fileLength, long baseAddress, ExecutableRange[]? executableSections = null)
    {
        if (_pipeline != null && _pipeline.LastStructuredAst != null)
        {
            return _pipeline.ReEmit(_currentFuncName, AiFunctionSummary);
        }

        if (blocks == null) return Array.Empty<PseudocodeLine>();

        EnsurePipeline();
        return _pipeline!.DecompileFunction(blocks, fileData, fileLength, baseAddress, _currentFuncName, AiFunctionSummary, executableSections);
    }

    public IrBlock[]? LastBlocks => _pipeline?.LastBlocks;

    
    public DecompilationPipeline? Pipeline => _pipeline;

    public Dictionary<ulong, string> ResolvedImports
    {
        get => _imports;
        set
        {
            _imports.Clear();
            foreach (var kv in value)
                _imports[kv.Key] = kv.Value;
            _pipeline = null;
        }
    }

    public Dictionary<long, string> ResolvedStrings
    {
        get => _strings;
        set
        {
            _strings.Clear();
            foreach (var kv in value)
                _strings[kv.Key] = kv.Value;
            _pipeline = null;
        }
    }

    
    
    private readonly Dictionary<(int, int), string> _userComments = new();
    private readonly Dictionary<long, string> _addrComments = new();

    public string? GetUserComment(int blockIdx, int lineIdx)
    {
        _userComments.TryGetValue((blockIdx, lineIdx), out var c);
        return c;
    }

    public string? GetCommentByAddress(long addr)
    {
        _addrComments.TryGetValue(addr, out var c);
        return c;
    }

    public void SetUserComment(int blockIdx, int lineIdx, string? comment)
    {
        if (comment == null) _userComments.Remove((blockIdx, lineIdx));
        else _userComments[(blockIdx, lineIdx)] = comment;
    }

    public void SetCommentByAddress(long addr, string? comment)
    {
        if (comment == null) _addrComments.Remove(addr);
        else _addrComments[addr] = comment;
        
        if (_pipeline != null)
            _pipeline.UserComments[addr] = comment ?? "";
    }

    
    public struct ClassContext
    {
        public double Confidence;
        public string? VarName;
        public HashSet<ulong>? Fields;
    }

    
    public ClassContext? GetPrimaryClassContext()
    {
        if (_pipeline?.LastBlocks == null || _pipeline.LastBlocks.Length == 0) return null;

        var structs = _pipeline.LastStructs;
        if (structs == null || structs.Count == 0) return null;

        foreach (var st in structs)
        {
            if (st.Name.Contains("RCX") && st.Fields.Count >= 2)
            {
                var fields = new HashSet<ulong>();
                foreach (var kv in st.Fields) fields.Add((ulong)(long)kv.Key);
                return new ClassContext
                {
                    Confidence = Math.Min(1.0, st.AccessCount * 0.15),
                    VarName = "this",
                    Fields = fields,
                };
            }
        }
        return null;
    }

    private void EnsurePipeline()
    {
        if (_pipeline == null)
        {
            _pipeline = new DecompilationPipeline(
                _bitness, _imports, _strings, _globalRenames, _stringExtractor);
            
            foreach (var kv in _addrComments)
                _pipeline.UserComments[kv.Key] = kv.Value;
        }
    }
}