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

    #region Format detection

    public static string DetectFormat(string path)
    {
        // Invariant：土耳其语等区域下 ToLower 会把 'I' 映射为 'ı'，导致 .BIN 等大写后缀识别失败
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!ExtMap.ContainsKey(ext))
            throw new FileParseError($"不支持的文件后缀：{ext ?? "(无后缀)"}");
        return ExtMap[ext];
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

    #region ELF helper methods

    /// <summary>
    /// ELF32 文件头（52 字节）。原先各函数只读 16 字节 e_ident 却按 52 字节
    /// 头的偏移取值，必然数组越界；统一完整读取后按规范偏移解析。
    /// </summary>
    private readonly struct Elf32Header
    {
        public readonly bool IsLe;
        public readonly uint Entry;        // e_entry     @24
        public readonly uint PhOff;        // e_phoff     @28
        public readonly uint ShOff;        // e_shoff     @32
        public readonly ushort PhEntSize;  // e_phentsize @42
        public readonly ushort PhNum;      // e_phnum     @44
        public readonly ushort ShEntSize;  // e_shentsize @46
        public readonly ushort ShNum;      // e_shnum     @48
        public readonly ushort ShStrNdx;   // e_shstrndx  @50

        private Elf32Header(byte[] h)
        {
            IsLe = h[5] == 1;
            Entry = ReadUInt32(h, 24, IsLe);
            PhOff = ReadUInt32(h, 28, IsLe);
            ShOff = ReadUInt32(h, 32, IsLe);
            PhEntSize = ReadUInt16(h, 42, IsLe);
            PhNum = ReadUInt16(h, 44, IsLe);
            ShEntSize = ReadUInt16(h, 46, IsLe);
            ShNum = ReadUInt16(h, 48, IsLe);
            ShStrNdx = ReadUInt16(h, 50, IsLe);
        }

        public static Elf32Header Read(BinaryReader reader)
        {
            var h = reader.ReadBytes(52);
            if (h.Length < 52 || h[0] != 0x7F || h[1] != (byte)'E' || h[2] != (byte)'L' || h[3] != (byte)'F')
                throw new FileParseError("不是有效的 ELF 文件");
            if (h[4] == 2)
                throw new FileParseError("不支持 ELF64，请使用 ELF32");
            return new Elf32Header(h);
        }
    }

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
            var eh = Elf32Header.Read(reader);

            // Read all file data
            fs.Position = 0;
            byte[] allBytes = reader.ReadBytes((int)fs.Length);

            // Parse program headers
            var loadSegs = new List<(int Addr, int FileOff, int Filesz)>();
            for (int i = 0; i < eh.PhNum; i++)
            {
                fs.Position = eh.PhOff + i * eh.PhEntSize;
                var ph = reader.ReadBytes(32);
                uint pType = ReadUInt32(ph, 0, eh.IsLe);
                if (pType != 1) continue;
                uint pPaddr = ReadUInt32(ph, 12, eh.IsLe);
                uint pFilesz = ReadUInt32(ph, 16, eh.IsLe);
                uint pOffset = ReadUInt32(ph, 4, eh.IsLe);
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

    /// <summary>扩展线性地址记录（type 04）设置的地址高 16 位。</summary>
    private uint _upperAddr;

    public int Count => _data.Count;
    public int MinAddr => _data.Count > 0 ? _data.Keys.Min() : 0;
    public int MaxAddr => _data.Count > 0 ? _data.Keys.Max() : 0;
    public IEnumerable<(int Start, int EndExclusive)> Segments => GetSegments();

    public void LoadHex(string path)
    {
        _data.Clear();
        _upperAddr = 0;
        _segmentBase = 0;
        bool sawEof = false;
        int lineNo = 0;
        // 流式逐行读取，避免大固件整文件预读导致内存驻留翻倍
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(":")) continue;
            if (!ParseHexLine(line, lineNo))
            {
                sawEof = true;
                break;
            }
        }
        // 缺少 EOF 记录说明文件在行边界被截断：每一行校验和都各自合法，
        // 若放行会烧出"半份固件"且用同一截断文件校验必然通过（静默数据完整性缺陷）
        if (!sawEof)
            throw new FileParseError("HEX 文件缺少 EOF 记录，文件可能被截断或不完整");
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
        int currentUpper = -1;
        // 按 GetSegments 输出真实存在的数据段，地址空洞不填充 0xFF（否则空洞被固化成真实数据）
        foreach (var (segStart, segEnd) in GetSegments())
        {
            int addr = segStart;
            while (addr < segEnd)
            {
                // 地址高 16 位变化时先输出扩展线性地址记录（type 04），
                // 否则行内地址字段只有 16 位，高于 0xFFFF 的数据会回绕重叠
                int upper = addr >> 16;
                if (upper != currentUpper)
                {
                    currentUpper = upper;
                    int ulbaSum = 2 + 0 + 0 + 4 + ((currentUpper >> 8) & 0xFF) + (currentUpper & 0xFF);
                    ulbaSum = (~ulbaSum + 1) & 0xFF;
                    sb.AppendLine($":02000004{currentUpper:X4}{ulbaSum:X2}");
                }
                // 一行最多 16 字节，且不跨 64K 边界（跨界需先输出新的 ULBA 记录）
                int count = Math.Min(16, Math.Min(segEnd - addr, 0x10000 - (addr & 0xFFFF)));
                int checksum = count + ((addr >> 8) & 0xFF) + (addr & 0xFF);
                var line = new System.Text.StringBuilder();
                line.Append(':');
                line.Append(count.ToString("X2"));
                line.Append((addr & 0xFFFF).ToString("X4"));
                line.Append("00"); // record type = data
                for (int i = 0; i < count; i++)
                {
                    byte b = _data[addr + i];
                    line.Append(b.ToString("X2"));
                    checksum += b;
                }
                checksum = (~checksum + 1) & 0xFF;
                line.Append(checksum.ToString("X2"));
                sb.AppendLine(line.ToString());
                addr += count;
            }
        }

        // EOF record
        sb.AppendLine(":00000001FF");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>扩展段地址记录（type 02）设置的段基址（值 × 16）。</summary>
    private uint _segmentBase;

    /// <summary>解析一行记录；返回 false 表示遇到 EOF 记录，调用方应停止解析后续行。</summary>
    private bool ParseHexLine(string line, int lineNo)
    {
        // 格式 :LLAAAATT[DD...]CC；整行（去掉 ':'）所有字节之和 mod 256 必须为 0
        string hex = line.Trim().Substring(1);
        if (hex.Length < 10 || hex.Length % 2 != 0)
            throw new FileParseError($"HEX 第 {lineNo} 行格式非法：{line}");

        int sum = 0;
        var recBytes = new byte[hex.Length / 2];
        for (int i = 0; i < recBytes.Length; i++)
        {
            if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out recBytes[i]))
                throw new FileParseError($"HEX 第 {lineNo} 行含非十六进制字符：{line}");
            sum += recBytes[i];
        }
        if ((sum & 0xFF) != 0)
            throw new FileParseError($"HEX 第 {lineNo} 行校验和错误（文件损坏或被截断）：{line}");

        int byteCount = recBytes[0];
        if (recBytes.Length != byteCount + 5)
            throw new FileParseError($"HEX 第 {lineNo} 行长度与字节计数字段不符：{line}");

        int address = (recBytes[1] << 8) | recBytes[2];
        int recordType = recBytes[3];

        switch (recordType)
        {
            case 0: // 数据记录：绝对地址 = 当前生效基址（type 04 线性 / type 02 分段二选一）+ 记录内 16 位偏移
            {
                int addr = (int)(_upperAddr + _segmentBase + (uint)address);
                for (int i = 0; i < byteCount; i++)
                    _data[addr + i] = recBytes[4 + i];
                break;
            }
            case 1: // EOF：规范要求必须是最后一行
                return false;
            case 2: // 扩展段地址：数据两字节 × 16 为段基址
                if (byteCount != 2)
                    throw new FileParseError($"HEX 第 {lineNo} 行扩展段地址记录数据长度必须为 2 字节：{line}");
                _segmentBase = (uint)((recBytes[4] << 8) | recBytes[5]) << 4;
                _upperAddr = 0; // 分段寻址与线性寻址互斥，切换时另一基址失效
                break;
            case 4: // 扩展线性地址：数据两字节为后续记录的地址高 16 位
                if (byteCount != 2)
                    throw new FileParseError($"HEX 第 {lineNo} 行扩展线性地址记录数据长度必须为 2 字节：{line}");
                _upperAddr = (uint)((recBytes[4] << 8) | recBytes[5]) << 16;
                _segmentBase = 0;
                break;
            // type 03/05（启动地址）等不影响数据寻址，忽略
        }
        return true;
    }
}
