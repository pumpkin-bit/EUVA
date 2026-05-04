// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EUVA.UI.Parsers
{
    public class PeIatParser
    {
        public Dictionary<ulong, string> ParsedImports { get; } = new Dictionary<ulong, string>();
        public Dictionary<long, string> StringCache { get; } = new Dictionary<long, string>();

        private List<(uint Rva, byte[] Data)> _dataSections = new();
        private ulong _baseAddress;

        private struct SectionHeader
        {
            public string Name;
            public uint VirtualAddress;
            public uint SizeOfRawData;
            public uint PointerToRawData;
        }

        public bool Parse(Stream stream, ulong imageBase)
        {
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                
                reader.BaseStream.Position = 0x3C; 
                uint peOffset = reader.ReadUInt32();
                
                reader.BaseStream.Position = peOffset;
                uint peSignature = reader.ReadUInt32();
                if (peSignature != 0x00004550) return false; 

                reader.ReadUInt16(); // machine
                ushort numberOfSections = reader.ReadUInt16();
                reader.BaseStream.Position += 16; 

                ushort magic = reader.ReadUInt16(); 
                bool is64Bit = magic == 0x20B;

                uint dataDirOffset = is64Bit ? 112u : 96u;
                reader.BaseStream.Position += (dataDirOffset - 2); 

                reader.BaseStream.Position += 8; 
                uint importRva = reader.ReadUInt32();
                uint importSize = reader.ReadUInt32();

                if (importRva == 0 || importSize == 0) return true; 

                reader.BaseStream.Position += (14 * 8);

                var sections = new List<SectionHeader>();
                for (int i = 0; i < numberOfSections; i++)
                {
                    var sec = new SectionHeader();
                    sec.Name = Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                    sec.VirtualAddress = reader.ReadUInt32(); 
                    reader.BaseStream.Position -= 4; 
                    reader.BaseStream.Position += 4; 
                    sec.VirtualAddress = reader.ReadUInt32();
                    sec.SizeOfRawData = reader.ReadUInt32();
                    sec.PointerToRawData = reader.ReadUInt32();
                    reader.BaseStream.Position += 16; 
                    sections.Add(sec);
                }

                _baseAddress = imageBase;
                long curPos = reader.BaseStream.Position;
                foreach (var sec in sections)
                {
                    if (sec.SizeOfRawData > 0)
                    {
                        reader.BaseStream.Position = sec.PointerToRawData;
                        byte[] secData = reader.ReadBytes((int)sec.SizeOfRawData);
                        _dataSections.Add((sec.VirtualAddress, secData));
                    }
                }
                reader.BaseStream.Position = curPos;

                uint RvaToOffset(uint rva)
                {
                    foreach (var sec in sections)
                    {
                        if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + sec.SizeOfRawData)
                        {
                            return rva - sec.VirtualAddress + sec.PointerToRawData;
                        }
                    }
                    return rva; 
                }

                uint importOffset = RvaToOffset(importRva);
                reader.BaseStream.Position = importOffset;

                while (true)
                {
                    uint originalFirstThunk = reader.ReadUInt32(); 
                    reader.ReadUInt32(); // timeDateStamp
                    reader.ReadUInt32(); // forwarderChain
                    uint nameRva = reader.ReadUInt32();
                    uint firstThunk = reader.ReadUInt32();

                    if (originalFirstThunk == 0 && nameRva == 0) break;

                    long savedPos = reader.BaseStream.Position;
                    reader.BaseStream.Position = RvaToOffset(nameRva);
                    string dllName = ReadNullTerminatedString(reader).ToLower();

                    if (dllName.EndsWith(".dll")) dllName = dllName.Substring(0, dllName.Length - 4);

                    uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
                    reader.BaseStream.Position = RvaToOffset(thunkRva);

                    int thunkIndex = 0;
                    while (true)
                    {
                        ulong thunkData = is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
                        if (thunkData == 0) break;

                        ulong iatVirtualAddress = imageBase + firstThunk + (ulong)(thunkIndex * (is64Bit ? 8 : 4));

                        bool isOrdinal = is64Bit ? (thunkData & 0x8000000000000000) != 0 : (thunkData & 0x80000000) != 0;

                        if (isOrdinal)
                        {
                            ulong ordinal = thunkData & 0xFFFF;
                            ParsedImports[iatVirtualAddress] = $"{dllName}::Ordinal_{ordinal}";
                        }
                        else
                        {
                            uint funcNameRva = (uint)(thunkData & 0x7FFFFFFF);
                            long posBeforeName = reader.BaseStream.Position;
                            
                            reader.BaseStream.Position = RvaToOffset(funcNameRva) + 2; 
                            string funcName = ReadNullTerminatedString(reader);
                            
                            ParsedImports[iatVirtualAddress] = $"{dllName}::{funcName}";
                            
                            reader.BaseStream.Position = posBeforeName;
                        }
                        thunkIndex++;
                    }

                    reader.BaseStream.Position = savedPos;
                }

                return true;
            }
            catch
            {
                return false; 
            }
        }

        private string ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0) bytes.Add(b);
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public unsafe string ExtractStringAt(ulong address)
        {
            if (StringCache.TryGetValue((long)address, out string? cachedStr) && !string.IsNullOrEmpty(cachedStr))
                return cachedStr;

            if (address < _baseAddress) return string.Empty;

            uint rva = (uint)(address - _baseAddress);

            foreach (var sec in _dataSections)
            {
                if (rva >= sec.Rva && rva < sec.Rva + (uint)sec.Data.Length)
                {
                    uint offset = rva - sec.Rva;
                    int maxLen = Math.Min(512, sec.Data.Length - (int)offset);
                    if (maxLen <= 0) return string.Empty;

                    int asciiLen = 0;
                    while (asciiLen < maxLen && sec.Data[offset + asciiLen] != 0) asciiLen++;
                    
                    bool isAsciiValid = asciiLen >= 3;
                    if (isAsciiValid)
                    {
                        for (int i = 0; i < asciiLen; i++)
                        {
                            byte b = sec.Data[offset + i];
                            if ((b < 32 || b > 126) && b != '\t' && b != '\r' && b != '\n') 
                            { 
                                isAsciiValid = false; 
                                break; 
                            }
                        }
                    }

                    int utf16Len = 0;
                    while (utf16Len + 1 < maxLen && 
                          (sec.Data[offset + utf16Len] != 0 || sec.Data[offset + utf16Len + 1] != 0)) 
                    {
                        utf16Len += 2;
                    }

                    bool isUtf16Valid = utf16Len >= 6; 
                    if (isUtf16Valid)
                    {
                        for (int i = 0; i < utf16Len; i += 2)
                        {
                            byte b1 = sec.Data[offset + i];
                            byte b2 = sec.Data[offset + i + 1];
                            if (b2 != 0 || (b1 < 32 || b1 > 126) && b1 != '\t' && b1 != '\r' && b1 != '\n') 
                            { 
                                isUtf16Valid = false; 
                                break; 
                            }
                        }
                    }

                    if (isUtf16Valid)
                    {
                        string result = System.Text.Encoding.Unicode.GetString(sec.Data, (int)offset, utf16Len);
                        StringCache[(long)address] = result;
                        return result;
                    }
                    if (isAsciiValid)
                    {
                        string result = System.Text.Encoding.ASCII.GetString(sec.Data, (int)offset, asciiLen);
                        StringCache[(long)address] = result;
                        return result;
                    }

                    return string.Empty; 
                }
            }

            return string.Empty; 
        }
    }
}