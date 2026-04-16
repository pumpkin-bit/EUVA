# EUVA MCP Server

Integration of the EUVA decompiler with LLMs via the Model Context Protocol.  
The MCP server exposes a set of tools for reading, analyzing, and modifying decompiled code directly from an AI assistant.

---

## Installation

```bash
cd YOUR_PATH\EUVA\Scripts\mcp_server
pip install -r requirements.txt
```

## Configuration

> [!NOTE]
> a simple example, in fact, you can use not only Claude, but any environment where MCP support is integrated 
The configuration file in other environments may differ from what is in this documentation. Keep in mind that you should read how to configure the MCP server configuration based on your environment in their official documentation.

Copy the contents below into your MCP client configuration file.  
For Claude Desktop: `YOUR_PATH\\claude_desktop_config.json`

```json
{
    "mcpServers": {
        "euva": {
            "command": "C:YOUR_PATH\\python.exe",
            "args": [
                "C:YOUR_PATH\\EUVA\\Scripts\\mcp_server\\euva_mcp.py"
            ],
            "env": {
                "PYTHONPATH": "C:YOUR_PATH\\EUVA\\Scripts\\mcp_server"
            }
        }
    }
}
```

> Replace `YOUR_PATH` with the actual paths on your system.

Optionally pass `--workspace` to specify the dumps directory:
```json
"args": ["...\\euva_mcp.py", "--workspace", "C:\\path\\to\\EUVA.UI\\bin\\Debug\\net8.0-windows"]
```

---

## Tools

| Tool | Description |
|---|---|
| `list_dumps` | Returns a list of all decompiled function `.dump` files in the workspace |
| `read_dump` | Reads a dump file and returns its content along with metadata (address, line count) |
| `modify_dump` | Modifies a dump file in-place. Supports actions: `COMMENT`, `REMOVE_COMMENT`, `EDIT_COMMENT`, `RENAME`, `RENAME_SCOPED`, `REPLACE_LINE`, `RENAME_LABEL`, `INSERT_LINE`, `DELETE_LINE` |
| `batch_modify` | Apply multiple `modify_dump` actions in a single call. Read once, apply all, write once |
| `search_pattern` | Searches for a string or regex pattern inside a dump. Returns matching line numbers and text |
| `batch_search` | Search for multiple patterns in one call. Returns a dict mapping each pattern to its matches |
| `get_function_summary` | Structural analysis of a function: API calls, string literals, local variables, control flow, MCP annotations |
| `xref_symbol` | Cross-references a symbol across all dump files. Finds every occurrence of a variable/function/constant |
| `batch_rename` | Bulk-renames variables via a JSON dictionary. Supports optional `start_line`/`end_line` for scoped renames |

---

## Usage Examples

**Rename a variable:**
> *"Rename spill_14 to is_error in func_xxxx.dump"*

**Analyze a function:**
> *"Give me a summary of func_xxxx.dump — what APIs does it call?"*

**Cross-references:**
> *"Where else is g_xxxx used?"*

**Bulk refactoring:**
> *"Rename: spill_14-is_error, spill_1C-hFile, sub_5A94-CheckUAC"*

---

*MCP Model Context Protocol is an open standard developed by [Anthropic](https://anthropic.com).*
