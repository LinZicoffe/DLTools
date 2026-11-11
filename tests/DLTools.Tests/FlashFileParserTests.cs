using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using 烧录上位机程序.Models;

namespace DLTools.Tests;

/// <summary>
/// 固件解析器纯逻辑测试：hex / elf / bin 三格式的段提取、往返一致性与
/// 损坏文件拒绝路径（此前烧录链三文件零测试，解析器是其中唯一可离线验证的纯托管部分）。
/// </summary>
public class FlashFileParserTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string TempFile(string name, string? content = null, byte[]? bytes = null)
    {
        var path = Path.Combine(_workspace.Root, name);
        if (content is not null) File.WriteAllText(path, content);
        else if (bytes is not null) File.WriteAllBytes(path, bytes);
        else File.WriteAllText(path, "");
        return path;
    }

    /// <summary>按 Intel HEX 规范构造一行记录（自动计算校验和），供损坏文件用例使用。</summary>
    private static string Record(int addr, byte[] data, int type = 0)
    {
        int sum = data.Length + ((addr >> 8) & 0xFF) + (addr & 0xFF) + type;
        var sb = new StringBuilder($":{data.Length:X2}{addr:X4}{type:X2}");
        foreach (var b in data)
        {
            sb.Append(b.ToString("X2"));
            sum += b;
        }
        return sb.Append(((~sum + 1) & 0xFF).ToString("X2")).ToString();
    }

    #region DetectFormat

    [Theory]
    [InlineData("f.axf", "elf")]
    [InlineData("f.elf", "elf")]
    [InlineData("f.hex", "hex")]
    [InlineData("f.bin", "bin")]
    [InlineData("FIRMWARE.BIN", "bin")] // 大写后缀：Invariant 路径
    public void DetectFormat_MapsSupportedExtensions(string fileName, string expected)
    {
        Assert.Equal(expected, FlashFileParser.DetectFormat(Path.Combine(_workspace.Root, fileName)));
    }

    [Fact]
    public void DetectFormat_RejectsUnsupportedExtension()
    {
        Assert.Throws<FileParseError>(() => FlashFileParser.DetectFormat(TempFile("f.txt")));
    }

    [Fact]
    public void ToIntelHex_MissingFile_Throws()
    {
        Assert.Throws<FileParseError>(() =>
            FlashFileParser.ToIntelHex(Path.Combine(_workspace.Root, "nope.hex")));
    }

    #endregion

    #region BIN

    [Fact]
    public void BinLoad_PlacesPayloadAtProvidedAddress()
    {
        var path = TempFile("fw.bin", bytes: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var ih = FlashFileParser.ToIntelHex(path, 0x08000000);

        var segment = Assert.Single(ih.Segments);
        Assert.Equal(0x08000000, segment.Start);
        Assert.Equal(0x08000004, segment.EndExclusive);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ih.ToBinArray(0x08000000, 0x08000004));
    }

    #endregion

    #region Intel HEX

    [Fact]
    public void HexRoundTrip_DataAndSegmentsSurviveWriteAndReload()
    {
        var original = new IntelHex();
        original.FromBytes(new byte[] { 1, 2, 3 }, 0x100);
        original.FromBytes(new byte[] { 9 }, 0x200); // 地址空洞：应保持两段
        var path = Path.Combine(_workspace.Root, "round.hex");
        original.WriteHexFile(path);

        var reloaded = FlashFileParser.ToIntelHex(path);
        var segments = reloaded.Segments.ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal((0x100, 0x103), (segments[0].Start, segments[0].EndExclusive));
        Assert.Equal((0x200, 0x201), (segments[1].Start, segments[1].EndExclusive));
        Assert.Equal(new byte[] { 1, 2, 3 }, reloaded.ToBinArray(0x100, 0x103));
        Assert.Equal(new byte[] { 9 }, reloaded.ToBinArray(0x200, 0x201));
    }

    [Fact]
    public void HexLoad_MissingEofRecord_RejectsTruncatedFile()
    {
        // 行行校验和都合法、但缺 EOF：放行会烧出"半份固件"
        var path = TempFile("cut.hex", Record(0x100, new byte[] { 1, 2 }) + "\n");
        Assert.Throws<FileParseError>(() => FlashFileParser.ToIntelHex(path));
    }

    [Fact]
    public void HexLoad_BadChecksum_RejectsCorruptLine()
    {
        var good = Record(0x100, new byte[] { 1, 2 });
        var corrupt = good[..^1] + (good[^1] == '0' ? '1' : '0'); // 破坏校验和最后一位
        var path = TempFile("bad.hex", corrupt + "\n:00000001FF\n");
        Assert.Throws<FileParseError>(() => FlashFileParser.ToIntelHex(path));
    }

    [Fact]
    public void HexLoad_ExtendedLinearAddress_PlacesDataAbove64K()
    {
        // type 04 设高 16 位 = 0x0001，数据实际落在 0x00010100（> 64K，验证 ULBA 生效）
        var path = TempFile("ulba.hex",
            Record(0x0000, new byte[] { 0x00, 0x01 }, type: 4) + "\n" +
            Record(0x0100, new byte[] { 0x42 }) + "\n" +
            ":00000001FF\n");
        var ih = FlashFileParser.ToIntelHex(path);

        var segment = Assert.Single(ih.Segments);
        Assert.Equal(0x00010100, segment.Start);
        Assert.Equal(0x42, ih.ToBinArray(0x00010100, 0x00010101)[0]);
    }

    [Fact]
    public void ToBinArray_FillsInteriorGapsWith0xFF()
    {
        var ih = new IntelHex();
        ih.FromBytes(new byte[] { 1 }, 0x100);
        ih.FromBytes(new byte[] { 3 }, 0x103); // 0x101–0x102 未编程：段内空洞
        Assert.Equal(new byte[] { 1, 0xFF, 0xFF, 3 }, ih.ToBinArray(0x100, 0x104));
    }

    #endregion

    #region ELF32

    private static void Write16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void Write32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static byte[] BuildElf32(uint paddr, byte[] payload)
    {
        const int ehSize = 52, phSize = 32, dataOff = ehSize + phSize;
        var file = new byte[dataOff + payload.Length];
        file[0] = 0x7F; file[1] = (byte)'E'; file[2] = (byte)'L'; file[3] = (byte)'F';
        file[4] = 1; // ELF32
        file[5] = 1; // 小端
        Write32(file, 28, ehSize);                     // e_phoff
        Write16(file, 40, ehSize);                     // e_ehsize
        Write16(file, 42, phSize);                     // e_phentsize
        Write16(file, 44, 1);                          // e_phnum
        Write32(file, ehSize + 0, 1);                  // p_type = PT_LOAD
        Write32(file, ehSize + 4, (uint)dataOff);      // p_offset
        Write32(file, ehSize + 12, paddr);             // p_paddr
        Write32(file, ehSize + 16, (uint)payload.Length); // p_filesz
        payload.CopyTo(file, dataOff);
        return file;
    }

    [Fact]
    public void ElfLoad_ExtractsLoadSegmentAtPaddr()
    {
        var path = TempFile("fw.elf", bytes: BuildElf32(0x08000100, new byte[] { 0xAA, 0xBB }));
        var ih = FlashFileParser.ToIntelHex(path);

        var segment = Assert.Single(ih.Segments);
        Assert.Equal(0x08000100, segment.Start);
        Assert.Equal(0x08000102, segment.EndExclusive);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, ih.ToBinArray(0x08000100, 0x08000102));
    }

    [Fact]
    public void ElfLoad_RejectsElf64()
    {
        var bytes = BuildElf32(0x100, new byte[] { 1 });
        bytes[4] = 2; // ELF64
        Assert.Throws<FileParseError>(() => FlashFileParser.ToIntelHex(TempFile("fw64.elf", bytes: bytes)));
    }

    [Fact]
    public void ElfLoad_RejectsBadMagic()
    {
        Assert.Throws<FileParseError>(() => FlashFileParser.ToIntelHex(TempFile("fake.elf", bytes: new byte[64])));
    }

    #endregion
}
