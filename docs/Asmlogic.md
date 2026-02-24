## Asmlogic

Here we move on to the most complex implementation in the code, where we manually assemble ModR/M bytes. For example, opcodes are operation identifiers for various logical operations. In my code, we work not with memory, but with the processor registers themselves, due to the magic number 0xC0. We also take the source register index, for example, ecx 001 in bytes, and we must shift it 3 positions to the left (001000) so that it occupies the middle part of the byte, and the target register index fits into the last 3 bits.
The result of this is that we assemble real bytes from x86 instructions.


Another equally complex part is calculating relative jumps, where the jump is calculated not from the beginning of the instruction, but from the address of the next instruction.
This way, we tell the processor to jump X bytes forward or backward. Let's take a simple example: if we want to jump from byte 0x100 to byte 0x110, we add 5 because the jump takes 5 bytes. If we want to jump, again, from byte 0x100 to byte 0x110, then our result with 5 added will be 11.

also name resolution where the engine searches first inside the method and then in the global list
Another idea I have is to use it this way: if, say, a signature search fails and crashes with an error, all subsequent commands simply stop working, so as not to accidentally kill a file due to a script search error. Such situations do happen.
Therefore, if the script doesn't find a signature, there's no point in worrying; most likely, no changes will be made, and the script will crash. However, keep in mind that you can always roll back using hotkeys.

So, a few words about the syntax and rules of the language.
A script is a sequence of blocks (for example, _createMethod(Name) { ... } creates a logical block) and certain commands. You could say this:
A script block is surrounded by the start; code start and end; code end operators, and comments of any type // # will be ignored during parsing.
There are public: private access modifiers: a block determines whether its methods will be accessible in the global environment. For example, functions inside a public block can be accessed from other blocks.
But private blocks are not accessible; their functions are not accessible to other blocks.
Public blocks can conditionally inherit certain values.
functions inside a method block are executed immediately after }
There's also math and address calculations inside functions.
The period is the current result.
0x is the prefix for hexadecimal numbers, and () is the grouping of operations.
There's a unique linker that's different from the others in that it connects everything and manages scopes. This is the clink function.
If AddressA is conditionally declared in a public block, then after execution, this variable will be added to the global table.

---

**MainWindow.xaml.cs**

```csharp
    public static class AsmLogic
    {

        private static readonly (string Name, byte Idx)[] RegTable =
        {
            ("eax",0), ("ebp",5), ("ebx",3), ("ecx",1),

            ("edi",7), ("edx",2), ("esi",6), ("esp",4)

        };

        private static readonly (string Mnemonic, byte Op)[] OpsTable =
        {
            ("add",0x01), ("and",0x21), ("cmp",0x39), ("jmp",0xE9),

            ("mov_eax",0xB8), ("or",0x09), ("sub",0x29), ("xor",0x31)
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryFindReg(string name, out byte idx)
        {
            foreach (var (n, i) in RegTable)

                if (n == name) { idx = i; return true; }

            idx = 0; return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryFindOp(string mnemonic, out byte op)
        {
            foreach (var (m, o) in OpsTable)

                if (m == mnemonic) { op = o; return true; }

            op = 0; return false;
        }
        public static byte[]? Assemble(string part, long currentAddr)
        {

            var tokens = part.ToLower().Replace(",", " ")

                            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0) return null;



            string mnemonic = tokens[0];

            if (mnemonic == "nop") return new byte[] { 0x90 };

            if (mnemonic == "ret") return new byte[] { 0xC3 };



            if (mnemonic == "jmp" && tokens.Length == 2 &&

                long.TryParse(tokens[1], out long target))

            {

                int rel = (int)(target - (currentAddr + 5));

                var result = new byte[5];

                result[0] = 0xE9;

                WriteLE32(result, 1, rel);

                return result;

            }

            ---
            if (tokens.Length == 3 && TryFindOp(mnemonic, out byte opCode) &&

                TryFindReg(tokens[1], out byte dest) && TryFindReg(tokens[2], out byte src))

                return new byte[] { opCode, (byte)(0xC0 + (src << 3) + dest) };



            if (mnemonic == "mov" && tokens.Length == 3 &&

                TryFindReg(tokens[1], out byte regIdx) &&

                int.TryParse(tokens[2], out int val))

            {

                var result = new byte[5];

                result[0] = (byte)(0xB8 + regIdx);

                WriteLE32(result, 1, val);

                return result;

            }



            return null;

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        private static void WriteLE32(byte[] buf, int off, int v)

        {

            buf[off] = (byte)v;

            buf[off + 1] = (byte)(v >> 8);

            buf[off + 2] = (byte)(v >> 16);

            buf[off + 3] = (byte)(v >> 24);

        }

    }

    private async Task RunParallelEngine(string scriptPath)
    {
        if (HexView.FileLength == 0) { Log("[Engine] FATAL: No file loaded!", Brushes.Red); return; }

        SafeLog($"[Engine] Starting script: {Path.GetFileName(scriptPath)}", Brushes.White);
        int stepsInThisRun = 0;
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(scriptPath); }
        catch (Exception ex) { Log($"[Engine] IO Error: {ex.Message}", Brushes.Red); return; }

        int totalChanges = 0;
        var globalScope = new Dictionary<string, long>();



        long fileLength = 0;
        await Dispatcher.InvokeAsync(() => fileLength = HexView.FileLength);

        await Task.Run(() =>
        {
            try
            {
                long lastAddress = 0;
                string currentModifier = "default";
                MethodContainer? currentMethod = null;
                bool inScriptBody = false, isTerminated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = _whitespaceRegex
                        .Replace(lines[i].Split('#')[0].Split("//")[0], " ").Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.ToLower() == "start;") { inScriptBody = true; continue; }
                    if (!inScriptBody) continue;
                    if (line.ToLower() == "end;") { isTerminated = true; break; }

                    if (line.EndsWith(":"))
                    {
                        var mod = line.Replace(":", "").ToLower();
                        if (mod == "public" || mod == "private")
                        { currentModifier = mod; continue; }
                    }

                    if (line.StartsWith("_createMethod"))
                    {
                        var mName = Regex.Match(line, @"\((.*?)\)").Groups[1].Value;
                        currentMethod = new MethodContainer { Name = mName, Access = currentModifier };
                        SafeLog($"[Engine] Parsing method: {mName} ({currentModifier})", Brushes.Gray);
                        continue;
                    }

                    if (currentMethod != null)
                    {
                        if (line == "{") continue;
                        if (line == "}")
                        {
                            var localScope = new Dictionary<string, long>();
                            SafeLog($"[Engine] Executing method: {currentMethod.Name}",
                                Brushes.CornflowerBlue);

                            foreach (var cmd in currentMethod.Body)
                                ExecuteCommand(cmd, localScope, globalScope,
                                    ref lastAddress, ref totalChanges,
                                    ref stepsInThisRun, fileLength);

                            if (currentMethod.Access == "public")
                            {
                                foreach (var exportName in currentMethod.Clinks.Keys)
                                {
                                    if (localScope.TryGetValue(exportName, out long addr))
                                    {
                                        globalScope[$"{currentMethod.Name}.{exportName}"] = addr;
                                        SafeLog($"[Link] {currentMethod.Name}.{exportName} -> 0x{addr:X}",
                                            Brushes.Cyan);
                                    }
                                }
                            }
                            currentMethod = null;
                            continue;
                        }

                        if (line.ToLower().StartsWith("clink:") || line.Contains("["))
                        {
                            int j = i; string fullClink = "";
                            while (j < lines.Length && !lines[j].Contains("]"))
                                fullClink += lines[j++];
                            if (j < lines.Length) fullClink += lines[j];
                            var match = _clinkBracketRegex.Match(fullClink);
                            if (match.Success)
                            {
                                var names = match.Groups[1].Value
                                    .Split(new[] { ',', '\r', '\n' },
                                        StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim());
                                foreach (var name in names) currentMethod.Clinks[name] = 0;
                            }
                            i = j; continue;
                        }
                        currentMethod.Body.Add(line);
                    }
                }

                if (stepsInThisRun > 0)
                {
                    lock (_undoStack) { _transactionSteps.Push(stepsInThisRun); }
                    SafeLog($"[Engine] Success. {stepsInThisRun} ops committed.", Brushes.SpringGreen);
                }
                if (!isTerminated) throw new Exception("Script reached EOF without 'end;'");
            }
            catch (Exception ex) { SafeLog($"[fatal error] {ex.Message}", Brushes.OrangeRed); }
        });
    }
    private void ExecuteCommand(string line,
        Dictionary<string, long> localScope, Dictionary<string, long> globalScope,
        ref long lastAddress, ref int totalChanges, ref int stepsInThisRun,
        long currentFileLength)
    {
        string cmd = line.ToLower();
        try
        {
            if (cmd.StartsWith("find"))
            {
                var fp = ExtractInsideBrackets(line).Split('=');
                if (fp.Length < 2) return;
                string varName = fp[0].Trim();
                string sigPat = fp[1].Trim();
                long rawAddr = FindSignature(sigPat);

                if (rawAddr == -1)
                {
                    localScope[varName] = long.MinValue;
                    SafeLogThreadSafe(
                        $"[Search] Signature NOT found: '{sigPat}'. " +
                        $"Variable '{varName}' marked INVALID — all dependent commands will be skipped.",
                        Brushes.Orange);
                }
                else
                {
                    localScope[varName] = rawAddr;
                    SafeLogThreadSafe($"[Search] ✓ {varName} = 0x{rawAddr:X8}", Brushes.Violet);
                }
            }
            else if (cmd.StartsWith("set"))
            {
                var sp = ExtractInsideBrackets(line).Split('=');
                if (sp.Length < 2) return;
                string varName = sp[0].Trim();
                long val = ParseMath(sp[1], lastAddress, localScope, globalScope);
                localScope[varName] = val;

                if (val == long.MinValue)
                    SafeLogThreadSafe(
                        $"[Set] Variable '{varName}' set to INVALID " +
                        $"(expression depends on a missing signature).",
                        Brushes.Orange);
            }
            else
            {
                string addrPart = line.Contains(':')
                    ? line.Split(':')[0]
                    : ExtractInsideBrackets(line).Split(':')[0];

                long addr = ParseMath(addrPart, lastAddress, localScope, globalScope);
                if (addr == long.MinValue)
                {
                    SafeLogThreadSafe(
                        $"[Skip] Command '{line.Split(':')[0].Trim()}' skipped: " +
                        $"address expression depends on an INVALID variable (missing signature).",
                        Brushes.Yellow);
                    return;
                }

                if (addr < 0 || addr >= currentFileLength)
                {
                    SafeLogThreadSafe($"[Skip] Address 0x{addr:X8} out of range " +
                        $"(file size: 0x{currentFileLength:X8}).", Brushes.Yellow);
                    return;
                }

                if (cmd.StartsWith("check"))
                {
                    byte[] expected = ParseBytes(line.Split(':')[1]);
                    for (int i = 0; i < expected.Length; i++)
                        if (HexView.ReadByte(addr + i) != expected[i])
                        {
                            SafeLogThreadSafe($"[Check Fail] 0x{addr:X} mismatch.", Brushes.OrangeRed);
                            return;
                        }
                    return;
                }

                if (line.Contains(':'))
                {
                    string dataPart = line.Split(':')[1].Trim();
                    byte[]? bytes = AsmLogic.Assemble(dataPart, addr);

                    if (bytes == null && dataPart.Contains('"'))
                    {
                        var m = Regex.Match(dataPart, "\"(.*)\"");
                        if (m.Success)
                            bytes = System.Text.Encoding.ASCII.GetBytes(m.Groups[1].Value);
                    }
                    if (bytes == null) bytes = ParseBytes(dataPart);

                    if (bytes is { Length: > 0 })
                    {
                        byte[] oldBytes = new byte[bytes.Length];
                        for (int i = 0; i < bytes.Length; i++)
                            oldBytes[i] = HexView.ReadByte(addr + i);

                        SafeLogThreadSafe(
                            $"[Patch] 0x{addr:X}: {BitConverter.ToString(oldBytes).Replace("-", " ")} -> " +
                            $"{BitConverter.ToString(bytes).Replace("-", " ")}", Brushes.YellowGreen);

                        lock (_undoStack) { _undoStack.Push((addr, oldBytes, bytes)); stepsInThisRun++; }

                        var captured = bytes;
                        Dispatcher.Invoke(() =>
                        {
                            for (int i = 0; i < captured.Length; i++)
                                HexView.WriteByte(addr + i, captured[i]);
                        });

                        totalChanges += bytes.Length;
                        lastAddress = addr + bytes.Length;

                        Dispatcher.BeginInvoke(new Action(() => HexView.InvalidateVisual()),
                            System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SafeLogThreadSafe($"[Cmd Error] '{line}': {ex.Message}", Brushes.Red);
        }
    }
    private static long ParseMath(string expr, long lastAddr,
        Dictionary<string, long> localScope, Dictionary<string, long> globalScope)
    {
        ReadOnlySpan<char> src = expr.AsSpan().Trim();
        if (src.Length == 0 || src is "." or "()") return lastAddr;
        char[] buf = ArrayPool<char>.Shared.Rent(src.Length * 21 + 64);
        int outLen = 0;
        try
        {
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < src.Length &&
                           (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '.'))
                        i++;
                    string token = src.Slice(start, i - start).ToString();

                    long val = localScope.TryGetValue(token, out long lv) ? lv
                             : globalScope.TryGetValue(token, out long gv) ? gv
                             : 0L;
                    if (val == long.MinValue) return long.MinValue;

                    AppendLong(buf, ref outLen, val);
                    continue;
                }
                if (outLen < buf.Length) buf[outLen++] = c;
                i++;
            }
            return EvalExpr(buf.AsSpan(0, outLen));
        }
        catch { return long.MinValue; }
        finally { ArrayPool<char>.Shared.Return(buf); }
    }
```