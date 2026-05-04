// SPDX-License-Identifier: GPL-3.0-or-later

namespace EUVA.Core.Disassembly.Analysis;

public static class StructReconstructor
{
    
    public sealed class RecoveredStruct
    {
        public string Name;
        public SortedDictionary<long, RecoveredField> Fields = new();
        public int AccessCount;

        public RecoveredStruct(string name) => Name = name;
    }

    public sealed class RecoveredField
    {
        public long Offset;
        public string Name;
        public TypeInfo Type;
        public byte BitSize;

        public RecoveredField(long offset, string name, byte bitSize)
        {
            Offset = offset;
            Name = name;
            BitSize = bitSize;
            Type = TypeInfo.Unknown;
        }
    }

    public static List<RecoveredStruct> Reconstruct(IrBlock[] blocks)
    {
        var accessByBase = new Dictionary<string, Dictionary<long, (byte BitSize, int Count)>>();

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;

                ScanOperand(instr.Destination, accessByBase);
                foreach (ref var src in instr.Sources.AsSpan())
                    ScanOperand(src, accessByBase);
            }
        }

        var patterns = new Dictionary<string, List<string>>(); 
        foreach (var (baseKey, offsets) in accessByBase)
        {
            if (offsets.Count < 2) continue; 

            var sortedOffsets = offsets.Keys.OrderBy(k => k).ToList();
            var pattern = string.Join(",", sortedOffsets);
            
            if (!patterns.TryGetValue(pattern, out var bases))
            {
                bases = new();
                patterns[pattern] = bases;
            }
            bases.Add(baseKey);
        }

        var structs = new List<RecoveredStruct>();
        int structId = 1;

        foreach (var (pattern, bases) in patterns)
        {
            string bestBase = bases.OrderByDescending(b => b.Contains("rcx") || b.Contains("rdi")).First();
            string name = bestBase.Contains("rcx") || bestBase.Contains("rdi") 
                ? $"this_type_{structId++}" 
                : $"struct_{structId++}";

            var st = new RecoveredStruct(name);
            var firstBaseOffsets = accessByBase[bases[0]];
            
            foreach (var offset in firstBaseOffsets.Keys.OrderBy(k => k))
            {
                var (bitSize, _) = firstBaseOffsets[offset];
                string fieldName = offset < 0 ? $"m_prev_{Math.Abs(offset):X}" : $"field_{offset:X}";
                var field = new RecoveredField(offset, fieldName, bitSize);
                
                foreach (var otherBase in bases)
                {
                    if (accessByBase[otherBase].TryGetValue(offset, out var otherAccess))
                        st.AccessCount += otherAccess.Count;
                }

                field.Type = bitSize switch
                {
                    8 => new TypeInfo { BaseType = PrimitiveType.UInt8 },
                    16 => new TypeInfo { BaseType = PrimitiveType.UInt16 },
                    32 => new TypeInfo { BaseType = PrimitiveType.UInt32 },
                    64 => new TypeInfo { BaseType = PrimitiveType.UInt64 },
                    _ => TypeInfo.Unknown,
                };
                st.Fields[offset] = field;
            }
            structs.Add(st);

            foreach (var block in blocks)
            {
                foreach (var instr in block.Instructions)
                {
                    UpdateOperandStructType(instr.Destination, bases, st.Name, blocks);
                    foreach (ref var src in instr.Sources.AsSpan())
                        UpdateOperandStructType(src, bases, st.Name, blocks);
                }
            }
        }

        return structs;
    }

    private static void UpdateOperandStructType(IrOperand op, List<string> matchingBases, string structName, IrBlock[] blocks)
    {
        if (op.Kind != IrOperandKind.Memory || op.MemBase == Iced.Intel.Register.None) return;
        
        var canonical = IrOperand.GetCanonical(op.MemBase);
        var baseKey = op.SsaVersion >= 0 ? $"r_{canonical}_{op.SsaVersion}" : $"r_{canonical}";
        
        if (matchingBases.Contains(baseKey))
        {
            foreach (var block in blocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr.DefinesDest && instr.Destination.Kind == IrOperandKind.Register &&
                        IrOperand.GetCanonical(instr.Destination.Register) == canonical &&
                        instr.Destination.SsaVersion == op.SsaVersion)
                    {
                        instr.Destination.Type = new TypeInfo 
                        { 
                            BaseType = PrimitiveType.Struct, 
                            PointerLevel = 1,
                            TypeName = structName
                        };
                    }
                }
            }
        }
    }

    private static void ScanOperand(in IrOperand op,
        Dictionary<string, Dictionary<long, (byte BitSize, int Count)>> access)
    {
        if (op.Kind != IrOperandKind.Memory) return;
        if (op.MemBase == Iced.Intel.Register.None) return;
        if (op.MemIndex != Iced.Intel.Register.None) return; 
        if (op.MemDisplacement == 0) return; 

        var canonical = IrOperand.GetCanonical(op.MemBase);
        if (canonical == Iced.Intel.Register.RSP ||
            canonical == Iced.Intel.Register.RBP ||
            canonical == Iced.Intel.Register.RIP || 
            canonical == Iced.Intel.Register.EIP)
            return; 

        if (op.MemDisplacement > 0x10000 || op.MemDisplacement < -0x10000)
            return;

        var baseKey = op.SsaVersion >= 0 ? $"r_{canonical}_{op.SsaVersion}" : $"r_{canonical}";
        if (!access.TryGetValue(baseKey, out var offsets))
        {
            offsets = new();
            access[baseKey] = offsets;
        }

        if (offsets.TryGetValue(op.MemDisplacement, out var existing))
            offsets[op.MemDisplacement] = (existing.BitSize, existing.Count + 1);
        else
            offsets[op.MemDisplacement] = (op.BitSize, 1);
    }
}
