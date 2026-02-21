# EUVA — EngineUnpacker Visual Analyzer

> **The definitive open-source binary analysis workstation for the modern researcher.**  
> Zero bloat. Zero vendor lock-in. Maximum signal.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0) ![Static Analysis](https://img.shields.io/badge/Analysis-Static-4B0082?style=plastic&logo=linux-foundation&logoColor=white) ![Hex](https://img.shields.io/badge/Data-Hex_Manipulation-696969?style=plastic&logo=data-studio&logoColor=white) ![Low Latency](https://img.shields.io/badge/Perf-Low_Latency-FFD700?style=plastic&logo=speedtest&logoColor=black)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-informational)](https://github.com/euva) [![Framework](https://img.shields.io/badge/.NET-8.0--windows-purple)](https://dotnet.microsoft.com) [![Language](https://img.shields.io/badge/Language-C%23%2012.0-brightgreen)](https://learn.microsoft.com/en-us/dotnet/csharp/) ![WPF](https://img.shields.io/badge/UI-WPF-blue?style=plastic&logo=windows&logoColor=white) ![Memory](https://img.shields.io/badge/Memory-Mapped%20Files-lightgrey?style=plastic&logo=speedtest&logoColor=white)

---

## Manifesto

Most likely, this program answers the question: *what if hex editors were written from scratch in 2026?*

EUVA is a **WPF/C# native application** that operates directly on the binary layer. No heavy runtime frameworks. No scripting interpreters bolted on as afterthoughts. No 200-MB installs for features you'll never use.

What EUVA provides:

- A **Memory-Mapped File engine** that scales to arbitrarily large binaries with zero heap pressure
- A **WriteableBitmap renderer** that bypasses the WPF render pipeline entirely pixel-perfect output at native DPI
- A **GlyphCache subsystem** that rasterizes each character once and blits it via direct memory copy thereafter
- A **Dirty Tracking system** with lock-free snapshot reads for zero-latency change visualization
- A **Transactional Undo system** both step-by-step (`Ctrl+Z`) and full-session rollback (`Ctrl+Shift+Z`)
- A **structured PE decomposition layer** that turns raw bytes into a navigable semantic tree
- A **built-in x86 assembler** that compiles instructions to opcodes inline with automatic relative offset resolution
- A **scriptable patching DSL** (`.euv` format) with live file-watch execution
- A **plugin-extensible detector pipeline** for packer/protector identification
- A **fully themeable rendering layer** with persistent theme state across sessions

---

## Disclaimer

**This program is under active development. Experimental builds may contain bugs or lead to unexpected behavior. Use with caution.**

This software is provided "as is", without warranty of any kind. EUVA is a high-precision instrument designed for educational purposes and security research. The author is not responsible for any system instability, data loss, or legal consequences resulting from the misuse of this tool.

By using EUVA, you acknowledge that you are solely responsible for your actions, you understand the risks of modifying binary files and process memory, and you will respect the laws and regulations of your jurisdiction.

---

## Project Structure

The codebase is organized into layered namespaces with clear separation of concerns:

```
EUVA.Core.Interfaces     — IBinaryMapper, IDetector, IDetectorPlugin, IRegionProvider
EUVA.Core.Models         — BinaryStructure, DataRegion, DetectionResult, SignatureMatch
EUVA.Core.Parsers        — PEMapper, SignatureScanner
EUVA.Core.Detectors      — DetectorManager, ThemidaDetector, UPXDetector
EUVA.Plugins             — IDetectorPlugin contract, PluginMetadata
EUVA.Plugins.Samples     — FSGDetectorPlugin (reference implementation)
EUVA.UI                  — MainWindow, App, HotkeyManager, EUVAAction enum
EUVA.UI.Controls         — VirtualizedHexView, StructureTreeView, PropertyGrid
EUVA.UI.Theming          — ThemeManager, ThemeDiagnostics, EuvaSettings
```

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

**Span-based I/O:**

All multi-byte reads in the hot path use `ArrayPool<byte>` to avoid allocations and copy into the caller's `Span<byte>`:

```csharp
public void ReadBytes(long offset, Span<byte> buffer)
{
    int count = buffer.Length;
    byte[] tmp = ArrayPool<byte>.Shared.Rent(count);
    try { _accessor.ReadArray(offset, tmp, 0, count); tmp.AsSpan(0, count).CopyTo(buffer); }
    finally { ArrayPool<byte>.Shared.Return(tmp); }
}
```

**Scroll Acceleration:**

Mouse wheel input is multiplied by keyboard modifier state:

```csharp
int multiplier = 1;
if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) multiplier = 100;
else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))  multiplier = 1000;
int linesToScroll = (e.Delta > 0 ? -3 : 3) * multiplier;
```

Default: 3 lines/tick. `Ctrl`: 300 lines/tick. `Shift`: 3000 lines/tick.

---

### 2. WriteableBitmap Renderer

> **This is the most significant architectural change from the previous version.**

The previous renderer used WPF's `DrawingContext` (`OnRender` / `DrawText` / `DrawRectangle`). Every frame allocated managed `Brush` and `FormattedText` objects. At large files or high scroll speeds, this produced visible latency and tearing.

The current renderer bypasses the WPF render pipeline entirely:

1. All drawing targets a `uint[] _backBuffer` — a CPU-side BGRA32 pixel array
2. Glyphs are rasterized once into `uint[]` pixel arrays by `GlyphCache` and reused via `BlitGlyph()` (direct `memcpy`)
3. On frame completion, the back buffer is flushed to a `WriteableBitmap` via an unsafe pointer copy
4. The `WriteableBitmap` is displayed through a single `<Image>` element — zero WPF layout passes

**Render pipeline per frame:**

```
RenderFullFrame()
  ├── FillBackground()           — buf.AsSpan().Fill(solidColor)
  ├── DrawStringToBuffer()       — column headers via BlitGlyph
  └── RenderLineInternal() ×N   — for each visible line:
        ├── FillRect()           — selection background
        ├── FillRect()           — modified-byte background
        ├── BlitGlyph()          — hex nibbles (two per byte)
        └── BlitGlyph()          — ASCII character
FlushToWriteableBitmap()
  └── unsafe memcpy(_backBuffer → _bitmap.BackBuffer)
```

**Dirty-line partial updates:**

When only a subset of lines change (e.g., a single byte write), `_dirtyLines` tracks which lines need redraw. `RenderLine(lineIdx)` clears just that line's pixel strip and re-renders it, then flushes only the affected `Int32Rect` region:

```csharp
_bitmap.AddDirtyRect(new Int32Rect(0, yPx, _bitmapWidth, CellH));
```

Full redraws only occur on scroll, resize, theme change, or file load.

---

### 3. GlyphCache Subsystem

Each unique `(char, colorARGB)` pair is rasterized exactly once using `RenderTargetBitmap` with `TextRenderingMode.Aliased` and `TextFormattingMode.Display` for sub-pixel clarity at non-integer DPI values.

The rasterized `Pbgra32` output (premultiplied alpha) is converted to `Bgra32` (non-premultiplied) before storage, matching the `WriteableBitmap` format:

```csharp
byte ub = (byte)((pb * 255 + pa / 2) / pa);  // un-premultiply with rounding
```

Cache key encoding packs `charIndex` and `colorARGB` into a single `long` for O(1) lookup with no allocation:

```csharp
long key = ((long)(byte)c << 32) | colorArgb;
```

Colors in the hot path are stored as packed `uint` (`0xAARRGGBB`) — no `Brush` objects, no heap traffic.

**Warmup:** On file load, `WarmupGlyphCache()` pre-rasterizes the full hex character set (`0-9`, `A-F`) in all active colors as a background `Task`, eliminating first-paint stutter.

---

### 4. Dirty Tracking System

Every write operation registers the exact file offset in `_modifiedOffsets`. The renderer never reads `_modifiedOffsets` directly — it reads `_modifiedSnapshot`, an immutable `HashSet<long>` published atomically after each write batch:

```csharp
private HashSet<long>          _modifiedOffsets;   // writer side (locked)
private volatile HashSet<long> _modifiedSnapshot;  // reader side (lock-free)
```

This eliminates lock contention between the script engine (writer) and the render loop (reader). The renderer captures the snapshot reference once per frame:

```csharp
var modSnap = _modifiedSnapshot;
// ... iterate over visible bytes, check modSnap.Contains(byteOffset)
```

Modified bytes receive a distinct `_colModifiedBg` fill drawn **before** the glyph, so the overlay is visible behind the text rather than covering it.

**Change Navigation:**

`F3` jumps to the next modified offset in ascending order, wrapping to the minimum when exhausted.

---

### 5. Transactional Undo System

EUVA maintains two stacks:

```csharp
private readonly Stack<(long Offset, byte[] Old, byte[] New)> _undoStack = new();
private readonly Stack<int> _transactionSteps = new();
```

`_undoStack` records every individual byte-level write as a `(offset, oldBytes, newBytes)` tuple. `_transactionSteps` records how many `_undoStack` entries belong to the most recent script execution run.

**Step-by-step undo (`Ctrl+Z`):** Pops one entry from `_undoStack` and restores the old bytes at that offset.

**Session rollback (`Ctrl+Shift+Z`):** Pops the count from `_transactionSteps` and replays that many step-undos, reverting the entire last script run atomically.

Both operations hold `lock(_undoStack)` for thread safety with the script engine.

---

### 6. PE Structural Decomposition (`PEMapper`)

`PEMapper` implements `IBinaryMapper` and produces a fully navigable `BinaryStructure` tree from raw PE binary data. Parsing is delegated to AsmResolver for header extraction; the semantic decomposition, region mapping, and tree construction are native EUVA logic.

**`IBinaryMapper` interface:**

```csharp
public interface IBinaryMapper
{
    BinaryStructure Parse(ReadOnlySpan<byte> data);
    IReadOnlyList<DataRegion> GetRegions();
    DataRegion? FindRegionAt(long offset);
    void RegisterRegionProvider(IRegionProvider provider);
    BinaryStructure? RootStructure { get; }
}
```

The `RegisterRegionProvider(IRegionProvider)` method allows third-party code to inject additional `DataRegion` sets into the mapper without subclassing:

```csharp
public interface IRegionProvider
{
    IEnumerable<DataRegion> ProvideRegions(BinaryStructure structure, ReadOnlySpan<byte> data);
}
```

**Parse Pipeline:**

```
Parse(ReadOnlySpan<byte> data)
    ├── ParseDosHeader()        → IMAGE_DOS_HEADER node + DOS region
    ├── ParseNtHeaders()
    │       ├── ParseFileHeader()       → IMAGE_FILE_HEADER node
    │       └── ParseOptionalHeader()   → IMAGE_OPTIONAL_HEADER node
    ├── ParseSections()         → IMAGE_SECTION_HEADER nodes + Code/Data regions
    └── ParseDataDirectories()  → Import/Export directory nodes
```

**Section Region Coloring:**

```csharp
var color = section.IsContentCode              ? Colors.LightGreen  :
            section.IsContentInitializedData   ? Colors.LightBlue   :
            section.IsContentUninitializedData ? Colors.LightGray    :
                                                 Colors.LightYellow;
```

**Reflection-Based Field Resolution:**

AsmResolver API surfaces vary across versions. `PEMapper` uses multi-candidate reflection probes to resolve properties without hard-coded paths or conditional compilation:

```csharp
var rawPtrVal = GetNestedMemberValue(section,
    "Header.PointerToRawData",  // AsmResolver 5.x
    "PointerToRawData",         // flat property
    "Offset");                  // final fallback
```

**The `DataRegion` Model:**

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

`RegionType` covers the full PE taxonomy: `Header`, `Code`, `Data`, `Import`, `Export`, `Resource`, `Relocation`, `Debug`, `Overlay`, `Signature`, `Unknown`.

---

### 7. Data Inspector — Type Interpretation Engine

The Inspector panel provides simultaneous multi-type interpretation of any selected file offset. All reads pass through the `ReadBytes(offset, Span<byte>)` API — no intermediate copies, no allocation beyond the pool-rented temporary.

**Supported Type Matrix:**

| Type | Size (bytes) | Notes |
|---|---|---|
| `Int8 / UInt8` | 1 | Binary bit-string display included |
| `Int16 / UInt16` | 2 | |
| `DOS Date` | 2 | `year = ((v >> 9) & 0x7F) + 1980` |
| `DOS Time` | 2 | `hour = v >> 11`, `min = (v >> 5) & 0x3F`, `sec = (v & 0x1F) * 2` |
| `Int24 / UInt24` | 3 | Manual byte construction |
| `Int32 / UInt32` | 4 | `BinaryPrimitives.ReadUInt32LittleEndian` |
| `Single (float32)` | 4 | IEEE 754 |
| `time_t (32-bit)` | 4 | Unix epoch |
| `Int64 / UInt64` | 8 | `BinaryPrimitives.ReadUInt64LittleEndian` |
| `Double (float64)` | 8 | IEEE 754 |
| `FILETIME` | 8 | 100-ns intervals since 1601-01-01 |
| `OLETIME` | 8 | COM Automation date |
| `GUID / UUID` | 16 | RFC 4122 format |
| `ULEB128` | Variable | DWARF/WebAssembly, max 10 bytes |
| `SLEB128` | Variable | Signed variant |

Multi-byte reads use `System.Buffers.Binary.BinaryPrimitives` for endian-safe access over `ReadOnlySpan<byte>`.

**Endianness Toggle:**

All multi-byte reads respect the current endian mode (`IsLittleEndian`). Toggling immediately re-parses the current selection.

---

### 8. Signature Scanner (`SignatureScanner`)

A zero-allocation, `ReadOnlySpan<byte>`-native pattern matching engine with wildcard support.

**Pattern Format:** space-delimited hex byte strings. `??` or `?` denotes a wildcard.

```
"55 50 58 30"                          // exact: UPX0 marker
"60 BE ?? ?? ?? ?? 8D BE ?? ?? ?? ??"  // UPX entry stub
```

The outer scan loop slices `ReadOnlySpan<byte>` at each candidate position — no allocations.

**Shannon Entropy Calculator:**

```csharp
public static double CalculateEntropy(ReadOnlySpan<byte> data)
{
    Span<int> frequencies = stackalloc int[256];
    foreach (byte b in data) frequencies[b]++;
    // ...
}
```

Uses `stackalloc` for the 256-bucket frequency table no heap allocation regardless of input size.

Entropy thresholds used by the detector pipeline:

| Threshold | Interpretation |
|---|---|
| `< 5.0` | Uncompressed / sparse data |
| `5.0 – 7.0` | Normal executable code |
| `> 7.0` | Compressed / encrypted (UPX) |
| `> 7.5` | Heavy obfuscation (Themida/WinLicense) |

---

### 9. Detector Pipeline (`DetectorManager`)

The detection subsystem is a priority-ordered, async plugin chain. Each registered `IDetector` receives a `ReadOnlyMemory<byte>` of the full file and the parsed `BinaryStructure` tree, then produces a `DetectionResult` with a confidence score in `[0.0, 1.0]`.

`AnalyzeAsync` accepts an `IProgress<string>?` for UI reporting and returns results sorted by confidence descending:

```csharp
public async Task<List<DetectionResult>> AnalyzeAsync(
    ReadOnlyMemory<byte> fileData,
    BinaryStructure structure,
    IProgress<string>? progress = null)
```

Built-in detectors:

| Detector | Priority | Namespace | Detection Strategy |
|---|---|---|---|
| `ThemidaDetector` | 5 | `EUVA.Core.Detectors.Samples` | Signature scan + section name check + Import Table anomaly + entropy |
| `UPXDetector` | 10 | `EUVA.Core.Detectors.Samples` | Signature scan (`UPX0/1/!`) + entry stub + section names + entropy |
| `FSGDetectorPlugin` | 15 | `EUVA.Plugins.Samples` | 3-version signature scan + section sizing heuristics + import anomaly |

**Confidence Scoring UPX Example:**

| Evidence | Contribution |
|---|---|
| Any UPX signature match | +0.40 |
| Section named `UPX0` or `UPX1` | +0.40 |
| Section named `.UPX0` or `.UPX1` | +0.30 |
| Entropy > 7.0 | +0.20 |
| Total (capped at 1.0) | Max 1.00 |

---

### 10. Theme Engine

EUVA's visual presentation is controlled by `.themes` files. Theme state persists across sessions via `EuvaSettings` (application settings). On startup, `App.xaml.cs` restores the last used theme path; if the file is missing, it falls back to the built-in default with a `ThemeDiagnostics.Warning`.

**`.themes` File Format:**

```themes
# EUVA Color Palette
# Format: TokenName = R , G , B , A

Background         = 18 , 18 , 18 , 255
Hex_Background     = 18 , 18 , 18 , 255
Hex_ByteActive     = 200, 200, 255, 255
Hex_ByteSelected   = 255, 200,   0, 255
Hex_AsciiPrintable = 100, 255, 100, 255
ConsoleError       = 255,  80,  80, 255
```

Parser rules: `#` starts an inline comment; malformed lines are skipped without aborting the file; values outside `[0, 255]` are rejected per-line; every parsed token is injected as both a `Color` and a frozen `SolidColorBrush` into `Application.Current.Resources`.

All color reads in `VirtualizedHexView` go through `ThemeColor(key, fallback)` and are stored as packed `uint` ARGB values for the hot path. `RefreshColorCache()` rebuilds these packed values and triggers a full redraw whenever a theme is applied.

**Token Reference (partial):**

| Token | Default (R,G,B,A) | Used In |
|---|---|---|
| `Background` | `30,30,30,255` | Main window |
| `Hex_Background` | `30,30,30,255` | HexView canvas |
| `Hex_ByteActive` | `173,216,230,255` | Non-null byte text |
| `Hex_ByteNull` | `80,80,80,255` | Zero byte text |
| `Hex_ByteSelected` | `255,255,0,255` | Selected byte |
| `Hex_AsciiPrintable` | `144,238,144,255` | Printable ASCII chars |
| `Hex_AsciiNonPrintable` | `100,100,100,255` | Non-printable chars |
| `PropertyKey` | `156,220,254,255` | Inspector labels |
| `PropertyValue` | `206,145,120,255` | Inspector values |
| `ConsoleError` | `244,71,71,255` | Error log lines |
| `ConsoleSuccess` | `106,153,85,255` | Success log lines |

---

### 11. Hotkey Configuration (`.htk`)

Hotkeys are defined in plain-text `.htk` files and loaded via `HotkeyManager.Load(path)`. The default bindings are applied via `HotkeyManager.LoadDefaults()` at startup if no `.htk` path is configured.

**`.htk` Format:**

```
# EUVA Hotkey Configuration
# Format: Action = Modifier + Key

NavInspector  = Alt + D1
NavSearch     = Alt + D2
NavDetections = Alt + D3
NavProperties = Alt + D4
CopyHex       = Control + C
CopyCArray    = Control + Shift + C
Undo          = Control + Z
FullUndo      = Control + Shift + Z
```

**`EUVAAction` Enum:**

| Action | Default Binding | Effect |
|---|---|---|
| `NavInspector` | `Alt+1` | Switch to Inspector tab |
| `NavSearch` | `Alt+2` | Switch to Search tab |
| `NavDetections` | `Alt+3` | Switch to Detections tab |
| `NavProperties` | `Alt+4` | Switch to Properties tab |
| `CopyHex` | `Ctrl+C` | Copy selection as hex string |
| `CopyCArray` | `Ctrl+Shift+C` | Copy selection as C byte array |
| `CopyPlainText` | `Ctrl+Alt+C` | Copy selection as decoded text |
| `Undo` | `Ctrl+Z` | Undo last byte write |
| `FullUndo` | `Ctrl+Shift+Z` | Revert entire last script run |

Both `.htk` path and active `.themes` path are persisted together in a config file at `AppBaseDir/hotkey.cfg` (two lines: htk path, theme path).

---

## Built-in Assembler (`AsmLogic`)

EUVA includes a proprietary x86 assembler implemented in C# with no external library dependencies. It translates mnemonic strings to raw opcode byte sequences with automatic relative offset calculation.

### Register Table

```csharp
{ "eax",0 }, { "ecx",1 }, { "edx",2 }, { "ebx",3 },
{ "esp",4 }, { "ebp",5 }, { "esi",6 }, { "edi",7 }
```

### Complete Instruction Set Reference

| Mnemonic | Operands | Opcode | Length | Notes |
|---|---|---|---|---|
| `nop` | — | `90` | 1 | No-op |
| `ret` | — | `C3` | 1 | Near return |
| `jmp` | `imm_addr` | `E9 rel32` | 5 | `rel32 = target - (current + 5)` |
| `mov` | `reg, imm32` | `B8+rd imm32` | 5 | Register immediate |
| `add` | `reg, reg` | `01 /r` | 2 | ModRM `0xC0 \| (src<<3) \| dest` |
| `or` | `reg, reg` | `09 /r` | 2 | |
| `and` | `reg, reg` | `21 /r` | 2 | |
| `sub` | `reg, reg` | `29 /r` | 2 | |
| `xor` | `reg, reg` | `31 /r` | 2 | |
| `cmp` | `reg, reg` | `39 /r` | 2 | Flags only |

**`jmp` displacement calculation:**

```
rel32 = target_address - (current_address + 5)

Example: jmp from 0x00401000 to 0x00402000
rel32 = 0x00402000 - 0x00401005 = 0x00000FFB
Encoding: E9 FB 0F 00 00
```

**`mov reg, imm32` opcode assignment:**

`eax`→`B8`, `ecx`→`B9`, `edx`→`BA`, `ebx`→`BB`, `esp`→`BC`, `ebp`→`BD`, `esi`→`BE`, `edi`→`BF`

---

## EUVA Scripting Language (`.euv` DSL)

EUVA scripts are structured text files with `.euv` extension. The script engine parses and executes them on-demand (`F5`) or automatically via `FileSystemWatcher` when the file changes on disk.

### Execution Model

Scripts execute in a two-phase pipeline:

1. **Parse phase**: The engine reads all lines, resolves method declarations, builds `MethodContainer` objects with body line lists and `clink` export declarations.
2. **Execute phase**: `FinalizeMethod()` iterates over each method's body, calling `ExecuteCommand()` per line. Every write is pushed to `_undoStack`; the total step count for the run is pushed to `_transactionSteps` on completion.

### Top-Level Structure

```euv
# Comments use # or //
start;                    # Required: marks begin of executable body

public:
_createMethod(Name) {
    # method body
}

private:
_createMethod(Internal) {
    # body
}

end;                      # Required: execution aborted if missing
```

### Command Reference

#### `find(variable = pattern)`

Scans the binary for the first occurrence of a byte pattern. `??` is a wildcard.

```euv
find(MyFunc = 48 83 EC 28 41 B9 01)
find(Stub   = 60 BE ?? ?? ?? ?? 8D BE ?? ?? ?? ??)
```

#### `set(variable = expression)`

Assigns a computed value. The right-hand side is evaluated by `ParseMath()` supports hex literals, arithmetic, and variable substitution.

```euv
set(PatchBase = MyFunc + 0x10)
set(NewTarget = 0x00405000)
```

#### `<address_expr> : <payload>` Patch Command

The core write instruction. Payload types: assembly mnemonics (via `AsmLogic`), raw hex bytes, or ASCII string literals in double quotes.

```euv
MyFunc       : nop
(MyFunc + 4) : mov eax, 999
(MyFunc + 9) : jmp 0x00402000
0x00401000   : 90 90 90 90
0x00402000   : "Hello"
```

The engine tries assembly first, then string parsing, then raw hex.

#### `check <address> : <bytes>`

Reads bytes at the address and halts the script if they do not match. Used to verify pre-conditions before writing.

```euv
check MyFunc : 48 83 EC 28
```

#### `clink: [symbol1, symbol2, ...]`

Exports local variables from a `public:` method to the global scope under `MethodName.SymbolName`.

```euv
public:
_createMethod(Scanner) {
    find(EntryPoint = 55 8B EC)
    clink: [EntryPoint]
}

_createMethod(Patcher) {
    set(target = Scanner.EntryPoint + 0x20)
    target : nop
}
```

### Address Expression Engine (`ParseMath`)

All address expressions pass through `ParseMath()`: variable substitution (longest-key-first to prevent partial matches), hex literal conversion, and evaluation via `System.Data.DataTable.Compute()`. `.` or `()` resolves to the last-written address.

### Complete `.euv` Example

```euv
start;

public:
_createMethod(Mode) {

    find(MyFunc = 48 83 EC 28 41 B9 01)

    MyFunc       : nop
    (MyFunc + 1) : nop
    (MyFunc + 2) : nop
    (MyFunc + 3) : nop
    (MyFunc + 4) : mov eax, 999

    clink: [MyFunc]
}

end;
```

### Live Watch Mode

`FileSystemWatcher` monitors the active script file. On any change event (400 ms debounce), the engine re-executes the full script. `F5` forces immediate re-execution without waiting for a file event.

---

## Encoding Support

The ASCII panel uses a pre-computed 256-entry lookup table initialized via `Encoding.GetEncoding(codePage)`. The table is regenerated on encoding change and the viewport invalidated immediately.

| Code Page | Encoding |
|---|---|
| 1251 | Windows Cyrillic (default) |
| 28591 | ISO-8859-1 (Latin-1) |
| 1252 | Windows Western |
| 65001 | UTF-8 |

---

## MediaHex Mode

A secondary render mode that streams raw binary files as grayscale ASCII art at 60 FPS using the hex viewport as a canvas. Brightness maps through a 10-character density ramp: `" .:-=+*#%@"`. `0x00` → space (black), `0xFF` → `@` (white).

The engine uses `CompositionTarget.Rendering` for frame delivery, synchronized to the WPF composition clock. Raw frames are read sequentially from a `FileStream` with `FileOptions.SequentialScan`. When the stream is exhausted, playback loops from position 0.

---

## Writing a Detector Plugin

Implement `IDetectorPlugin` (in namespace `EUVA.Plugins`) and distribute as a `.dll`:

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

Drop the compiled `.dll` into the plugins directory. `DetectorManager.LoadFromDirectory()` loads it automatically at startup. Any class implementing `IDetector` or `IDetectorPlugin` is automatically instantiated and registered.

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

## License

EUVA is free software released under the **GNU General Public License v3.0**.

```
GNU GENERAL PUBLIC LICENSE
Version 3, 29 June 2007

Copyright (C) 2026 pumpkin-bit (falker) & EUVA Contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <https://www.gnu.org/licenses/>.

---

EngineUnpacker Visual Analyzer (EUVA)
Professional PE Static Analysis Tool

Educational tool for reverse engineering research.
Use responsibly and in accordance with applicable laws.
```

---

*EUVA — built for researchers who read hex for fun.*
