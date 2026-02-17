# EUVA — EngineUnpacker Visual Analyzer

> **The definitive open-source binary analysis workstation for the modern researcher.**  
> Zero bloat. Zero vendor lock-in. Maximum signal.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0) ![Static Analysis](https://img.shields.io/badge/Analysis-Static-4B0082?style=plastic&logo=linux-foundation&logoColor=white) ![Hex](https://img.shields.io/badge/Data-Hex_Manipulation-696969?style=plastic&logo=data-studio&logoColor=white) ![Low Latency](https://img.shields.io/badge/Perf-Low_Latency-FFD700?style=plastic&logo=speedtest&logoColor=black)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-informational)](https://github.com/euva) [![Framework](https://img.shields.io/badge/.NET-8.0--windows-purple)](https://dotnet.microsoft.com) [![Language](https://img.shields.io/badge/Language-C%23%2012.0-brightgreen)](https://learn.microsoft.com/en-us/dotnet/csharp/) ![WPF](https://img.shields.io/badge/UI-WPF-blue?style=plasti&logo=windows&logoColor=white) ![Memory](https://img.shields.io/badge/Memory-Mapped%20Files-lightgrey?style=plastic&logo=speedtest&logoColor=white)
![Build](https://img.shields.io/badge/Build-Stable-success?style=plastic&logo=github-actions&logoColor=white) ![Version](https://img.shields.io/badge/Release-v1.0-Green?style=plastic&logo=semver&logoColor=white)

---
## Disclaimer
**This program is under active development. Experimental builds may contain bugs or lead to unexpected behavior. Use with caution.**

This software is provided "as is", without warranty of any kind. EUVA is a high-precision instrument designed for educational purposes and security research. The author is not responsible for any system instability, data loss, or legal consequences resulting from the misuse of this tool.
By using EUVA, you acknowledge that:
**You are solely responsible for your actions.**
**You understand the risks of modifying binary files and process memory.**
**You will respect the laws and regulations of your jurisdiction.**


--- 

## Manifesto

010 Editor and HxD are relics. Their architecture was designed for a world where megabytes were large numbers and binary research meant staring at a static grid. EUVA is the answer to the question nobody in the toolchain ecosystem bothered to ask: *what does a binary analysis tool look like if it's built from scratch in 2026 for researchers who actually understand what they're doing?*

EUVA is a **WPF/C# native application** that operates directly on the binary layer. No heavy runtime frameworks handling PE parsing. No scripting interpreters bolted on as afterthoughts. No 200-MB installs for features you'll never use. What EUVA provides instead:

- A **Memory-Mapped File engine** that scales to arbitrarily large binaries with zero heap pressure
- A **structured PE decomposition layer** that turns raw bytes into a navigable semantic tree
- A **Dirty Tracking system** that achieves nanosecond-precision change verification directly in the renderer
- A **built-in x86 assembler** that compiles instructions to opcodes inline, with automatic relative offset resolution
- A **scriptable patching DSL** (`.euv` format) with live file-watch execution
- A **plugin-extensible detector pipeline** for packer/protector identification
- A **fully themeable rendering layer** with a 30-token color palette and hot-reload support

---



## Core Subsystems

### 1. Memory-Mapped File Engine (`VirtualizedHexView`)

EUVA does not load files into `byte[]` arrays. It maps them directly through the OS virtual memory system via `System.IO.MemoryMappedFiles`. This is a fundamental architectural choice, not an optimization.

```csharp
_mmf = MemoryMappedFile.CreateFromFile(
    filePath, FileMode.Open, null, 0, 
    MemoryMappedFileAccess.ReadWrite);
_accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
```

**What this means in practice:**

| Concern | HxD/010 Editor approach | EUVA approach |
|---|---|---|
| 1 GB binary | Full allocation or paged chunking | Kernel handles paging transparently |
| Write operation | Copy-on-write to a temp buffer | Direct `_accessor.Write(offset, value)` |
| Flush to disk | Serialization pass | `_accessor.Flush()` — OS flushes dirty pages |
| Memory pressure | Scales with file size | Scales with *viewport*, not file size |

The renderer operates in a fully virtualized coordinate system. `_currentScrollLine` is the only state separating screen-space from file-space:

```csharp
long firstVisibleLine = _currentScrollLine;
long lastVisibleLine  = Math.Min(firstVisibleLine + visibleLines, totalLines);

for (long line = firstVisibleLine; line < lastVisibleLine; line++)
{
    long offset = line * _bytesPerLine;
    double y    = (line - firstVisibleLine) * _lineHeight + 25;
    DrawLine(dc, offset, y, offsetColumnWidth, asciiColumnStart);
}
```

A 4 GB binary and a 4 KB binary render with identical overhead.

**Scroll Acceleration:**

Mouse wheel input is multiplied by keyboard modifier state, providing three-speed navigation:

```csharp
int multiplier = 1;
if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) multiplier = 100;
else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) multiplier = 1000;

int linesToScroll = (e.Delta > 0 ? -3 : 3) * multiplier;
```

Default: 3 lines/tick. `Ctrl`: 300 lines/tick. `Shift`: 3000 lines/tick.

---

### 2. Dirty Tracking System

Dirty Tracking is EUVA's mechanism for instant, zero-latency verification of binary modifications. It is not a diff engine. It is a live coordinate index.

```csharp
private readonly HashSet<long> _modifiedOffsets = new();

public void WriteByte(long offset, byte value)
{
    _accessor.Write(offset, value);
    lock (_modifiedOffsets)
    {
        _modifiedOffsets.Add(offset);
    }
}
```

Every write operation — whether from the script engine, manual editing, or assembler output — registers the exact file offset in `_modifiedOffsets`. The renderer queries this set for every byte it draws:

```csharp
if (_modifiedOffsets.Contains(byteOffset))
{
    var modBackground = new SolidColorBrush(
        Color.FromArgb(80, 255, 0, 128));  // Hot-pink overlay
    dc.DrawRectangle(modBackground, null,
        new Rect(x - 2, y - 2, _charWidth * 2.5, _lineHeight));
}
```

Modified bytes receive a distinct visual overlay **in the same render pass** as unmodified bytes. There is no diff phase, no second pass, no "compare mode." The moment a byte is written, it is visually distinct from the surrounding data.

**Change Navigation:**

`F3` invokes `JumpToNextChange()`, which walks `_modifiedOffsets` in ascending order from the current selection, wrapping to the minimum offset when exhausted:

```csharp
var nextChange = _modifiedOffsets
    .Where(o => o > startSearchFrom)
    .OrderBy(o => o)
    .Cast<long?>()
    .FirstOrDefault();

if (nextChange == null)
    nextChange = _modifiedOffsets.Min();
```

This enables rapid verification workflows: run a script patch, press `F3` repeatedly to audit every modified byte individually, verify against expected values in the Inspector.

---

### 3. PE Structural Decomposition (`PEMapper`)

`PEMapper` implements `IBinaryMapper` and produces a fully navigable `BinaryStructure` tree from raw PE binary data. Parsing is delegated to AsmResolver for header extraction, but the semantic decomposition, region mapping, and tree construction are native EUVA logic.

**Parse Pipeline:**

```
Parse(ReadOnlySpan<byte> data)
    ├── ParseDosHeader()     → IMAGE_DOS_HEADER node + DOS region
    ├── ParseNtHeaders()
    │       ├── ParseFileHeader()    → IMAGE_FILE_HEADER node
    │       └── ParseOptionalHeader() → IMAGE_OPTIONAL_HEADER node
    ├── ParseSections()      → IMAGE_SECTION_HEADER nodes + Code/Data regions
    └── ParseDataDirectories() → Import/Export directory nodes
```

**Section Region Coloring Logic:**

Section type is detected from PE characteristics flags and mapped directly to a WPF `Color`:

```csharp
var color = section.IsContentCode             ? Colors.LightGreen  :
            section.IsContentInitializedData  ? Colors.LightBlue   :
            section.IsContentUninitializedData? Colors.LightGray    :
                                                Colors.LightYellow;
```

**Reflection-Based Field Resolution:**

PE header fields vary across library versions and AsmResolver API surfaces. `PEMapper` uses a multi-candidate reflection probe to resolve properties robustly without hard-coded field names:

```csharp
private static object? GetNestedMemberValue(object? obj, params string[] names)
{
    foreach (var name in names)
    {
        // Walk dotted paths (e.g., "Header.PointerToRawData")
        // Try Property → fall back to Field
        // Return first successful resolution
    }
    return null;
}
```

Calling sites provide ordered fallback lists:

```csharp
var rawPtrVal = GetNestedMemberValue(section,
    "Header.PointerToRawData",  // AsmResolver 5.x path
    "PointerToRawData",         // flat property
    "Offset");                  // final fallback
```

This makes `PEMapper` resilient to upstream API changes without requiring conditional compilation.

**The `DataRegion` Model:**

Every parsed structural element is materialized as a `DataRegion` with precise byte boundaries:

```csharp
public class DataRegion
{
    public long      Offset          { get; init; }
    public long      Size            { get; init; }
    public string    Name            { get; init; }
    public RegionType Type           { get; init; }
    public Color     HighlightColor  { get; init; }
    public int       Layer           { get; init; }
    public BinaryStructure? LinkedStructure { get; init; }

    public bool Contains(long offset) => offset >= Offset && offset < Offset + Size;
}
```

`RegionType` covers the full PE taxonomy:

| Value | Semantic |
|---|---|
| `Header` | DOS/NT header areas |
| `Code` | Executable sections |
| `Data` | Initialized data sections |
| `Import` | Import Address Table region |
| `Export` | Export Directory region |
| `Resource` | `.rsrc` section |
| `Relocation` | `.reloc` section |
| `Debug` | Debug directory data |
| `Overlay` | Post-EOF appended data |
| `Signature` | Authenticode signature region |
| `Unknown` | Unclassified regions |

---

### 4. Data Inspector — Type Interpretation Engine

The Inspector panel provides simultaneous multi-type interpretation of any selected file offset. All reads go directly through `_accessor.ReadArray()` — no intermediate copies, no allocation.

**Supported Type Matrix:**

| Type | Size (bytes) | Implementation | Notes |
|---|---|---|---|
| `Int8 / UInt8` | 1 | `(sbyte)b \| b` | Binary bit-string display included |
| `Int16 / UInt16` | 2 | `BitConverter.ToUInt16` | |
| `DOS Date` | 2 | Bitfield decode | `year = ((v >> 9) & 0x7F) + 1980` |
| `DOS Time` | 2 | Bitfield decode | `hour = v >> 11`, `min = (v >> 5) & 0x3F`, `sec = (v & 0x1F) * 2` |
| `Int24 / UInt24` | 3 | Manual byte construction | `b[0] \| (b[1] << 8) \| (b[2] << 16)` |
| `Int32 / UInt32` | 4 | `BitConverter.ToUInt32` | |
| `Single (float32)` | 4 | `BitConverter.ToSingle` | IEEE 754 |
| `time_t (32-bit)` | 4 | `DateTimeOffset.FromUnixTimeSeconds(v)` | Unix epoch |
| `Int64 / UInt64` | 8 | `BitConverter.ToUInt64` | |
| `Double (float64)` | 8 | `BitConverter.ToDouble` | IEEE 754 |
| `FILETIME` | 8 | `DateTime.FromFileTime((long)v)` | 100ns intervals since 1601-01-01 |
| `OLETIME` | 8 | `DateTime.FromOADate(double)` | COM Automation date |
| `GUID / UUID` | 16 | `new Guid(b).ToString("B")` | RFC 4122 format |
| `ULEB128` | Variable | Streaming decode (max 10 bytes) | DWARF/WebAssembly encoding |
| `SLEB128` | Variable | Sign-extended streaming decode | Signed variant |

**DOS Timestamp Bitfield Decoding:**

The MS-DOS FAT timestamp encoding packs date and time into two 16-bit words with the following layout:

```
Date word (16 bits):
  [15:9]  Year offset from 1980 (0–119 → 1980–2099)
  [8:5]   Month (1–12)
  [4:0]   Day (1–31)

Time word (16 bits):
  [15:11] Hours (0–23)
  [10:5]  Minutes (0–59)
  [4:0]   2-second intervals (0–29 → 0–58 seconds)
```

```csharp
public static string ToDosDate(ushort v) =>
    $"{((v >> 9) & 0x7F) + 1980:D4}-{(v >> 5) & 0x0F:D2}-{v & 0x1F:D2}";

public static string ToDosTime(ushort v) =>
    $"{v >> 11:D2}:{(v >> 5) & 0x3F:D2}:{(v & 0x1F) * 2:D2}";
```

**ULEB128 / SLEB128 Streaming Decoder:**

Variable-length encodings (used in DWARF debug info, WebAssembly modules, and Android DEX) are decoded by streaming 7-bit groups with continuation bits:

```csharp
public static (long value, int size) ReadLEB128(byte[] data, bool signed)
{
    long result = 0; int shift = 0; int pos = 0;
    while (pos < data.Length)
    {
        byte b = data[pos++];
        result |= (long)(b & 0x7F) << shift;
        shift += 7;
        if ((b & 0x80) == 0)
        {
            // Signed: sign-extend if the final group's MSB is set
            if (signed && (shift < 64) && ((b & 0x40) != 0))
                result |= -(1L << shift);
            break;
        }
    }
    return (result, pos);
}
```

**Endianness Toggle:**

All multi-byte reads respect the current endian mode (`IsLittleEndian`). Toggling via `BtnEndian_Click` immediately re-parses the current selection:

```csharp
byte[] GetLE(int count) {
    var b = GetRaw(count);
    if (!IsLittleEndian) Array.Reverse(b);
    return b;
}
```

---

### 5. Signature Scanner (`SignatureScanner`)

A zero-allocation, `ReadOnlySpan<byte>`-native pattern matching engine with wildcard support.

**Pattern Format:**

Patterns are space-delimited hex byte strings. `??` or `?` denotes a wildcard position that matches any byte value.

```
"55 50 58 30"                          // exact match: UPX0
"60 BE ?? ?? ?? ?? 8D BE ?? ?? ?? ??"  // UPX entry stub with wildcards
"B8 ?? ?? ?? ?? B9 ?? ?? ?? ?? 50 51 E8"  // Themida entry
```

**Parser:**

```csharp
private static PatternByte[] ParsePattern(string pattern)
{
    var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var result = new PatternByte[parts.Length];
    for (int i = 0; i < parts.Length; i++)
        result[i] = (parts[i] == "??" || parts[i] == "?")
            ? new PatternByte { IsWildcard = true }
            : new PatternByte { Value = Convert.ToByte(parts[i], 16) };
    return result;
}
```

**Matcher:**

```csharp
private static bool MatchesPattern(ReadOnlySpan<byte> data, PatternByte[] pattern)
{
    for (int i = 0; i < pattern.Length; i++)
        if (!pattern[i].IsWildcard && data[i] != pattern[i].Value)
            return false;
    return true;
}
```

The outer scan loop slices `ReadOnlySpan<byte>` at each candidate position — no allocations, no copies.

**Shannon Entropy Calculator:**

```csharp
public static double CalculateEntropy(ReadOnlySpan<byte> data)
{
    Span<int> frequencies = stackalloc int[256];
    foreach (byte b in data) frequencies[b]++;

    double entropy = 0.0;
    double len     = data.Length;
    for (int i = 0; i < 256; i++)
    {
        if (frequencies[i] == 0) continue;
        double p = frequencies[i] / len;
        entropy -= p * Math.Log2(p);
    }
    return entropy;  // Bits per byte, range [0.0, 8.0]
}
```

Entropy thresholds used by the detector pipeline:

| Threshold | Interpretation |
|---|---|
| `< 5.0` | Uncompressed / sparse data |
| `5.0 – 7.0` | Normal executable code |
| `> 7.0` | Compressed / encrypted (UPX) |
| `> 7.5` | Heavy obfuscation (Themida/WinLicense) |

---

### 6. Detector Pipeline (`DetectorManager`)

The detection subsystem is a priority-ordered, async plugin chain. Each registered `IDetector` receives the full file buffer and the parsed `BinaryStructure` tree, then produces a `DetectionResult` with a confidence score in `[0.0, 1.0]`.

**Registration and Sorting:**

```csharp
public void RegisterDetector(IDetector detector)
{
    _detectors.Add(detector);
    _detectors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
}
```

Lower priority numbers run first. Built-in detectors:

| Detector | Priority | Detection Strategy |
|---|---|---|
| `ThemidaDetector` | 5 | Signature scan + section name check + Import Table anomaly + entropy |
| `UPXDetector` | 10 | Signature scan (`UPX0/1/!`) + entry stub pattern + section names + entropy |
| `FSGDetectorPlugin` | 15 | 3-version signature scan + section sizing heuristics + import anomaly |

**Confidence Scoring — UPX Example:**

| Evidence | Confidence Contribution |
|---|---|
| Any UPX signature match | +0.40 |
| Section named `UPX0` or `UPX1` | +0.40 |
| Section named `.UPX0` or `.UPX1` | +0.30 |
| Entropy > 7.0 | +0.20 |
| Total (capped at 1.0) | Max 1.00 |

**Plugin System:**

Third-party detectors can be distributed as standalone `.dll` files and loaded at runtime:

```csharp
public void LoadFromDirectory(string pluginDirectory)
{
    var dllFiles = Directory.GetFiles(pluginDirectory, "*.dll",
        SearchOption.AllDirectories);
    foreach (var dllFile in dllFiles)
    {
        var assembly = Assembly.LoadFrom(dllFile);
        LoadFromAssembly(assembly);
    }
}
```

Any class implementing `IDetector` (or `IDetectorPlugin` for richer metadata) is automatically instantiated and registered. `IDetectorPlugin` adds lifecycle hooks:

```csharp
public interface IDetectorPlugin : IDetector
{
    PluginMetadata Metadata { get; }
    void Initialize();
    void Cleanup();
}
```

---

## Built-in Assembler (`AsmLogic`)

EUVA includes a proprietary x86 assembler compiled entirely from scratch in C#. No NASM. No Keystone. No external assembly library of any kind. The assembler translates mnemonic strings to raw opcode byte sequences inline, with full support for automatic relative offset calculation.

### Register Table

```csharp
private static readonly Dictionary<string, byte> Regs = new() {
    { "eax", 0 }, { "ecx", 1 }, { "edx", 2 }, { "ebx", 3 },
    { "esp", 4 }, { "ebp", 5 }, { "esi", 6 }, { "edi", 7 }
};
```

### Opcode Table

```csharp
private static readonly Dictionary<string, byte> Ops = new() {
    { "add", 0x01 }, { "or",  0x09 }, { "and", 0x21 },
    { "sub", 0x29 }, { "xor", 0x31 }, { "cmp", 0x39 },
    { "jmp", 0xE9 }, { "mov_eax", 0xB8 }
};
```

### Instruction Encoding Reference

#### `nop` — No Operation

```
Encoding: 0x90
Length:   1 byte
```

```csharp
if (mnemonic == "nop") return new byte[] { 0x90 };
```

#### `ret` — Return from Procedure

```
Encoding: 0xC3
Length:   1 byte
```

#### `jmp <target>` — Unconditional Jump (Near Relative)

This is the most architecturally significant instruction in the assembler. `jmp` requires the computation of a signed 32-bit relative displacement — the delta between the target address and the instruction's post-execution program counter.

**Encoding formula:**

```
rel32 = target_address - (current_address + 5)
```

The `+5` accounts for the 5-byte length of the `jmp rel32` instruction itself (1 opcode byte + 4 displacement bytes). The CPU's instruction pointer advances past the full instruction before the relative offset is applied.

```csharp
if (mnemonic == "jmp" && tokens.Length == 2)
{
    if (long.TryParse(tokens[1], out long targetAddr))
    {
        int relativeOffset = (int)(targetAddr - (currentAddr + 5));
        byte[] offsetBytes = BitConverter.GetBytes(relativeOffset);

        byte[] result = new byte[5];
        result[0] = 0xE9;  // jmp rel32
        Buffer.BlockCopy(offsetBytes, 0, result, 1, 4);
        return result;
    }
}
```

**Example:** Injecting a `jmp` from address `0x00401000` to `0x00402000`:

```
rel32 = 0x00402000 - (0x00401000 + 5)
      = 0x00402000 - 0x00401005
      = 0x00000FFB

Encoding: E9 FB 0F 00 00
```

This calculation is **critical for cross-section jumps**. When the script engine injects a trampoline from `.text` to injected code in `.data`, the absolute target address must be resolved at assembly time and the relative displacement recalculated. EUVA performs this automatically.

#### `mov <reg>, <imm32>` — Move Immediate to Register

Encodes the "Move Immediate to Register" short form (opcode `B8+rd`):

```csharp
if (mnemonic == "mov" && tokens.Length == 3)
{
    if (Regs.TryGetValue(tokens[1], out byte regIdx)
        && int.TryParse(tokens[2], out int val))
    {
        byte[] result = new byte[5];
        result[0] = (byte)(0xB8 + regIdx);  // B8 = MOV EAX; B9 = MOV ECX; etc.
        Buffer.BlockCopy(BitConverter.GetBytes(val), 0, result, 1, 4);
        return result;
    }
}
```

| Register | Opcode |
|---|---|
| `eax` | `B8 <imm32>` |
| `ecx` | `B9 <imm32>` |
| `edx` | `BA <imm32>` |
| `ebx` | `BB <imm32>` |
| `esp` | `BC <imm32>` |
| `ebp` | `BD <imm32>` |
| `esi` | `BE <imm32>` |
| `edi` | `BF <imm32>` |

#### `<op> <reg>, <reg>` — ALU Register-Register Operations

Two-operand ALU instructions are encoded using the ModRM byte. In register-to-register mode, ModRM has the format `11 src dst` (mod=11b, indicating direct register operands):

```
ModRM = 0xC0 | (src << 3) | dest
```

```csharp
if (Ops.ContainsKey(mnemonic) && tokens.Length == 3)
{
    if (Regs.TryGetValue(tokens[1], out byte dest)
        && Regs.TryGetValue(tokens[2], out byte src))
    {
        byte modRM = (byte)(0xC0 + (src << 3) + dest);
        return new byte[] { Ops[mnemonic], modRM };
    }
}
```

**ModRM encoding example — `xor eax, eax` (the canonical zero idiom):**

```
dest = Regs["eax"] = 0
src  = Regs["eax"] = 0
ModRM = 0xC0 | (0 << 3) | 0 = 0xC0

Encoding: 31 C0
```

**`xor ebx, ecx`:**

```
dest = Regs["ebx"] = 3
src  = Regs["ecx"] = 1
ModRM = 0xC0 | (1 << 3) | 3 = 0xCB

Encoding: 31 CB
```

### Complete Instruction Set Reference

| Mnemonic | Operands | Opcode | Length | Notes |
|---|---|---|---|---|
| `nop` | — | `90` | 1 | No-op |
| `ret` | — | `C3` | 1 | Near return |
| `jmp` | `imm_addr` | `E9 rel32` | 5 | Relative offset auto-calculated |
| `mov` | `reg, imm32` | `B8+rd imm32` | 5 | Register immediate |
| `add` | `reg, reg` | `01 /r` | 2 | ModRM register-register |
| `or` | `reg, reg` | `09 /r` | 2 | |
| `and` | `reg, reg` | `21 /r` | 2 | |
| `sub` | `reg, reg` | `29 /r` | 2 | |
| `xor` | `reg, reg` | `31 /r` | 2 | |
| `cmp` | `reg, reg` | `39 /r` | 2 | Flags only, no write |

---

## EUVA Scripting Language (`.euv` DSL)

EUVA scripts are structured text files with `.euv` extension. The script engine parses and executes them either on-demand (`F5`) or automatically via file-system watch when the file changes on disk.

### Execution Model

Scripts execute in a two-phase pipeline:

1. **Parse phase**: The engine reads all lines, resolves method declarations, and builds `MethodContainer` objects with body line lists and `clink` export declarations.
2. **Execute phase**: `FinalizeMethod()` iterates over each method's body, calling `ExecuteCommand()` per line. Variables are scoped: local to the method, with optional export to global scope via `clink`.

### Top-Level Structure

```euv
# Comments use # or //
start;                  # Required: marks begin of executable body

public:                 # Access modifier for following method
_createMethod(Name) {
    # method body
}

private:               # Private methods cannot export via clink
_createMethod(Internal) {
    # body
}

end;                   # Required: execution aborted if missing
```

The engine enforces the `end;` sentinel:

```csharp
if (!isTerminated) throw new Exception("FATAL: No 'end;' flag! Execution aborted.");
```

### `_createMethod(name)`

Declares a named method block. Methods are the primary unit of logical grouping. A method may be `public:` (can export symbols via `clink`) or `private:` (local execution only).

```euv
public:
_createMethod(GodMode) {
    find(MyFunc = 48 83 EC 28 41 B9 01)
    MyFunc : nop
    clink: [MyFunc]
}
```

**Execution flow:**

`FinalizeMethod()` runs each body line through `ExecuteCommand()`, then processes `clink` exports:

```csharp
if (method.Access == "public") {
    foreach (var exportName in method.Clinks.Keys) {
        if (localScope.TryGetValue(exportName, out long addr)) {
            string globalName = $"{method.Name}.{exportName}";
            globalScope[globalName] = addr;
        } else {
            throw new Exception($"Unknown: '{exportName}' not defined in {method.Name}!");
        }
    }
}
```

After finalization, exported symbols are accessible to subsequent methods as `MethodName.SymbolName` in the global scope.

### Command Reference

#### `find(variable = pattern)`

Scans the loaded binary for the first occurrence of a byte pattern and assigns the file offset to a local variable.

```euv
find(MyFunc = 48 83 EC 28 41 B9 01)
```

Pattern format: space-separated hex bytes. `??` is a wildcard matching any byte value.

```euv
find(StubEntry = 60 BE ?? ?? ?? ?? 8D BE ?? ?? ?? ??)
```

On match, `MyFunc` is set to the file offset of the first byte of the pattern. On no match, the variable is set to `-1`. The console logs the result:

```
[Found] MyFunc at 0x00401A30
```

**Implementation:**

```csharp
private long FindSignature(string pattern)
{
    var p = pattern.Split(' ')
        .Select(b => b == "??" ? (byte?)null : Convert.ToByte(b, 16))
        .ToArray();

    for (long i = 0; i < HexView.FileLength - p.Length; i++) {
        bool m = true;
        for (int j = 0; j < p.Length; j++)
            if (p[j] != null && HexView.ReadByte(i + j) != p[j]) { m = false; break; }
        if (m) return i;
    }
    return -1;
}
```

#### `set(variable = expression)`

Assigns a computed value to a local variable. The right-hand side is evaluated by `ParseMath()`, which supports hex literals, arithmetic operators, and variable substitution.

```euv
set(PatchBase = MyFunc + 0x10)
set(NopEnd    = PatchBase + 4)
set(NewTarget = 0x00405000)
```

#### Address `:` Data — Patch Command

The core write instruction. The address expression is on the left of `:`, the payload on the right.

**Syntax:**

```euv
<address_expr> : <payload>
```

**Payload types:**

**1. Assembly mnemonics** (assembled inline by `AsmLogic.Assemble`):

```euv
MyFunc       : nop
(MyFunc + 1) : nop
(MyFunc + 4) : mov eax, 999
(MyFunc + 9) : jmp 0x00405000
(MyFunc + 14): xor eax, eax
```

**2. Raw hex bytes:**

```euv
0x00401000 : 90 90 90 90
```

**3. ASCII string literals:**

```euv
0x00402000 : "Hello, World!"
```

The engine tries assembly first, then string parsing, then raw hex:

```csharp
bytes = AsmLogic.Assemble(dataPart, addr);

if (bytes == null && dataPart.Contains("\""))
    bytes = Encoding.ASCII.GetBytes(Regex.Match(dataPart, "\"(.*)\"").Groups[1].Value);

if (bytes == null)
    bytes = ParseBytes(dataPart);
```

#### `check <address> : <bytes>` — Conditional Execution

Reads bytes at the given address and halts the patch if they do not match. Used to verify pre-conditions before writing.

```euv
check MyFunc : 48 83 EC 28
```

If the bytes at `MyFunc` do not equal `48 83 EC 28`, the command returns early without executing subsequent patch lines.

#### `clink: [symbol1, symbol2, ...]`

Declares which local variables should be exported from the current method to the global scope after execution. Only valid inside `public:` methods.

```euv
public:
_createMethod(Scanner) {
    find(EntryPoint = 55 8B EC)
    find(ExitPoint  = C9 C3)

    clink: [EntryPoint, ExitPoint]
}

_createMethod(Patcher) {
    # Access exported symbols as Scanner.EntryPoint, Scanner.ExitPoint
    set(target = Scanner.EntryPoint + 0x20)
    target : nop
}
```

### Address Expression Engine (`ParseMath`)

All address expressions in EUVA scripts pass through `ParseMath()`, a full arithmetic evaluator supporting variable substitution, hex literals, and standard operators.

**Processing pipeline:**

1. Replace `.` or `()` with `lastAddress` (the post-write cursor from the previous patch operation)
2. Substitute all known variable names with their decimal string representations (longest-key-first to prevent partial matches)
3. Convert `0xNNN` hex literals to decimal
4. Evaluate the resulting expression using `System.Data.DataTable.Compute()`

```csharp
private long ParseMath(string expr, long lastAddr, Dictionary<string, long> effectiveScope)
{
    string formula = expr.Trim().Replace(" ", "");
    if (formula == "." || formula == "()") return lastAddr;

    // Substitute variables (longest first to avoid partial matches)
    var sortedKeys = effectiveScope.Keys.OrderByDescending(k => k.Length).ToList();
    foreach (var key in sortedKeys) {
        string pattern = @"\b" + Regex.Escape(key) + @"\b";
        formula = Regex.Replace(formula, pattern, effectiveScope[key].ToString("D"));
    }

    // Hex literal conversion
    formula = Regex.Replace(formula, @"0x([0-9A-Fa-f]+)", m =>
        long.Parse(m.Groups[1].Value, NumberStyles.HexNumber).ToString());

    return Convert.ToInt64(new DataTable().Compute(formula, null));
}
```

**Examples:**

| Expression | Resolves to |
|---|---|
| `MyFunc` | Address found by `find()` |
| `MyFunc + 4` | `MyFunc` plus 4 |
| `(MyFunc + 1)` | Parenthesized arithmetic |
| `0x00401000` | Absolute hex address |
| `MyFunc + 0x10` | Mixed variable + hex |
| `.` | `lastAddress` (cursor from previous write) |
| `Scanner.EntryPoint + 8` | Cross-method exported symbol |

### Complete `.euv` Example — GodMode Patch

This is the canonical test script shipped with EUVA:

```euv
start;

public:
_createMethod(GodMode) {

    # Locate the function prologue in the binary
    find(MyFunc = 48 83 EC 28 41 B9 01)

    # NOP out the first 4 bytes of the function
    MyFunc       : nop
    (MyFunc + 1) : nop
    (MyFunc + 2) : nop
    (MyFunc + 3) : nop

    # mov eax, 999  — force a known return value
    (MyFunc + 4) : mov eax, 999

    # Export MyFunc address for use by other methods
    clink: [MyFunc]
}

end;
```

**Execution trace:**

```
[Found] MyFunc at 0x00401A30
[Write] 0x00401A30 ← 90 (nop)
[Write] 0x00401A31 ← 90 (nop)
[Write] 0x00401A32 ← 90 (nop)
[Write] 0x00401A33 ← 90 (nop)
[Write] 0x00401A34 ← BF E7 03 00 00 (mov edi, 999)
[Link]  GodMode.MyFunc -> 0x401A30
```

### Script Engine — Live Watch Mode

EUVA monitors the active script file for changes using `FileSystemWatcher`:

```csharp
_scriptWatcher = new FileSystemWatcher(dir) {
    Filter        = file,
    NotifyFilter  = NotifyFilters.LastWrite | NotifyFilters.Size
                  | NotifyFilters.FileName  | NotifyFilters.CreationTime,
    EnableRaisingEvents = true
};
_scriptWatcher.Changed += OnScriptUpdated;
```

On any change event, the engine waits 400ms (debounce), then re-executes the full script. **Save the file in your editor → EUVA re-patches the binary immediately.** This enables a tight iterative loop: write a patch, save, see the Dirty Track overlay update, verify in the Inspector, refine, repeat.

`F5` forces immediate re-execution without waiting for a file change.

---

## Theme Engine

EUVA's visual presentation is fully controlled by `.themes` files — plain-text palettes defining 30 canonical UI color tokens.

### Token Reference

| Token | Default (R,G,B,A) | Used In |
|---|---|---|
| `Background` | `30,30,30,255` | Main window |
| `Sidebar` | `37,37,38,255` | Left panel |
| `Toolbar` | `45,45,48,255` | Menu bar |
| `Border` | `62,62,66,255` | Panel borders |
| `Hex_Background` | `30,30,30,255` | HexView canvas |
| `Hex_ByteActive` | `173,216,230,255` | Non-null byte text |
| `Hex_ByteNull` | `80,80,80,255` | Zero byte text |
| `Hex_ByteSelected` | `255,255,0,255` | Selected byte |
| `Hex_AsciiPrintable` | `144,238,144,255` | Printable ASCII chars |
| `Hex_AsciiNonPrintable` | `100,100,100,255` | Non-printable chars |
| `TreeIconSection` | `86,156,214,255` | Section nodes |
| `TreeIconField` | `78,201,176,255` | Field nodes |
| `PropertyKey` | `156,220,254,255` | Inspector labels |
| `PropertyValue` | `206,145,120,255` | Inspector values |
| `ConsoleError` | `244,71,71,255` | Error log lines |
| `ConsoleSuccess` | `106,153,85,255` | Success log lines |

### `.themes` File Format

```themes
# EUVA Color Palette
# Format: TokenName = R , G , B , A
# A = 255 means fully opaque

Background         = 18 , 18 , 18 , 255
Hex_Background     = 18 , 18 , 18 , 255
Hex_ByteActive     = 200, 200, 255, 255
Hex_ByteSelected   = 255, 200,   0, 255
Hex_AsciiPrintable = 100, 255, 100, 255
ConsoleError       = 255,  80,  80, 255
```

**Parser rules (No-Hysteria mode):**

- `#` starts an inline comment; everything after it on the line is ignored
- Blank and comment-only lines are silently skipped
- Malformed lines log an error and are skipped — the rest of the file continues loading
- Channel values outside `[0, 255]` are rejected per-line without aborting the file
- Every successfully parsed token is injected as both a `Color` and a frozen `SolidColorBrush` into `Application.Current.Resources`, enabling `DynamicResource` bindings throughout the entire WPF tree

### Hotkey Configuration (`.htk`)

Hotkeys are defined in plain-text `.htk` files:

```
# EUVA Hotkey Configuration
# Format: Action = Modifier + Key

NavInspector   = Alt + D1
NavSearch      = Alt + D2
NavDetections  = Alt + D3
NavProperties  = Alt + D4
CopyHex        = Control + C
CopyCArray     = Control + Shift + C
CopyPlainText  = Ctrl+Alt+C
```

Available actions:

| Action | Default Binding | Effect |
|---|---|---|
| `NavInspector` | `Alt+1` | Switch to Inspector tab |
| `NavSearch` | `Alt+2` | Switch to Search tab, focus input |
| `NavDetections` | `Alt+3` | Switch to Detections tab |
| `NavProperties` | `Alt+4` | Switch to Properties tab |
| `CopyHex` | `Ctrl+C` | Copy selection as hex string |
| `CopyCArray` | `Ctrl+Shift+C` | Copy selection as C byte array |
| `CopyPlainText` | `Ctrl+Alt+C` | Copy selection as decoded text |

---

## Encoding Support

EUVA's ASCII panel decodes bytes using a pre-computed lookup table initialized for any Windows code page:

```csharp
public void InitializeAsciiTable(int codePage)
{
    var encoding = Encoding.GetEncoding(codePage);
    byte[] allBytes = new byte[256];
    for (int i = 0; i < 256; i++) allBytes[i] = (byte)i;
    string decoded = encoding.GetString(allBytes);
    // Populate _asciiLookupTable[256]
}
```

Supported code pages (menu-selectable):

| Code Page | Encoding |
|---|---|
| 28591 | ISO-8859-1 (Latin-1), default |
| 1251 | Windows Cyrillic |
| 1252 | Windows Western |
| 65001 | UTF-8 |
| Any valid Windows code page | Via `Encoding.GetEncoding(codePage)` |

The table is regenerated on encoding change and the viewport invalidated immediately.

---

## MediaHex Mode

A secondary render mode that streams raw binary files as grayscale ASCII art video at 60 FPS, using the hex viewport as a canvas. Brightness is mapped through a 10-character density ramp:

```csharp
private readonly string _videoRamp = " .:-=+*#%@";
int rampIndex = value * (_videoRamp.Length - 1) / 255;
displayChar   = _videoRamp[rampIndex];
```

Byte value `0x00` maps to space (black). Byte value `0xFF` maps to `@` (white). The ASCII panel effectively becomes a 24×N-row grayscale display.

The engine uses `CompositionTarget.Rendering` for frame delivery, synchronized to the WPF composition clock, and reads raw frames sequentially from a `FileStream` with `FileOptions.SequentialScan`:

```csharp
int bytesRead = _rawVideoStream.Read(_frameBuffer, 0, _videoTotalSize);
if (bytesRead < _videoTotalSize)
{
    _rawVideoStream.Position = 0;  // Loop
    return;
}
HexView.SetMediaFrame(_frameBuffer);
```

---

## Build Requirements

| Component | Requirement |
|---|---|
| Runtime | .NET 8.0 Windows |
| Language | C# 12.0 |
| UI Framework | WPF (`net8.0-windows`) |
| Nullable | Enabled |
| PE Parsing | AsmResolver 5.5.1 |
| Architecture | x64 |
| OS | Windows 10 / 11 |

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

---

## Writing a Detector Plugin

Implement `IDetectorPlugin` and distribute as a `.dll`:

```csharp
public class MyPackerDetector : IDetectorPlugin
{
    public string Name    => "MyPacker Detector";
    public string Version => "1.0.0";
    public int Priority   => 20;

    public PluginMetadata Metadata => new()
    {
        Author           = "Your Name",
        Description      = "Detects MyPacker v1.x",
        SupportedPackers = new List<string> { "MyPacker 1.0" }
    };

    public void Initialize() { }
    public void Cleanup()    { }

    public bool CanAnalyze(BinaryStructure structure) =>
        structure.Type == "Root" && structure.Name == "PE File";

    public async Task<DetectionResult?> DetectAsync(
        ReadOnlyMemory<byte> fileData, BinaryStructure structure)
    {
        return await Task.Run(() =>
        {
            var signatures = SignatureScanner.FindPattern(
                fileData.Span, "60 ?? ?? ?? E8 00 00 00 00", "MyPacker Stub");

            if (signatures.Count == 0) return null;

            return new DetectionResult
            {
                Name         = "MyPacker",
                Type         = DetectionType.Packer,
                Confidence   = 0.85,
                Signatures   = signatures,
                DetectorName = Name
            };
        });
    }
}
```

Drop the compiled `.dll` into the plugins directory. EUVA loads it automatically at startup.

---

## License

EUVA is free software released under the **GNU General Public License v3.0**.

```
Copyright (C) 2026 EUVA Contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.
```

The full license text is available at <https://www.gnu.org/licenses/gpl-3.0>.

---

*EUVA — built for researchers who read hex for fun.*
