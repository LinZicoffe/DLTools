using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace 烧录上位机程序.Models;

/// <summary>
/// 固件文件解析：纯函数 + 零 Qt/WPF 依赖，可独立单元测试。
///
/// 支持格式：
/// - .axf / .elf → ELF32 Program Headers
/// - .hex        → Intel HEX
/// - .bin        → 起始地址由调用方提供
///
/// 设计责任划分：
/// - 本层：格式合法性 + 文件内地址范围 + 总字节数
/// - UI 层：把 FileParseError 转 InfoBar
/// - J-Link DLL 层：地址是否真的落在芯片 Flash 范围（不在这边维护芯片表）
/// </summary>
public static class FlashFileParser
{
    public const string FormatElf = "elf";
    public const string FormatHex = "hex";
    public const string FormatBin = "bin";

    private static readonly Dictionary<string, string> ExtMap = new()
    {
        [".axf"] = FormatElf,
        [".elf"] = FormatElf,
        [".hex"] = FormatHex,
        [".bin"] = FormatBin,
    };

    #region FlashFileInfo / Symbol / Section / MemorySummary / ElfMeta

    public class FlashFileInfo
    {
        public string Format { get; set; } = "";
        public int AddrStart { get; set; }
        public int AddrEnd { get; set; }
        public int TotalBytes { get; set; }
        public string Notes { get; set; } = "";
    }

    public class Symbol
    {
        public string Name { get; set; } = "";
        public int Address { get; set; }
        public int Size { get; set; }
        public string Type { get; set; } = "";
        public string Bind { get; set; } = "";
        public string Section { get; set; } = "";
    }

    public class Section
    {
        public string Name { get; set; } = "";
        public int Addr { get; set; }
        public int Size { get; set; }
        public string Flags { get; set; } = "";
        public int Align { get; set; }
    }

    public class MemorySummary
    {
        public int Text { get; set; }
        public int Data { get; set; }
        public int Bss { get; set; }
        public int Flash { get; set; }
        public int Ram { get; set; }
    }

    public class ElfMeta
    {
        public int Entry { get; set; }
        public int? InitialSp { get; set; }
        public int? ResetHandler { get; set; }
    }

    #endregion

    #region Format detection & parsing

    public static string DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLower();
        if (!ExtMap.ContainsKey(ext))
            throw new FileParseError($"不支持的文件后缀：{ext ?? "(无后缀)"}");
        return ExtMap[ext];
    }

    public static FlashFileInfo ParseFile(string path, int binStartAddr = 0)
    {
        if (!File.Exists(path))
            throw new FileParseError($"文件不存在：{path}");
        var fmt = DetectFormat(path);
        return fmt switch
        {
            FormatElf => ParseElf(path),
            FormatHex => ParseHex(path),
            _ => ParseBin(path, binStartAddr),
        };
    }

    private static FlashFileInfo ParseElf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            var ident = reader.ReadBytes(16);
            if (ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
                throw new FileParseError("不是有效的 ELF 文件");

            bool is64 = ident[4] == 2;
            bool isLe = ident[5] == 1;
            if (is64)
                throw new FileParseError("不支持 ELF64，请使用 ELF32");

            // ELF32 header
            fs.Position = 0;
            var header = reader.ReadBytes(52);
            ushort eType = ReadUInt16(header, 16, isLe);
            ushort eMachine = ReadUInt16(header, 18, isLe);
            uint eEntry = ReadUInt32(header, 24, isLe);
            uint ePhOff = ReadUInt32(header, 28, isLe);
            ushort ePhEntSize = ReadUInt16(header, 42, isLe);
            ushort ePhNum = ReadUInt16(header, 44, isLe);

            if (ePhNum == 0)
                throw new FileParseError("ELF 中无程序头");

            var loadSegs = new List<(int Addr, int Filesz)>();
            for (int i = 0; i < ePhNum; i++)
            {
                fs.Position = ePhOff + i * ePhEntSize;
                var ph = reader.ReadBytes(32);
                uint pType = ReadUInt32(ph, 0, isLe);
                if (pType != 1) continue; // PT_LOAD
                uint pPaddr = ReadUInt32(ph, 12, isLe);
                uint pFilesz = ReadUInt32(ph, 16, isLe);
                if (pFilesz > 0)
                    loadSegs.Add((checked((int)pPaddr), checked((int)pFilesz)));
            }

            if (loadSegs.Count == 0)
                throw new FileParseError("ELF 中无 LOAD 段");

            int addrStart = loadSegs.Min(s => s.Addr);
            int addrEnd = loadSegs.Max(s => s.Addr + s.Filesz);
            int total = loadSegs.Sum(s => s.Filesz);

            return new FlashFileInfo
            {
                Format = FormatElf,
                AddrStart = addrStart,
                AddrEnd = addrEnd,
                TotalBytes = total,
                Notes = $"{loadSegs.Count} LOAD segment(s)"
            };
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"ELF 解析失败：{e.Message}"); }
    }

    private static FlashFileInfo ParseHex(string path)
    {
        try
        {
            var ih = new IntelHex();
            ih.LoadHex(path);
            if (ih.Count == 0)
                throw new FileParseError("HEX 文件为空");
            return new FlashFileInfo
            {
                Format = FormatHex,
                AddrStart = ih.MinAddr,
                AddrEnd = ih.MaxAddr + 1,
                TotalBytes = ih.Count,
                Notes = $"{ih.MaxAddr - ih.MinAddr + 1} address span"
            };
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"HEX 解析失败：{e.Message}"); }
    }

    private static FlashFileInfo ParseBin(string path, int startAddr)
    {
        int size = (int)new System.IO.FileInfo(path).Length;
        if (size == 0)
            throw new FileParseError("BIN 文件为空");
        return new FlashFileInfo
        {
            Format = FormatBin,
            AddrStart = startAddr,
            AddrEnd = startAddr + size,
            TotalBytes = size,
            Notes = $"raw {size} bytes"
        };
    }

    #endregion

    #region IntelHex conversion

    public static IntelHex ToIntelHex(string path, int binStartAddr = 0)
    {
        if (!File.Exists(path))
            throw new FileParseError($"文件不存在：{path}");
        var fmt = DetectFormat(path);
        return ToIntelHexInternal(path, fmt, binStartAddr);
    }

    private static IntelHex ToIntelHexInternal(string srcPath, string srcFmt, int binStartAddr)
    {
        return srcFmt switch
        {
            FormatElf => ToIntelHexFromElf(srcPath),
            FormatHex => ToIntelHexFromHex(srcPath),
            _ => ToIntelHexFromBin(srcPath, binStartAddr),
        };
    }

    private static IntelHex ToIntelHexFromHex(string path)
    {
        var ih = new IntelHex();
        ih.LoadHex(path);
        return ih;
    }

    private static IntelHex ToIntelHexFromBin(string path, int binStartAddr)
    {
        var ih = new IntelHex();
        var data = File.ReadAllBytes(path);
        ih.FromBytes(data, binStartAddr);
        return ih;
    }

    #endregion

    #region ELF section/meta reading

    public static List<Section> ReadSections(string path)
    {
        const uint SHF_ALLOC = 0x2;
        const uint SHF_WRITE = 0x1;
        const uint SHF_EXEC = 0x4;
        const string SHT_NOBITS = "NOBITS";

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            var ident = reader.ReadBytes(16);
            if (ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
                throw new FileParseError("不是有效的 ELF 文件");
            bool isLe = ident[5] == 1;

            uint eShOff = ReadUInt32(ident, 32, isLe);
            ushort eShEntSize = ReadUInt16(ident, 46, isLe);
            ushort eShNum = ReadUInt16(ident, 48, isLe);
            ushort eShStrNdx = ReadUInt16(ident, 50, isLe);

            // Read section name string table
            fs.Position = eShOff + eShStrNdx * eShEntSize;
            var shStrHeader = reader.ReadBytes(40);
            uint shStrAddr = ReadUInt32(shStrHeader, 16, isLe);
            uint shStrSize = ReadUInt32(shStrHeader, 20, isLe);
            fs.Position = shStrAddr;
            var strTable = reader.ReadBytes((int)shStrSize);

            var sections = new List<Section>();
            for (int i = 0; i < eShNum; i++)
            {
                fs.Position = eShOff + i * eShEntSize;
                var sh = reader.ReadBytes(40);
                uint shName = ReadUInt32(sh, 0, isLe);
                uint shType = ReadUInt32(sh, 4, isLe);
                uint shFlags = ReadUInt32(sh, 8, isLe);
                uint shAddr = ReadUInt32(sh, 12, isLe);
                uint shSize = ReadUInt32(sh, 16, isLe);

                if ((shFlags & SHF_ALLOC) == 0) continue;

                // Get section name
                int nameOff = (int)shName;
                int nameEnd = Array.IndexOf(strTable, (byte)0, nameOff);
                if (nameEnd < 0) nameEnd = strTable.Length;
                string name = System.Text.Encoding.ASCII.GetString(strTable, nameOff, nameEnd - nameOff);

                bool isNobits = shType == 8; // SHT_NOBITS
                string rwx = "R" + ((shFlags & SHF_WRITE) != 0 ? "W" : "-") + ((shFlags & SHF_EXEC) != 0 ? "X" : "-");
                if (isNobits) rwx += " (nobits)";

                sections.Add(new Section
                {
                    Name = string.IsNullOrEmpty(name) ? "(无名)" : name,
                    Addr = (int)shAddr,
                    Size = (int)shSize,
                    Flags = rwx,
                    Align = (int)ReadUInt32(sh, 28, isLe)
                });
            }

            sections.Sort((a, b) => a.Addr.CompareTo(b.Addr));
            return sections;
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"ELF 解析失败：{e.Message}"); }
    }

    public static MemorySummary ReadMemorySummary(string path)
    {
        const uint SHF_ALLOC = 0x2;
        const uint SHF_WRITE = 0x1;
        const uint SHF_EXEC = 0x4;
        const uint SHT_NOBITS = 8;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            var ident = reader.ReadBytes(16);
            bool isLe = ident[5] == 1;

            uint eShOff = ReadUInt32(ident, 32, isLe);
            ushort eShEntSize = ReadUInt16(ident, 46, isLe);
            ushort eShNum = ReadUInt16(ident, 48, isLe);
            ushort eShStrNdx = ReadUInt16(ident, 50, isLe);

            // Read section name string table
            fs.Position = eShOff + eShStrNdx * eShEntSize;
            var shStrHeader = reader.ReadBytes(40);
            uint shStrAddr = ReadUInt32(shStrHeader, 16, isLe);
            uint shStrSize = ReadUInt32(shStrHeader, 20, isLe);
            fs.Position = shStrAddr;
            var strTable = reader.ReadBytes((int)shStrSize);

            int text = 0, data = 0, bss = 0;
            for (int i = 0; i < eShNum; i++)
            {
                fs.Position = eShOff + i * eShEntSize;
                var sh = reader.ReadBytes(40);
                uint shFlags = ReadUInt32(sh, 8, isLe);
                uint shType = ReadUInt32(sh, 4, isLe);
                uint shSize = ReadUInt32(sh, 16, isLe);

                if ((shFlags & SHF_ALLOC) == 0) continue;

                if (shType == SHT_NOBITS)
                    bss += (int)shSize;
                else if ((shFlags & SHF_WRITE) != 0)
                    data += (int)shSize;
                else
                    text += (int)shSize;
            }

            return new MemorySummary
            {
                Text = text,
                Data = data,
                Bss = bss,
                Flash = text + data,
                Ram = data + bss
            };
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"ELF 解析失败：{e.Message}"); }
    }

    public static ElfMeta ReadElfMeta(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            var ident = reader.ReadBytes(16);
            bool isLe = ident[5] == 1;

            uint eEntry = ReadUInt32(ident, 24, isLe);
            uint ePhOff = ReadUInt32(ident, 28, isLe);
            ushort ePhEntSize = ReadUInt16(ident, 42, isLe);
            ushort ePhNum = ReadUInt16(ident, 44, isLe);

            // Find lowest addr LOAD segment
            int lowestAddr = int.MaxValue;
            var loadSegs = new List<(int Addr, int Filesz)>();
            for (int i = 0; i < ePhNum; i++)
            {
                fs.Position = ePhOff + i * ePhEntSize;
                var ph = reader.ReadBytes(32);
                uint pType = ReadUInt32(ph, 0, isLe);
                if (pType != 1) continue;
                uint pPaddr = ReadUInt32(ph, 12, isLe);
                uint pFilesz = ReadUInt32(ph, 16, isLe);
                if (pFilesz >= 8)
                    loadSegs.Add((checked((int)pPaddr), checked((int)pFilesz)));
            }

            int? initialSp = null;
            int? resetHandler = null;
            if (loadSegs.Count > 0)
            {
                var seg = loadSegs.MinBy(s => s.Addr);
                fs.Position = 0;
                var allBytes = reader.ReadBytes((int)new System.IO.FileInfo(path).Length);
                // The segment data starts at the file offset of the segment
                // For simplicity, read from the start of the file (typical for bootloaders)
                if (allBytes.Length >= 8)
                {
                    initialSp = BitConverter.ToInt32(allBytes, 0);
                    resetHandler = BitConverter.ToInt32(allBytes, 4) & ~1;
                }
            }

            return new ElfMeta
            {
                Entry = (int)eEntry,
                InitialSp = initialSp,
                ResetHandler = resetHandler
            };
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"ELF 解析失败：{e.Message}"); }
    }

    public static List<Symbol> ReadSymbols(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            var ident = reader.ReadBytes(16);
            if (ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
                throw new FileParseError("不是有效的 ELF 文件");
            bool isLe = ident[5] == 1;
            bool is64 = ident[4] == 2;
            if (is64) throw new FileParseError("不支持 ELF64");

            uint eShOff = ReadUInt32(ident, 32, isLe);
            ushort eShEntSize = ReadUInt16(ident, 46, isLe);
            ushort eShNum = ReadUInt16(ident, 48, isLe);
            ushort eShStrNdx = ReadUInt16(ident, 50, isLe);

            // Find .symtab section
            int symAddr = -1, symSize = 0, symLink = 0, symInfo = 0;
            int strTabAddr = -1, strTabSize = 0;

            for (int i = 0; i < eShNum; i++)
            {
                fs.Position = eShOff + i * eShEntSize;
                var sh = reader.ReadBytes(40);
                uint shName = ReadUInt32(sh, 0, isLe);
                uint shType = ReadUInt32(sh, 4, isLe);
                uint shFlags = ReadUInt32(sh, 8, isLe);
                uint shAddr = ReadUInt32(sh, 12, isLe);
                uint shSize = ReadUInt32(sh, 16, isLe);
                uint shLink = ReadUInt32(sh, 20, isLe);
                uint shInfo = ReadUInt32(sh, 24, isLe);
                uint shEntSize = ReadUInt32(sh, 36, isLe);

                // Get name
                fs.Position = eShOff + eShStrNdx * eShEntSize;
                var shStrHeader = reader.ReadBytes(40);
                uint shStrBase = ReadUInt32(shStrHeader, 16, isLe);
                fs.Position = shStrBase;
                var shStrTable = reader.ReadBytes((int)ReadUInt32(shStrHeader, 20, isLe));
                int nameOff = (int)shName;
                int nameEnd = Array.IndexOf(shStrTable, (byte)0, nameOff);
                if (nameEnd < 0) nameEnd = shStrTable.Length;
                string name = System.Text.Encoding.ASCII.GetString(shStrTable, nameOff, nameEnd - nameOff);

                if (name == ".symtab")
                {
                    symAddr = (int)shAddr;
                    symSize = (int)shSize;
                    symLink = (int)shLink;
                    symInfo = (int)shInfo;
                }
                else if (name == ".strtab")
                {
                    strTabAddr = (int)shAddr;
                    strTabSize = (int)shSize;
                }
            }

            if (symAddr < 0) return new List<Symbol>();

            if (strTabAddr < 0) return new List<Symbol>();
            fs.Position = strTabAddr;
            var strTable = reader.ReadBytes(strTabSize);

            int symEntSize = 16; // ELF32 symbol size
            int numSymbols = symSize / symEntSize;
            var symbols = new List<Symbol>();

            fs.Position = symAddr;
            for (int i = 0; i < numSymbols; i++)
            {
                var sym = reader.ReadBytes(16);
                int stName = ReadInt32(sym, 0, isLe);
                uint stValue = ReadUInt32(sym, 4, isLe);
                uint stSize = ReadUInt32(sym, 8, isLe);
                byte stInfo = sym[12];
                byte stOther = sym[13];
                ushort stShndx = ReadUInt16(sym, 14, isLe);

                // Get symbol name from strtab
                string symName = "";
                if (stName >= 0 && stName < strTabSize)
                {
                    int nameOff = stName;
                    int nameEnd = Array.IndexOf(strTable, (byte)0, nameOff);
                    if (nameEnd < 0) nameEnd = strTabSize;
                    symName = System.Text.Encoding.ASCII.GetString(strTable, nameOff, nameEnd - nameOff);
                }

                if (string.IsNullOrEmpty(symName)) continue;

                byte type = (byte)((stInfo >> 4) & 0xF);
                byte bind = (byte)(stInfo & 0xF);
                string typeName = type switch
                {
                    1 => "FUNC",
                    2 => "OBJECT",
                    3 => "SECTION",
                    4 => "FILE",
                    _ => "NOTYPE"
                };
                string bindName = bind switch
                {
                    1 => "LOCAL",
                    2 => "GLOBAL",
                    3 => "WEAK",
                    _ => "NOTYPE"
                };
                string sectionName = stShndx == 0 ? "UND" : stShndx == 0xFFF1 ? "ABS" : stShndx == 0xFFF2 ? "COMMON" : "";

                symbols.Add(new Symbol
                {
                    Name = symName,
                    Address = (int)stValue,
                    Size = (int)stSize,
                    Type = typeName,
                    Bind = bindName,
                    Section = sectionName
                });
            }

            return symbols;
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"ELF 解析失败：{e.Message}"); }
    }

    #endregion

    #region ELF helper methods

    private static ushort ReadUInt16(byte[] data, int offset, bool isLe)
    {
        if (isLe)
            return (ushort)(data[offset] | data[offset + 1] << 8);
        return (ushort)(data[offset + 1] | data[offset] << 8);
    }

    private static uint ReadUInt32(byte[] data, int offset, bool isLe)
    {
        if (isLe)
            return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
        return (uint)(data[offset + 3] | data[offset + 2] << 8 | data[offset + 1] << 16 | data[offset] << 24);
    }

    private static int ReadInt32(byte[] data, int offset, bool isLe)
    {
        return (int)ReadUInt32(data, offset, isLe);
    }

    #endregion

    #region ELF segment data extraction for IntelHex

    /// <summary>
    /// 从 ELF 文件中提取 LOAD 段数据并填充到 IntelHex。
    /// </summary>
    private static IntelHex ToIntelHexFromElf(string path)
    {
        var ih = new IntelHex();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            var ident = reader.ReadBytes(16);
            bool isLe = ident[5] == 1;

            uint ePhOff = ReadUInt32(ident, 28, isLe);
            ushort ePhEntSize = ReadUInt16(ident, 42, isLe);
            ushort ePhNum = ReadUInt16(ident, 44, isLe);

            // Read all file data
            fs.Position = 0;
            byte[] allBytes = reader.ReadBytes((int)fs.Length);

            // Parse program headers
            var loadSegs = new List<(int Addr, int FileOff, int Filesz)>();
            for (int i = 0; i < ePhNum; i++)
            {
                fs.Position = ePhOff + i * ePhEntSize;
                var ph = reader.ReadBytes(32);
                uint pType = ReadUInt32(ph, 0, isLe);
                if (pType != 1) continue;
                uint pPaddr = ReadUInt32(ph, 12, isLe);
                uint pFilesz = ReadUInt32(ph, 16, isLe);
                uint pOffset = ReadUInt32(ph, 4, isLe);
                if (pFilesz > 0)
                    loadSegs.Add((checked((int)pPaddr), checked((int)pOffset), checked((int)pFilesz)));
            }

            foreach (var (addr, fileOff, filesz) in loadSegs)
            {
                byte[] segData = new byte[filesz];
                Array.Copy(allBytes, fileOff, segData, 0, filesz);
                ih.FromBytes(segData, addr);
            }
        }
        catch (FileParseError) { throw; }
        catch (Exception e) { throw new FileParseError($"ELF 解析失败：{e.Message}"); }
        return ih;
    }

    #endregion
}

/// <summary>
/// 文件解析错误。
/// </summary>
public class FileParseError : Exception
{
    public FileParseError(string message) : base(message) { }
    public FileParseError(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Intel HEX 解析与操作类。
/// 支持 .hex 文件读写、按地址存取数据、获取段列表、转换为二进制。
/// </summary>
public class IntelHex
{
    private readonly Dictionary<int, byte> _data = new();

    public int Count => _data.Count;
    public int MinAddr => _data.Count > 0 ? _data.Keys.Min() : 0;
    public int MaxAddr => _data.Count > 0 ? _data.Keys.Max() : 0;
    public IEnumerable<(int Start, int EndExclusive)> Segments => GetSegments();

    public void LoadHex(string path)
    {
        _data.Clear();
        var lines = File.ReadAllLines(path);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(":")) continue;
            ParseHexLine(line);
        }
    }

    public void FromBytes(byte[] bytes, int offset)
    {
        for (int i = 0; i < bytes.Length; i++)
            _data[offset + i] = bytes[i];
    }

    public byte[] ToBinArray(int start, int end)
    {
        var result = new List<byte>();
        for (int addr = start; addr < end && addr <= MaxAddr; addr++)
        {
            if (_data.TryGetValue(addr, out byte b))
                result.Add(b);
            else
                result.Add(0xFF); // 未编程的地址填充 0xFF
        }
        return result.ToArray();
    }

    public byte[] ToBinFile()
    {
        if (_data.Count == 0) return Array.Empty<byte>();
        int min = MinAddr, max = MaxAddr;
        var result = new byte[max - min + 1];
        for (int addr = min; addr <= max; addr++)
        {
            if (_data.TryGetValue(addr, out byte b))
                result[addr - min] = b;
        }
        return result;
    }

    public void PutBytes(byte[] bytes, int addr)
    {
        for (int i = 0; i < bytes.Length; i++)
            _data[addr + i] = bytes[i];
    }

    public IEnumerable<(int Start, int EndExclusive)> GetSegments()
    {
        if (_data.Count == 0) yield break;
        var sorted = _data.Keys.OrderBy(a => a).ToList();
        int start = sorted[0], prev = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] != prev + 1)
            {
                yield return (start, prev + 1);
                start = sorted[i];
            }
            prev = sorted[i];
        }
        yield return (start, prev + 1);
    }

    public void WriteHexFile(string path)
    {
        var sb = new System.Text.StringBuilder();
        int min = MinAddr, max = MaxAddr;
        int addr = min;
        while (addr <= max)
        {
            // Collect up to 16 bytes per line
            int count = Math.Min(16, max - addr + 1);
            // Check if data exists for these addresses
            bool allEmpty = true;
            for (int i = 0; i < count; i++)
            {
                if (_data.ContainsKey(addr + i)) { allEmpty = false; break; }
            }
            if (allEmpty)
            {
                addr += count;
                continue;
            }

            var line = new System.Text.StringBuilder();
            line.Append(':');
            int byteCount = count;
            line.Append(byteCount.ToString("X2"));
            line.Append(((ushort)addr).ToString("X4"));
            line.Append("00"); // record type = data

            int checksum = byteCount + ((ushort)addr & 0xFF) + (((ushort)addr >> 8) & 0xFF);
            for (int i = 0; i < count; i++)
            {
                byte b = _data.TryGetValue(addr + i, out byte val) ? val : (byte)0xFF;
                line.Append(b.ToString("X2"));
                checksum += b;
            }
            checksum = (~checksum + 1) & 0xFF;
            line.Append(checksum.ToString("X2"));

            sb.AppendLine(line.ToString());
            addr += count;
        }

        // EOF record
        sb.AppendLine(":00000001FF");
        File.WriteAllText(path, sb.ToString());
    }

    public void Tobinfile(string path)
    {
        var bytes = ToBinFile();
        File.WriteAllBytes(path, bytes);
    }

    private void ParseHexLine(string line)
    {
        try
        {
            // Remove whitespace
            line = line.Trim();
            if (!line.StartsWith(":")) return;

            // Format: :LLAAAATT[DD...]CC
            // LL = byte count, AAAA = address, TT = type, DD = data, CC = checksum
            string hex = line.Substring(1); // Remove leading ':'
            if (hex.Length < 8) return;

            int byteCount = Convert.ToInt32(hex.Substring(0, 2), 16);
            int address = Convert.ToInt32(hex.Substring(2, 4), 16);
            int recordType = Convert.ToInt32(hex.Substring(6, 2), 16);

            if (recordType == 0) // Data record
            {
                int dataStart = 8;
                for (int i = 0; i < byteCount && dataStart + 2 + i * 2 < hex.Length; i++)
                {
                    string byteStr = hex.Substring(dataStart + i * 2, 2);
                    int addr = address + i;
                    byte val = Convert.ToByte(byteStr, 16);
                    _data[addr] = val;
                }
            }
            // Extended linear address (type 4) and others are skipped for simplicity
            // but could be implemented for full support
        }
        catch { }
    }
}
