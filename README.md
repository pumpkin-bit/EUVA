# EUVA — EngineUnpacker Visual Analyzer

> **Developing Byte and Signature Research Program.**  
> Zero bloat. Zero vendor lock-in. Maximum signal.


[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0) ![Static Analysis](https://img.shields.io/badge/Analysis-Static-4B0082?style=plastic&logo=linux-foundation&logoColor=white) ![Hex](https://img.shields.io/badge/Data-Hex_Manipulation-696969?style=plastic&logo=data-studio&logoColor=white) ![Low Latency](https://img.shields.io/badge/Perf-Low_Latency-FFD700?style=plastic&logo=speedtest&logoColor=black)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-informational)](https://github.com/euva) [![Framework](https://img.shields.io/badge/.NET-8.0--windows-purple)](https://dotnet.microsoft.com) [![Language](https://img.shields.io/badge/Language-C%23%2012.0-brightgreen)](https://learn.microsoft.com/en-us/dotnet/csharp/) ![WPF](https://img.shields.io/badge/UI-WPF-blue?style=plastic&logo=windows&logoColor=white) ![Memory](https://img.shields.io/badge/Memory-Mapped%20Files-lightgrey?style=plastic&logo=speedtest&logoColor=white)

---

## Introduction

Most likely, this program answers the question: *what if hex editors were written from scratch in 2026?*

EUVA is a **WPF/C# native application** that operates directly on the binary layer. No heavy runtime frameworks. No scripting interpreters bolted on as afterthoughts. No 200-MB installs for features you'll never use.

---

## Disclaimer 

**This program is under active development. Experimental builds may contain bugs or lead to unexpected behavior. Use with caution.**

This software is provided "as is", without warranty of any kind. EUVA is a high-precision instrument designed for educational purposes and security research. The author is not responsible for any system instability, data loss, or legal consequences resulting from the misuse of this tool.

By using EUVA, you acknowledge that you are solely responsible for your actions, you understand the risks of modifying binary files and process memory, and you will respect the laws and regulations of your jurisdiction.

---

## Key Features

- **Memory-Mapped File engine** that scales to arbitrarily large binaries with zero heap pressure
- **WriteableBitmap renderer** that bypasses the WPF render pipeline entirely pixel-perfect output at native DPI
- **GlyphCache subsystem** that rasterizes each character once and blits it via direct memory copy thereafter
- **Dirty Tracking system** with lock-free snapshot reads for zero-latency change visualization
- **Transactional Undo system** both step-by-step (`Ctrl+Z`) and full-session rollback (`Ctrl+Shift+Z`)
- **structured PE decomposition layer** that turns raw bytes into a navigable semantic tree
- **built-in x86 assembler** that compiles instructions to opcodes inline with automatic relative offset resolution
- **scriptable patching DSL** (`.euv` format) with live file-watch execution
- **plugin-extensible detector pipeline** for packer/protector identification
- **fully themeable rendering layer** with persistent theme state across sessions
- **Addition of the Yara-X rules engine** which allows for matching against thousands of pre-built rules for binary file analysis.
- **Byte minimap** Allows you to instantly scan the hex grid of a binary file, simplifying research and instantly identifying where packed code or similar may be located.
- **Disassembler** An iced-based disassembler will help in analyzing binary files and will present the binary file as readable logic.
- **Decompiler** Decompile x64, x86 binaries and get pseudocode in C/C++ format
- **Scripting Decompiler** A C# scripting layer that allows you to write custom decompiler scripts and custom decompilation methods.

---

## Implementation
To find out how a particular subsystem works, you can read the relevant documents.

- [Memory-Mapped-File](docs/MemoryMappedFile.md)
- [WriteableBitmap Render](docs/WriteableBitmapRenderer.md)
- [GlyphCache](docs/GlyphCache.md)
- [Dirty-Tracking-System](docs/DirtyTrackingAndSnapshot.md)
- [Transactional-Undo-system](docs/UndoSystem.md)
- [Structured-PE-decomposition-layer](docs/PESemanticTree.md)
- [built-in-x86-assembler](docs/Asmlogic.md)
- [scriptable-patching-DSL](docs/EuvFIleWatch.md)
- [plugin-extensible-detector-pipeline](docs/Detectors.md)
- [fully-themeable-rendering-layer](docs/Themes.md)
- [Addition-of-the-Yara-X-rules-engine](docs/EuvaUseYaraX.md)
- [Byte-minimap](docs/byteminimap.md)
- [Disassembler](docs/Disassembler.md)
- [Decompiler](docs/Decompiler.md)
- [Scripting-Decompiler](docs/Decompiler#19-glass-engine-c-scripting-integration)
- [AI-Agents-Decompiler](docs/Decompiler#20-ai-assisted-semantic-integration)
---

## Quick Start

**Requirements**

- .NET 8.0 SDK 
- С# ^12.0 compiler
- AsmResolver >= 5.5.1
- DefenceTechSecurity.Yarax Release 1.0.1-release.yrx1.12.0
- Iced Disassembler >= 1.21.0
- Microsoft Msagl >= 1.1.6
- Microsoft.CodeAnalysis.CSharp (Roslyn) >= 5.3.0


```
git clone https://github.com/pumpkin-bit/EUVA.git
cd EUVA/EUVA.UI

dotnet build -c Release
dotnet run // optional
```

**Native AOT version**

> [!WARNING]
> The tool doesn't deny support for Native AOT, but for user convenience, it includes a Roslyn JIT compiler for writing custom decompilation scripts. However, even if Roslyn doesn't support Native AOT, you can still compile the project into a native version.

---

**Hotkeys Default**



- `NavInspector` - `Alt+1`        Switch to Inspector tab 
- `NavSearch` - `Alt+2`           Switch to Search tab 
- `NavDetections` - `Alt+3`       Switch to Detections tab 
- `NavProperties` - `Alt+4`       Switch to Properties tab

- `CopyHex` - `Ctrl+C`            Copy selection as hex string 
- `CopyCArray` - `Ctrl+Shift+C`   Copy selection as C byte array 
- `CopyPlainText` - `Ctrl+Alt+C`  Copy selection as Plain text 

- `Undo` - `Ctrl+Z`               Undo last byte write 
- `FullUndo` - `Ctrl+Shift+Z`     Revert entire last script run 
- `View byte` - `F3`              View the latest bytes changes
- `View Yara Matches` - `Shift+F3` View matches found by Yara
- `View Disassembler` - `Ctrl+D` View Disassembler
- `View Decompiler` - `Ctrl+E` View Decompiler, use `F5` to switch between graphics mode and text mode

**You can reassign hotkeys by loading (via the settings in the program menu) and editing the .htk file.**

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
