// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EUVA.Core.Robots.Patterns;

public sealed class PatternEngine
{
    private readonly List<TransformRule> _rules;
    private readonly DataFlowTracker _dataflow;
    private int _totalPatches;
    private readonly HashSet<string> _usedStructs = new(StringComparer.OrdinalIgnoreCase);

    public PatternEngine(List<TransformRule> rules)
    {
        _rules = rules;
        _dataflow = new DataFlowTracker();
        _totalPatches = 0;

        if (EUVA.Core.Robots.Patterns.Types.TypeDatabase.Structs.Count == 0)
        {
            EUVA.Core.Robots.Patterns.Types.TypeDatabase.Load(
                EUVA.Core.Robots.Patterns.Types.TypeDatabase.GetDefaultStructsFile());
        }
    }

    public DataFlowTracker DataFlow => _dataflow;
    public int TotalPatches => _totalPatches;
    public IEnumerable<string> UsedStructs => _usedStructs;

    public string[] ApplyAll(string[] lines)
    {
        _totalPatches = 0;
        _dataflow.AnalyzePass(lines);

        EUVA.Core.Robots.Patterns.Types.AutoStructBuilder.DiscoverTypes(lines, _dataflow);

        var result = new string[lines.Length];
        Array.Copy(lines, result, lines.Length);

        for (int i = 0; i < result.Length; i++)
        {
            string original = result[i];
            string current = ApplyStructFields(original);

            foreach (var rule in _rules)
            {
                if (rule.RequiresContext && rule.Mode == "context")
                {
                    current = ApplyContextRule(current, rule, i);
                    continue;
                }

                if (rule.Mode == "multiline") continue; 

              
                current = ApplyRegexRule(current, rule);
            }

            if (current != original)
            {
                result[i] = current;
                _totalPatches++;
            }
        }

        return result;
    }

    private string ApplyRegexRule(string line, TransformRule rule)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;

        try
        {
            if (!string.IsNullOrEmpty(rule.Guard))
            {
                if (!Regex.IsMatch(line, rule.Guard)) return line;
            }

            if (Regex.IsMatch(line, rule.Pattern))
            {
                return Regex.Replace(line, rule.Pattern, rule.Replacement);
            }
        }
        catch (RegexMatchTimeoutException) { }
        catch (ArgumentException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[PatternEngine] Bad regex in rule '{rule.Id}': {ex.Message}");
            Console.ResetColor();
        }

        return line;
    }

    private string ApplyStructFields(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;

        string current = line;

        var rxField = new Regex(@"([\w_]+)->field_([0-9A-Fa-f]+)");
        current = rxField.Replace(current, m => 
        {
            var varName = m.Groups[1].Value;
            var offsetHex = m.Groups[2].Value; 
            
            var knownType = _dataflow.GetKnownType(varName);
            if (knownType != null)
            {
                var structDef = EUVA.Core.Robots.Patterns.Types.TypeDatabase.GetStruct(knownType);
                if (structDef != null && structDef.Fields.TryGetValue(offsetHex, out var fieldDef))
                {
                    _usedStructs.Add(structDef.Name);
                    return $"{varName}->{fieldDef.Name}";
                }
            }
            return m.Value;
        });

        var rxRawCast = new Regex(@"\*\s*\(\s*(?<type>[\w_:]+(?:\s*\*)?)\s*\*\s*\)\s*\(\s*(?<var>[\w_]+)\s*(?<sign>[\+\-])\s*(?:0x)?(?<offset>[0-9A-Fa-f]+)\s*\)");
        current = rxRawCast.Replace(current, m =>
        {
            var varName = m.Groups["var"].Value;
            var sign = m.Groups["sign"].Value;
            string offsetHex = m.Groups["offset"].Value;
            if (sign == "-") offsetHex = "minus_" + offsetHex;
            
            var knownType = _dataflow.GetKnownType(varName);
            if (knownType != null)
            {
                var structDef = EUVA.Core.Robots.Patterns.Types.TypeDatabase.GetStruct(knownType);
                if (structDef != null && structDef.Fields.TryGetValue(offsetHex, out var fieldDef))
                {
                    _usedStructs.Add(structDef.Name);
                    return $"{varName}->{fieldDef.Name}";
                }
            }
            return m.Value;
        });

        return current;
    }

    private string ApplyContextRule(string line, TransformRule rule, int lineIndex)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;

        try
        {
            switch (rule.ContextKey)
            {
                case "resolve_comparison":
                    return ResolveComparison(line, rule);

                case "rename_by_call":
                    return RenameByCallResult(line, rule, lineIndex);

                case "add_type_annotation":
                    return AddTypeAnnotation(line, rule);

                default:
                    return ApplyRegexRule(line, rule);
            }
        }
        catch (Exception)
        {
            return line;
        }
    }

    private string ResolveComparison(string line, TransformRule rule)
    {
        var match = Regex.Match(line, @"if\s*\(\s*(\w+)\s*==\s*(\w+)\s*\)");
        if (!match.Success) return line;

        string varName = match.Groups[1].Value;
        string value = match.Groups[2].Value;

        string? resolved = _dataflow.TryResolveConstant(varName, value);
        if (resolved != null)
        {
            return line.Replace($"{varName} == {value}", $"{varName} == {resolved}");
        }

        return line;
    }

    private string RenameByCallResult(string line, TransformRule rule, int lineIndex)
    {
        var match = Regex.Match(line, @"^\s*(\w+)\s*=\s*");
        if (!match.Success) return line;

        string varName = match.Groups[1].Value;
        var sym = _dataflow.GetSymbol(varName);
        if (sym == null || sym.AssignedAtLine != lineIndex || sym.SemanticTag == null) return line;

        string newName = sym.SemanticTag switch
        {
            "os_version" => "dwVersion",
            "file_handle" => "hFile",
            "window_handle" => "hWnd",
            "module_handle" => "hModule",
            "process_handle" => "hProcess",
            "process_id" => "dwProcessId",
            "thread_id" => "dwThreadId",
            "allocated_mem" => "pAllocated",
            "string_length" => "nLength",
            "error_code" => "dwLastError",
            "prev_error_mode" => "dwPrevErrorMode",
            "func_pointer" => "pfnProc",
            "tick_count" => "dwTickCount",
            _ => null
        };

        if (newName != null && newName != varName)
        {
            return line.Replace(varName + " =", newName + " =");
        }

        return line;
    }

    private string AddTypeAnnotation(string line, TransformRule rule)
    {
        var match = Regex.Match(line, @"^(\s*)(\w+)\s*=\s*");
        if (!match.Success) return line;

        string indent = match.Groups[1].Value;
        string varName = match.Groups[2].Value;

        if (varName == "rax" || varName == "rcx" || varName == "rdx" || varName == "r8" || varName == "r9" ||
            varName == "rbx" || varName == "rsi" || varName == "rdi" || varName == "rsp" || varName == "rbp")
        {
            return line;
        }

        var sym = _dataflow.GetSymbol(varName);
        if (sym == null || sym.KnownType == null) return line;

        string typePrefix = sym.KnownType + " ";
        if (line.TrimStart().StartsWith(typePrefix)) return line;
        if (line.TrimStart().StartsWith("const ")) return line;

        if (sym.AssignedAtLine >= 0)
        {
            return indent + sym.KnownType + " " + line.TrimStart();
        }

        return line;
    }
}
