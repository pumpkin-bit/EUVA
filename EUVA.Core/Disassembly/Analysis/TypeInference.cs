// SPDX-License-Identifier: GPL-3.0-or-later

using Iced.Intel;

namespace EUVA.Core.Disassembly.Analysis;

public static class TypeInference
{
    
    public static int Infer(IrBlock[] blocks)
    {
        int inferred = 0;
        var worklist = new Queue<IrInstruction>();
        var inWorklist = new HashSet<IrInstruction>();
        var defMap = new Dictionary<(string, int), IrInstruction>();
        var useMap = new Dictionary<(string, int), List<IrInstruction>>();

        
        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;

                
                if (instr.DefinesDest)
                {
                    var key = SsaBuilder.GetVarKey(in instr.Destination);
                    if (key != null && instr.Destination.SsaVersion >= 0)
                        defMap[(key, instr.Destination.SsaVersion)] = instr;
                }

                
                foreach (var src in instr.Sources)
                {
                    var key = SsaBuilder.GetVarKey(in src);
                    if (key != null && src.SsaVersion >= 0)
                    {
                        var tuple = (key, src.SsaVersion);
                        if (!useMap.TryGetValue(tuple, out var uses))
                        {
                            uses = new List<IrInstruction>();
                            useMap[tuple] = uses;
                        }
                        uses.Add(instr);
                    }
                }

                
                if (InferFromInstruction(instr) > 0)
                {
                    inferred++;
                    EnqueueUses(instr.Destination, useMap, worklist, inWorklist);
                }
            }
        }

        foreach (var block in blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.IsDead) continue;
                if (instr.Opcode == IrOpcode.Assign || instr.Opcode == IrOpcode.Phi)
                {
                    if (inWorklist.Add(instr))
                        worklist.Enqueue(instr);
                }
            }
        }

        while (worklist.Count > 0)
        {
            var instr = worklist.Dequeue();
            inWorklist.Remove(instr);

            bool changed = false;

            
            if (instr.DefinesDest && instr.Opcode == IrOpcode.Assign && instr.Sources.Length == 1)
            {
                if (instr.Destination.Type == TypeInfo.Unknown && instr.Sources[0].Type != TypeInfo.Unknown)
                {
                    instr.Destination.Type = instr.Sources[0].Type;
                    inferred++;
                    changed = true;
                }
                else if (instr.Destination.Type == TypeInfo.Unknown)
                {
                    var srcKey = SsaBuilder.GetVarKey(in instr.Sources[0]);
                    if (srcKey != null && defMap.TryGetValue((srcKey, instr.Sources[0].SsaVersion), out var srcDef) &&
                        srcDef.Destination.Type != TypeInfo.Unknown)
                    {
                        instr.Destination.Type = srcDef.Destination.Type;
                        inferred++;
                        changed = true;
                    }
                }

                
                if (instr.Destination.Type != TypeInfo.Unknown && instr.Sources[0].Type == TypeInfo.Unknown)
                {
                    var srcKey = SsaBuilder.GetVarKey(in instr.Sources[0]);
                    if (srcKey != null && defMap.TryGetValue((srcKey, instr.Sources[0].SsaVersion), out var srcDef) &&
                        srcDef.Destination.Type == TypeInfo.Unknown)
                    {
                        srcDef.Destination.Type = instr.Destination.Type;
                        inferred++;
                        
                        if (inWorklist.Add(srcDef)) worklist.Enqueue(srcDef);
                        EnqueueUses(srcDef.Destination, useMap, worklist, inWorklist);
                    }
                }
            }

            
            if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown && instr.Opcode == IrOpcode.Phi)
            {
                var inferredType = InferFromSources(instr, defMap);
                if (inferredType != TypeInfo.Unknown)
                {
                    instr.Destination.Type = inferredType;
                    inferred++;
                    changed = true;
                }
            }

            if (changed)
            {
                
                EnqueueUses(instr.Destination, useMap, worklist, inWorklist);
            }
        }

        return inferred;
    }

    private static void EnqueueUses(IrOperand operand, Dictionary<(string, int), List<IrInstruction>> useMap,
        Queue<IrInstruction> worklist, HashSet<IrInstruction> inWorklist)
    {
        var key = SsaBuilder.GetVarKey(in operand);
        if (key != null && operand.SsaVersion >= 0)
        {
            if (useMap.TryGetValue((key, operand.SsaVersion), out var uses))
            {
                foreach (var use in uses)
                {
                    if (inWorklist.Add(use))
                        worklist.Enqueue(use);
                }
            }
        }
    }

    private static int InferFromInstruction(IrInstruction instr)
    {
        int count = 0;

        switch (instr.Opcode)
        {
            
            case IrOpcode.Add or IrOpcode.Sub:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    
                    bool isPointerMath = false;
                    foreach (var src in instr.Sources)
                    {
                        if (src.Type != TypeInfo.Unknown && src.Type.PointerLevel > 0)
                        {
                            instr.Destination.Type = src.Type;
                            isPointerMath = true;
                            count++;
                            break;
                        }
                    }

                    if (!isPointerMath)
                    {
                        instr.Destination.Type = InferNumericType(instr.BitSize, signed: false);
                        count++;
                    }
                }
                break;

            case IrOpcode.Mul or IrOpcode.Div or IrOpcode.Mod or IrOpcode.Neg:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    instr.Destination.Type = InferNumericType(instr.BitSize, signed: false);
                    count++;
                }
                break;

            case IrOpcode.IMul or IrOpcode.IDiv:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    instr.Destination.Type = InferNumericType(instr.BitSize, signed: true);
                    count++;
                }
                break;

            
            case IrOpcode.And or IrOpcode.Or or IrOpcode.Xor or IrOpcode.Not
                or IrOpcode.Shl or IrOpcode.Shr or IrOpcode.Sar:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    instr.Destination.Type = InferNumericType(instr.BitSize,
                        signed: instr.Opcode == IrOpcode.Sar);
                    count++;
                }
                break;

            
            case IrOpcode.Load:
                
                if (instr.Sources.Length > 0 && instr.Sources[0].Type == TypeInfo.Unknown)
                {
                    var destType = InferNumericType(instr.BitSize, signed: false);
                    instr.Sources[0].Type = new TypeInfo { BaseType = destType.BaseType != PrimitiveType.Unknown ? destType.BaseType : PrimitiveType.Void, PointerLevel = 1 };
                    count++;
                }
                break;

            case IrOpcode.Store:
                
                if (instr.Destination.Type == TypeInfo.Unknown && instr.Sources.Length > 0)
                {
                    var src = instr.Sources[0];
                    if (src.Type != TypeInfo.Unknown)
                    {
                        instr.Destination.Type = new TypeInfo { BaseType = src.Type.BaseType, PointerLevel = (byte)(src.Type.PointerLevel + 1) };
                        count++;
                    }
                    else
                    {
                        var srcType = InferNumericType(src.BitSize, signed: false);
                        instr.Destination.Type = new TypeInfo { BaseType = srcType.BaseType != PrimitiveType.Unknown ? srcType.BaseType : PrimitiveType.Void, PointerLevel = 1 };
                        count++;
                    }
                }
                break;

            
            case IrOpcode.Call:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    
                    instr.Destination.Type = instr.BitSize >= 64
                        ? new TypeInfo { BaseType = PrimitiveType.Void, PointerLevel = 1 } 
                        : new TypeInfo { BaseType = PrimitiveType.Int32, PointerLevel = 0 };
                    count++;
                }
                break;
                
            case IrOpcode.ZeroExtend:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    instr.Destination.Type = InferNumericType(instr.BitSize, signed: false);
                    count++;
                }
                break;

            case IrOpcode.SignExtend:
                if (instr.DefinesDest && instr.Destination.Type == TypeInfo.Unknown)
                {
                    instr.Destination.Type = InferNumericType(instr.BitSize, signed: true);
                    count++;
                }
                break;
        }

        return count;
    }

    private static TypeInfo InferFromSources(IrInstruction instr,
        Dictionary<(string, int), IrInstruction> defMap)
    {
        if (instr.Opcode == IrOpcode.Assign && instr.Sources.Length == 1)
        {
            
            var src = instr.Sources[0];
            if (src.Type != TypeInfo.Unknown) return src.Type;

            var key = SsaBuilder.GetVarKey(in src);
            if (key != null && src.SsaVersion >= 0 && defMap.TryGetValue((key, src.SsaVersion), out var def))
            {
                if (def.Destination.Type != TypeInfo.Unknown)
                    return def.Destination.Type;
            }
        }

        if (instr.Opcode == IrOpcode.Phi)
        {
            
            TypeInfo common = TypeInfo.Unknown;
            foreach (var src in instr.Sources)
            {
                var srcType = src.Type;
                if (srcType == TypeInfo.Unknown)
                {
                    var key = SsaBuilder.GetVarKey(in src);
                    if (key != null && src.SsaVersion >= 0 && defMap.TryGetValue((key, src.SsaVersion), out var def))
                        srcType = def.Destination.Type;
                }
                if (srcType == TypeInfo.Unknown) continue;

                if (common == TypeInfo.Unknown)
                {
                    common = srcType;
                }
                else if (common != srcType)
                {
                    
                    if (common.PointerLevel == srcType.PointerLevel)
                    {
                        
                        if (common.BaseType == PrimitiveType.Void && srcType.BaseType != PrimitiveType.Void)
                            common = srcType;
                        else if (common.BaseType != PrimitiveType.Void && srcType.BaseType == PrimitiveType.Void)
                        {
                            
                        }
                        else
                        {
                            return TypeInfo.Unknown; 
                        }
                    }
                    else
                    {
                        return TypeInfo.Unknown; 
                    }
                }
            }
            return common;
        }

        return TypeInfo.Unknown;
    }

    private static TypeInfo InferNumericType(byte bitSize, bool signed) => (bitSize, signed) switch
    {
        (8, false) => new TypeInfo { BaseType = PrimitiveType.UInt8 },
        (8, true)  => new TypeInfo { BaseType = PrimitiveType.Int8 },
        (16, false) => new TypeInfo { BaseType = PrimitiveType.UInt16 },
        (16, true)  => new TypeInfo { BaseType = PrimitiveType.Int16 },
        (32, false) => new TypeInfo { BaseType = PrimitiveType.UInt32 },
        (32, true)  => new TypeInfo { BaseType = PrimitiveType.Int32 },
        (64, false) => new TypeInfo { BaseType = PrimitiveType.UInt64 },
        (64, true)  => new TypeInfo { BaseType = PrimitiveType.Int64 },
        _ => TypeInfo.Unknown
    };
}
