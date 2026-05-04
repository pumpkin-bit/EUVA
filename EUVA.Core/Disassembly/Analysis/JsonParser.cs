// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EUVA.Core.Disassembly.Analysis;

public class FunctionHashItem
{
    public string Crc32 { get; set; } = string.Empty;
    public string Name  { get; set; } = string.Empty;
    public string Lib   { get; set; } = string.Empty;
}

public class StringTrigger
{
    public List<string> Substrings { get; set; } = new();
    public string       NewName    { get; set; } = string.Empty;
}

public class RegexTrigger
{
    public string Pattern   { get; set; } = string.Empty;
    public string NewName   { get; set; } = string.Empty;
    public int    MinLength { get; set; } = 0;
}

public class ConstantPattern
{
    public string Value   { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
}

public class ApiChain
{
    public string       Id           { get; set; } = string.Empty;
    public string       FunctionName { get; set; } = string.Empty;
    public string       Description  { get; set; } = string.Empty;
    public List<string> Sequence     { get; set; } = new();
    public bool         RequireAll   { get; set; } = false;
    public int          MinMatches   { get; set; } = 2;
}

public class ApiSignature
{
    public string?       ReturnName { get; set; }
    public int           ArgCount   { get; set; }
    public List<string>? ArgNames   { get; set; }
}

public class SignatureDatabase
{
    public List<FunctionHashItem>           FunctionHashes    { get; set; } = new(); 
    public List<StringTrigger>              StringTriggers    { get; set; } = new();
    public List<RegexTrigger>               RegexTriggers     { get; set; } = new();
    public List<ConstantPattern>            ConstantPatterns  { get; set; } = new();
    public List<ApiChain>                   ApiChains         { get; set; } = new();
    public Dictionary<string, ApiSignature> ApiSignatures     { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> StructDefinitions { get; set; } = new();
}

public sealed class ApiSignatureDictionaryConverter : JsonConverter<Dictionary<string, ApiSignature>>
{
    public override Dictionary<string, ApiSignature> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dict = new Dictionary<string, ApiSignature>(StringComparer.OrdinalIgnoreCase);
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected StartObject");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return dict;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected PropertyName");

            string key = reader.GetString()!;
            reader.Read();

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var sig = JsonSerializer.Deserialize<ApiSignature>(ref reader, options);
            if (sig != null) dict[key] = sig;
        }
        throw new JsonException("Unexpected end of JSON");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, ApiSignature> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (IDictionary<string, ApiSignature>)value, options);
}

public sealed class StructDefinitionsDictionaryConverter : JsonConverter<Dictionary<string, Dictionary<string, string>>>
{
    public override Dictionary<string, Dictionary<string, string>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected StartObject");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected PropertyName");

            string structName = reader.GetString()!;
            reader.Read();

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
            if (fields != null && fields.Count > 0) result[structName] = fields;
        }
        throw new JsonException("Unexpected end of JSON");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, Dictionary<string, string>> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (IDictionary<string, Dictionary<string, string>>)value, options);
}

public static class SignatureCache
{
    public static SignatureDatabase Db { get; private set; } = new();

    public static Dictionary<uint, string> FunctionHashesLookup { get; private set; } = new(); 
    public static Dictionary<ulong, string> ConstantNames { get; private set; } = new();
    public static Dictionary<string, Dictionary<int, string>> StructFieldLookup { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    private static List<(Regex Rx, int MinLength, string NewName)> _compiledRegexes = new();

    public static void Load(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var jsonBytes = File.ReadAllBytes(filePath);
        var options   = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ApiSignatureDictionaryConverter());
        options.Converters.Add(new StructDefinitionsDictionaryConverter());
        Db = JsonSerializer.Deserialize<SignatureDatabase>(jsonBytes, options) ?? new SignatureDatabase();

        BuildFunctionHashes();
        BuildConstantNames();
        BuildCompiledRegexes();
        BuildStructFieldLookup();
    }

    public static void LoadBin(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var binBytes = File.ReadAllBytes(filePath);
        var options = MessagePack.MessagePackSerializerOptions.Standard
            .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance);
        Db = MessagePack.MessagePackSerializer.Deserialize<SignatureDatabase>(binBytes, options) ?? new SignatureDatabase();

        BuildFunctionHashes();
        BuildConstantNames();
        BuildCompiledRegexes();
        BuildStructFieldLookup();
    }

    public static string? GetNameForString(string value)
    {
        foreach (var trigger in Db.StringTriggers)
            foreach (var sub in trigger.Substrings)
                if (value.Contains(sub, StringComparison.OrdinalIgnoreCase)) return trigger.NewName;

        if (value.Length > 0)
            foreach (var (rx, minLen, newName) in _compiledRegexes)
            {
                if (value.Length < minLen) continue;
                if (rx.IsMatch(value)) return newName;
            }
        return null;
    }

    public static string? GetNameForConstant(ulong value) => ConstantNames.TryGetValue(value, out var name) ? name : null;

    public static string? GetChainName(ICollection<string> calledApis)
    {
        foreach (var chain in Db.ApiChains)
        {
            if (chain.Sequence.Count == 0) continue;
            int matchCount = 0;
            foreach (var api in chain.Sequence)
            {
                if (calledApis.Contains(api)) matchCount++;
            }

            bool matched = chain.RequireAll ? matchCount == chain.Sequence.Count : matchCount >= Math.Max(1, chain.MinMatches);
            if (matched) return chain.FunctionName;
        }
        return null;
    }

    public static string? GetFieldName(string? structTypeName, int offset)
    {
        if (structTypeName != null && StructFieldLookup.TryGetValue(structTypeName, out var fields) && fields.TryGetValue(offset, out var name))
            return name;
        foreach (var flds in StructFieldLookup.Values)
            if (flds.TryGetValue(offset, out var n)) return n;
        return null;
    }

    private static void BuildFunctionHashes()
    {
        FunctionHashesLookup = new Dictionary<uint, string>();
        foreach (var fh in Db.FunctionHashes)
        {
            if (string.IsNullOrWhiteSpace(fh.Crc32)) continue;
            uint crc = (uint)ParseUInt64(fh.Crc32);
            FunctionHashesLookup.TryAdd(crc, $"{fh.Lib}::{fh.Name}");
        }
    }

    private static void BuildConstantNames()
    {
        ConstantNames = new Dictionary<ulong, string>(Db.ConstantPatterns.Count);
        foreach (var p in Db.ConstantPatterns)
        {
            if (string.IsNullOrWhiteSpace(p.Value)) continue;
            ulong val = ParseUInt64(p.Value);

            
            if (!IsSafeGlobalConstant(val)) 
                continue;

            ConstantNames.TryAdd(val, p.NewName);
        }
    }

    private static bool IsSafeGlobalConstant(ulong val)
    {
        
        if (val <= 0x1000) return false;

        
        
        
        if ((val & (val - 1)) == 0) return false;

        
        if (val == 0xFFFF || val == 0xFFFFFFFF || val == 0xFFFFFFFFFFFFFFFF) return false;

        
        return true; 
    }

    private static void BuildCompiledRegexes()
    {
        _compiledRegexes = new List<(Regex, int, string)>(Db.RegexTriggers.Count);
        foreach (var rt in Db.RegexTriggers)
        {
            if (string.IsNullOrWhiteSpace(rt.Pattern)) continue;
            try
            {
                var rx = new Regex(rt.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                _compiledRegexes.Add((rx, rt.MinLength, rt.NewName));
            }
            catch { }
        }
    }

    private static void BuildStructFieldLookup()
    {
        StructFieldLookup = new Dictionary<string, Dictionary<int, string>>(Db.StructDefinitions.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (structName, rawFields) in Db.StructDefinitions)
        {
            if (rawFields == null || rawFields.Count == 0) continue;
            var compiled = new Dictionary<int, string>(rawFields.Count);
            foreach (var (offsetStr, fieldName) in rawFields)
            {
                if (string.IsNullOrWhiteSpace(offsetStr) || string.IsNullOrWhiteSpace(fieldName)) continue;
                try { compiled.TryAdd((int)ParseUInt64(offsetStr), fieldName); } catch { }
            }
            if (compiled.Count > 0) StructFieldLookup[structName] = compiled;
        }
    }

    private static ulong ParseUInt64(string s)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            
            bool isNegative = s.StartsWith("-");
            if (isNegative) s = s.Substring(1).Trim();
            
            ulong val = 0;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hexPart = s.Substring(2).Replace("-", "").Trim();
                val = string.IsNullOrEmpty(hexPart) ? 0 : Convert.ToUInt64(hexPart, 16);
            }
            else
            {
                val = ulong.Parse(s);
            }
            
            return isNegative ? (ulong)(-(long)val) : val;
        }
        catch
        {
            return 0;
        }
    }
}