// SPDX-License-Identifier: GPL-3.0-or-later


using AsmResolver.PE;
using AsmResolver.PE.File;
using AsmResolver.PE.File.Headers;
using EUVA.Core.Interfaces;
using EUVA.Core.Models;
using System.Text;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace EUVA.Core.Parsers;




public class PEMapper : IBinaryMapper
{
    private readonly List<DataRegion> _regions = new();
    private readonly List<IRegionProvider> _providers = new();
    private BinaryStructure? _rootStructure;
    private byte[] _fileData = Array.Empty<byte>();

    public BinaryStructure? RootStructure => _rootStructure;

    public BinaryStructure Parse(ReadOnlySpan<byte> data)
    {
        _fileData = data.ToArray();
        _regions.Clear();

        var root = new BinaryStructure
        {
            Name = "PE File",
            Type = "Root",
            Offset = 0,
            Size = data.Length
        };

        try
        {
            var peFile = PEFile.FromBytes(_fileData);
            
            
            ParseDosHeader(root, peFile);
            
            
            ParseNtHeaders(root, peFile);
            
            
            ParseSections(root, peFile);
            
            
            ParseDataDirectories(root, peFile);

            
            foreach (var provider in _providers)
            {
                _regions.AddRange(provider.ProvideRegions(root, data));
            }
        }
        catch (Exception ex)
        {
            root.AddChild(new BinaryStructure
            {
                Name = "Parse Error",
                Type = "Error",
                Value = ex.Message
            });
        }

        _rootStructure = root;
        return root;
    }

    private void ParseDosHeader(BinaryStructure root, PEFile peFile)
    {
        var dosNode = new BinaryStructure
        {
            Name = "DOS Header",
            Type = "IMAGE_DOS_HEADER",
            Offset = 0,
            Size = 64
        };

        var dosHeader = peFile.DosHeader;

        var magicVal = GetNestedMemberValue(dosHeader, "Signature", "Magic");
        var lastPageVal = GetNestedMemberValue(dosHeader, "LastPageByteCount", "BytesOnLastPage");
        var pageCountVal = GetNestedMemberValue(dosHeader, "PageCount", "PagesInFile");

        AddField(dosNode, "e_magic", 0x00, 2, magicVal ?? 0, magicVal != null ? $"0x{Convert.ToUInt32(magicVal):X4} (MZ)" : null);
        AddField(dosNode, "e_cblp", 0x02, 2, lastPageVal ?? 0);
        AddField(dosNode, "e_cp", 0x04, 2, pageCountVal ?? 0);
        AddField(dosNode, "e_lfanew", 0x3C, 4, dosHeader.NextHeaderOffset, $"0x{dosHeader.NextHeaderOffset:X8}");

        CreateRegion("DOS Header", 0, 64, RegionType.Header, Colors.DarkSlateBlue, dosNode);
        root.AddChild(dosNode);
    }

    private void ParseNtHeaders(BinaryStructure root, PEFile peFile)
    {
        var ntNode = new BinaryStructure
        {
            Name = "NT Headers",
            Type = "IMAGE_NT_HEADERS",
            Offset = peFile.DosHeader.NextHeaderOffset,
            Size = 248
        };

        var fileHeader = peFile.FileHeader;
        var optionalHeader = peFile.OptionalHeader;

        
        var fileHeaderNode = new BinaryStructure
        {
            Name = "File Header",
            Type = "IMAGE_FILE_HEADER",
            Offset = peFile.DosHeader.NextHeaderOffset + 4,
            Size = 20
        };

        AddField(fileHeaderNode, "Machine", 0, 2, fileHeader.Machine, fileHeader.Machine.ToString());
        AddField(fileHeaderNode, "NumberOfSections", 2, 2, fileHeader.NumberOfSections);
        AddField(fileHeaderNode, "TimeDateStamp", 4, 4, fileHeader.TimeDateStamp, 
            DateTimeOffset.FromUnixTimeSeconds(fileHeader.TimeDateStamp).ToString());
        AddField(fileHeaderNode, "Characteristics", 16, 2, fileHeader.Characteristics, 
            fileHeader.Characteristics.ToString());

        ntNode.AddChild(fileHeaderNode);

        
        var optHeaderNode = new BinaryStructure
        {
            Name = "Optional Header",
            Type = "IMAGE_OPTIONAL_HEADER",
            Offset = peFile.DosHeader.NextHeaderOffset + 24,
            Size = (long)fileHeader.SizeOfOptionalHeader
        };

        AddField(optHeaderNode, "Magic", 0, 2, optionalHeader.Magic, optionalHeader.Magic.ToString());
        AddField(optHeaderNode, "AddressOfEntryPoint", 16, 4, optionalHeader.AddressOfEntryPoint,
            $"0x{optionalHeader.AddressOfEntryPoint:X8}");
        
        var imageBaseVal = GetNestedMemberValue(optionalHeader, "ImageBase");
        int imageBaseSize = (imageBaseVal is ulong) ? 8 : 4;
        var imageBaseNumeric = Convert.ToUInt64(imageBaseVal ?? 0UL);
        AddField(optHeaderNode, "ImageBase", 24, imageBaseSize,
            imageBaseNumeric, $"0x{imageBaseNumeric:X}");
        AddField(optHeaderNode, "SectionAlignment", 32, 4, optionalHeader.SectionAlignment,
            $"0x{optionalHeader.SectionAlignment:X}");
        AddField(optHeaderNode, "FileAlignment", 36, 4, optionalHeader.FileAlignment,
            $"0x{optionalHeader.FileAlignment:X}");
        AddField(optHeaderNode, "SizeOfImage", 56, 4, optionalHeader.SizeOfImage,
            $"0x{optionalHeader.SizeOfImage:X}");
        AddField(optHeaderNode, "SizeOfHeaders", 60, 4, optionalHeader.SizeOfHeaders,
            $"0x{optionalHeader.SizeOfHeaders:X}");

        ntNode.AddChild(optHeaderNode);

        CreateRegion("NT Headers", (long)ntNode.Offset!, (long)ntNode.Size!, 
            RegionType.Header, Colors.DarkBlue, ntNode);
        root.AddChild(ntNode);
    }

    private void ParseSections(BinaryStructure root, PEFile peFile)
    {
        var sectionsNode = new BinaryStructure
        {
            Name = "Sections",
            Type = "SectionTable",
            Offset = peFile.DosHeader.NextHeaderOffset + 24 + peFile.FileHeader.SizeOfOptionalHeader,
            Size = peFile.FileHeader.NumberOfSections * 40
        };

        foreach (var section in peFile.Sections)
        {
            var nameObj = GetNestedMemberValue(section, "Name") ?? section.Name;
            string secName;
            if (nameObj is byte[] nameBytes)
                secName = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
            else
                secName = nameObj?.ToString() ?? string.Empty;

            var rawPtrVal = GetNestedMemberValue(section, "Header.PointerToRawData", "PointerToRawData") ?? GetNestedMemberValue(section, "PointerToRawData") ?? GetNestedMemberValue(section, "Offset");

            var sectionNode = new BinaryStructure
            {
                Name = secName,
                Type = "IMAGE_SECTION_HEADER",
                Offset = Convert.ToInt64(rawPtrVal ?? 0),
                Size = (long)section.GetPhysicalSize()
            };

            var virtualSizeVal = GetNestedMemberValue(section, "Header.VirtualSize", "VirtualSize");
            AddField(sectionNode, "VirtualSize", 0, 4, virtualSizeVal ?? 0, $"0x{Convert.ToUInt32(virtualSizeVal ?? 0):X}");
            AddField(sectionNode, "VirtualAddress", 4, 4, section.Rva, $"0x{section.Rva:X8}");
            var sizeOfRawDataVal = GetNestedMemberValue(section, "SizeOfRawData");
            AddField(sectionNode, "SizeOfRawData", 8, 4, sizeOfRawDataVal ?? 0, $"0x{Convert.ToUInt32(sizeOfRawDataVal ?? 0):X}");
            var ptrRawVal = GetNestedMemberValue(section, "Header.PointerToRawData", "PointerToRawData");
            AddField(sectionNode, "PointerToRawData", 12, 4, ptrRawVal ?? 0, $"0x{Convert.ToUInt32(ptrRawVal ?? 0):X}");
            AddField(sectionNode, "Characteristics", 36, 4, section.Characteristics, section.Characteristics.ToString());

            var color = section.IsContentCode ? Colors.LightGreen :
                       section.IsContentInitializedData ? Colors.LightBlue :
                       section.IsContentUninitializedData ? Colors.LightGray : Colors.LightYellow;

            CreateRegion($"Section: {sectionNode.Name}", sectionNode.Offset ?? 0, (long)section.GetPhysicalSize(), 
                RegionType.Code, color, sectionNode);

            sectionsNode.AddChild(sectionNode);
        }

        root.AddChild(sectionsNode);
    }

    private void ParseDataDirectories(BinaryStructure root, PEFile peFile)
    {
        var dirNode = new BinaryStructure
        {
            Name = "Data Directories",
            Type = "DataDirectories"
        };

        var optHeader = peFile.OptionalHeader;

        var dirs = optHeader.DataDirectories;
        if (dirs != null)
        {
            
            if (dirs.Count > 1)
            {
                var importDir = dirs[1];
                if (importDir.VirtualAddress != 0 || importDir.Size != 0)
                {
                    var importNode = new BinaryStructure
                    {
                        Name = "Import Directory",
                        Type = "IMAGE_IMPORT_DESCRIPTOR"
                    };
                    AddField(importNode, "RVA", 0, 4, importDir.VirtualAddress,
                        $"0x{importDir.VirtualAddress:X8}");
                    AddField(importNode, "Size", 4, 4, importDir.Size,
                        $"0x{importDir.Size:X}");
                    dirNode.AddChild(importNode);
                }
            }

            if (dirs.Count > 0)
            {
                var exportDir = dirs[0];
                if (exportDir.VirtualAddress != 0 || exportDir.Size != 0)
                {
                    var exportNode = new BinaryStructure
                    {
                        Name = "Export Directory",
                        Type = "IMAGE_EXPORT_DESCRIPTOR"
                    };
                    AddField(exportNode, "RVA", 0, 4, exportDir.VirtualAddress,
                        $"0x{exportDir.VirtualAddress:X8}");
                    AddField(exportNode, "Size", 4, 4, exportDir.Size,
                        $"0x{exportDir.Size:X}");
                    dirNode.AddChild(exportNode);
                }
            }
        }

        root.AddChild(dirNode);
    }

    private void AddField(BinaryStructure parent, string name, long relativeOffset, int size, 
        object value, string? displayValue = null)
    {
        var field = new BinaryStructure
        {
            Name = name,
            Type = "Field",
            Offset = parent.Offset + relativeOffset,
            Size = size,
            Value = value,
            DisplayValue = displayValue ?? value.ToString()
        };

        parent.AddChild(field);
    }

    private void CreateRegion(string name, long offset, long size, RegionType type, 
        Color color, BinaryStructure? linkedStructure = null)
    {
        _regions.Add(new DataRegion
        {
            Name = name,
            Offset = offset,
            Size = size,
            Type = type,
            HighlightColor = color,
            Layer = 0,
            LinkedStructure = linkedStructure
        });
    }

    public IReadOnlyList<DataRegion> GetRegions() => _regions;

    public DataRegion? FindRegionAt(long offset)
    {
        return _regions
            .Where(r => r.Contains(offset))
            .OrderByDescending(r => r.Layer)
            .FirstOrDefault();
    }

    public void RegisterRegionProvider(IRegionProvider provider)
    {
        _providers.Add(provider);
    }

    private static object? GetNestedMemberValue(object? obj, params string[] names)
    {
        if (obj == null) return null;
        foreach (var name in names)
        {
            object? current = obj;
            var parts = name.Split('.');
            bool found = true;
            foreach (var part in parts)
            {
                if (current == null) { found = false; break; }
                var t = current.GetType();
                var prop = t.GetProperty(part);
                if (prop != null)
                {
                    current = prop.GetValue(current);
                    continue;
                }
                var field = t.GetField(part);
                if (field != null)
                {
                    current = field.GetValue(current);
                    continue;
                }
                found = false;
                break;
            }
            if (found) return current;
        }
        return null;
    }
}
