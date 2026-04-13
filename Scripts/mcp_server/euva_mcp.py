import sys
import os
import glob
import json
import re
import argparse
from typing import List, Dict, Any, Optional
from mcp.server.fastmcp import FastMCP

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_WORKSPACE = os.path.join(SCRIPT_DIR, "..", "..", "EUVA.UI", "bin", "Debug", "net8.0-windows")
parser = argparse.ArgumentParser(description="EUVA Python MCP Server")
parser.add_argument("--workspace", type=str, default=DEFAULT_WORKSPACE,
                    help="Path to the EUVA workspace (directory containing 'Dumps' and 'Robots')")
args, _ = parser.parse_known_args()
WORKSPACE_DIR = os.path.abspath(args.workspace)
DUMPS_DIR = os.path.join(WORKSPACE_DIR, "Dumps")

mcp = FastMCP("EUVA", dependencies=["pydantic"])

def _read_dump_lines(dump_filename: str):
    path = os.path.join(DUMPS_DIR, dump_filename)
    if not os.path.exists(path):
        return None, path
    with open(path, "r", encoding="utf-8") as f:
        return f.readlines(), path

def _write_dump(path: str, lines: list):
    with open(path, "w", encoding="utf-8") as f:
        f.writelines(lines)

@mcp.tool()
def list_dumps() -> List[str]:
    if not os.path.exists(DUMPS_DIR):
        return []
    dumps = glob.glob(os.path.join(DUMPS_DIR, "*.dump"))
    return [os.path.basename(d) for d in dumps]

@mcp.tool()
def read_dump(filename: str) -> Dict[str, Any]:
    lines, path = _read_dump_lines(filename)
    if lines is None:
        return {"Error": f"Dump file {filename} not found."}
    address = filename.replace("func_", "").replace(".dump", "")
    return {
        "Address": address,
        "File": filename,
        "LineCount": len(lines),
        "Content": "".join(lines)
    }

@mcp.tool()
def modify_dump(dump_filename: str, action: str, target: str, context: str) -> str:
    lines, path = _read_dump_lines(dump_filename)
    if lines is None:
        return f"Error: Dump file {dump_filename} not found."

    act = action.upper()

    try:
        if act == "COMMENT":
            idx = int(target) - 1
            if not (0 <= idx < len(lines)):
                return f"Error: Line {target} out of range (1..{len(lines)})."
            lines[idx] = lines[idx].rstrip() + f" // [MCP] {context}\n"
            _write_dump(path, lines)
            return f"OK: Added comment at line {target}."

        elif act == "REMOVE_COMMENT":
            idx = int(target) - 1
            if not (0 <= idx < len(lines)):
                return f"Error: Line {target} out of range."
            lines[idx] = re.sub(r'\s*//\s*\[MCP\].*$', '', lines[idx]).rstrip() + "\n"
            _write_dump(path, lines)
            return f"OK: Removed MCP comment from line {target}."

        elif act == "EDIT_COMMENT":
            idx = int(target) - 1
            if not (0 <= idx < len(lines)):
                return f"Error: Line {target} out of range."
            if "// [MCP]" in lines[idx]:
                lines[idx] = re.sub(r'//\s*\[MCP\].*$', f'// [MCP] {context}', lines[idx])
            else:
                lines[idx] = lines[idx].rstrip() + f" // [MCP] {context}\n"
            _write_dump(path, lines)
            return f"OK: Updated MCP comment at line {target}."

        elif act == "RENAME":
            content = "".join(lines)
            count = len(re.findall(r'\b' + re.escape(target) + r'\b', content))
            content = re.sub(r'\b' + re.escape(target) + r'\b', context, content)
            with open(path, "w", encoding="utf-8") as f:
                f.write(content)
            return f"OK: Renamed '{target}' → '{context}' ({count} occurrences)."

        elif act == "RENAME_LABEL":
            content = "".join(lines)
            pattern = r'\b' + re.escape(target) + r'\b'
            count = len(re.findall(pattern, content))
            content = re.sub(pattern, context, content)
            with open(path, "w", encoding="utf-8") as f:
                f.write(content)
            return f"OK: Renamed label '{target}' → '{context}' ({count} occurrences)."

        elif act == "INSERT_LINE":
            idx = int(target) - 1
            if not (0 <= idx <= len(lines)):
                return f"Error: Line {target} out of range."
            lines.insert(idx, context.rstrip() + "\n")
            _write_dump(path, lines)
            return f"OK: Inserted line before position {target}."

        elif act == "DELETE_LINE":
            idx = int(target) - 1
            if not (0 <= idx < len(lines)):
                return f"Error: Line {target} out of range."
            removed = lines.pop(idx).strip()
            _write_dump(path, lines)
            return f"OK: Deleted line {target}: '{removed[:60]}'"

        else:
            return f"Error: Unknown action '{action}'. Use: COMMENT, REMOVE_COMMENT, EDIT_COMMENT, RENAME, RENAME_LABEL, INSERT_LINE, DELETE_LINE."

    except ValueError:
        return f"Error: 'target' must be a line number for action '{action}'."
    except Exception as e:
        return f"Error: {e}"

@mcp.tool()
def search_pattern(dump_filename: str, pattern: str, is_regex: bool = False) -> List[Dict[str, Any]]:
    lines, path = _read_dump_lines(dump_filename)
    if lines is None:
        return [{"Error": f"File {dump_filename} not found."}]

    results = []
    for i, line in enumerate(lines):
        try:
            match = re.search(pattern, line) if is_regex else (pattern in line)
        except re.error as e:
            return [{"Error": f"Invalid regex: {e}"}]
        if match:
            results.append({"Line": i + 1, "Text": line.strip()})
    return results

@mcp.tool()
def get_function_summary(dump_filename: str) -> Dict[str, Any]:
    lines, path = _read_dump_lines(dump_filename)
    if lines is None:
        return {"Error": f"File {dump_filename} not found."}

    content = "".join(lines)
    api_calls = re.findall(r'\b([A-Z][a-zA-Z]+(?:Ex)?[AW]?)\s*\(', content)
    strings = re.findall(r'"([^"]*)"', content)
    locals_vars = set(re.findall(r'\b(spill_\w+|var_\w+|arg_\w+)\b', content))
    ifs = len(re.findall(r'\bif\s*\(', content))
    whiles = len(re.findall(r'\bwhile\s*\(', content))
    fors = len(re.findall(r'\bfor\s*\(', content))
    mcp_comments = [l.strip() for l in lines if "// [MCP]" in l]

    return {
        "File": dump_filename,
        "TotalLines": len(lines),
        "ApiCalls": list(set(api_calls)),
        "StringLiterals": strings[:20],
        "LocalVariables": sorted(locals_vars),
        "ControlFlow": {"if": ifs, "while": whiles, "for": fors},
        "McpAnnotations": mcp_comments
    }

@mcp.tool()
def xref_symbol(symbol: str) -> List[Dict[str, Any]]:
    if not os.path.exists(DUMPS_DIR):
        return [{"Error": "Dumps directory not found."}]

    results = []
    for dump_file in glob.glob(os.path.join(DUMPS_DIR, "*.dump")):
        fname = os.path.basename(dump_file)
        with open(dump_file, "r", encoding="utf-8") as f:
            for i, line in enumerate(f, 1):
                if re.search(r'\b' + re.escape(symbol) + r'\b', line):
                    results.append({"File": fname, "Line": i, "Text": line.strip()})
    return results

@mcp.tool()
def batch_rename(dump_filename: str, renames: str) -> str:
    lines, path = _read_dump_lines(dump_filename)
    if lines is None:
        return f"Error: File {dump_filename} not found."

    try:
        rename_map = json.loads(renames)
    except json.JSONDecodeError as e:
        return f"Error: Invalid JSON in renames: {e}"

    content = "".join(lines)
    total = 0
    for old, new in rename_map.items():
        count = len(re.findall(r'\b' + re.escape(old) + r'\b', content))
        content = re.sub(r'\b' + re.escape(old) + r'\b', new, content)
        total += count

    with open(path, "w", encoding="utf-8") as f:
        f.write(content)
    return f"OK: Applied {len(rename_map)} renames ({total} total replacements)."

if __name__ == "__main__":
    mcp.run()
