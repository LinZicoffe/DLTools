using System.Text;
using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadRangeProbeTests
{
    [Theory]
    [InlineData("bytes 0-0/12345", 0, 0, 12345L)]
    [InlineData("bytes 5-9/12345", 5, 9, 12345L)]
    [InlineData("bytes 0-1023/*", 0, 1023, null)]
    public void TryParseContentRange_AcceptsCanonicalForms(string value, long start, long end, long? total)
    {
        Assert.True(RangeProbe.TryParseContentRange(value, out var parsedStart, out var parsedEnd, out var parsedTotal));
        Assert.Equal(start, parsedStart);
        Assert.Equal(end, parsedEnd);
        Assert.Equal(total, parsedTotal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bytes 0-0")]
    [InlineData("bytes/123")]
    [InlineData("bytes 9-5/123")]    // end < start
    [InlineData("bytes -1-2/123")]
    [InlineData("bytes a-b/123")]
    [InlineData("bytes 0-0/-5")]
    public void TryParseContentRange_RejectsMalformedValues(string? value)
    {
        Assert.False(RangeProbe.TryParseContentRange(value, out _, out _, out _));
    }

    [Theory]
    [InlineData("attachment; filename=\"firmware.zip\"", "firmware.zip")]
    [InlineData("attachment; filename=fw.zip", "fw.zip")]
    [InlineData("attachment; filename=\"my firmware.zip\"", "my firmware.zip")]
    [InlineData("attachment", "")]
    [InlineData("", "")]
    public void ParseContentDispositionFileName_ReadsQuotedAndBareValues(string header, string expected)
    {
        Assert.Equal(expected, RangeProbe.ParseContentDispositionFileName(header));
    }

    [Fact]
    public void ParseContentDispositionFileName_ExtendedUtf8ValueWinsOverPlainFilename()
    {
        var header = "attachment; filename=\"firm%20ware.zip\"; filename*=UTF-8''%E5%9B%BA%E4%BB%B6.bin";
        Assert.Equal("固件.bin", RangeProbe.ParseContentDispositionFileName(header));
    }

    [Fact]
    public void ParseContentDispositionFileName_FallsBackWhenExtendedCharsetIsNotUtf8()
    {
        var header = "attachment; filename=fallback.zip; filename*=iso-8859-1'en'A%E9.zip";
        Assert.Equal("fallback.zip", RangeProbe.ParseContentDispositionFileName(header));
    }

    [Fact]
    public void ParseContentDispositionFileName_StripsInvalidFileNameCharacters()
    {
        Assert.Equal("firmware.zip", RangeProbe.ParseContentDispositionFileName("attachment; filename=\"firm:ware*.zip\""));
    }

    [Fact]
    public async Task Probe_Partial206WithContentRange_ReportsRangesAndTotalLength()
    {
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.Partial(12345, 0, 0, [1, 2], etag: "\"etag-1\"")));
        var result = await RangeProbe.ProbeAsync(transport, "https://8.8.8.8/fw.bin", CancellationToken.None);
        Assert.True(result.SupportsRanges);
        Assert.Equal(12345, result.TotalLength);
        Assert.Equal("\"etag-1\"", result.ETag);
        Assert.Equal(206, result.StatusCode);
    }

    [Fact]
    public async Task Probe_Status200_FallsBackToSingleStreamWithContentLength()
    {
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.OkFull(4096, new byte[4096], disposition: "attachment; filename=fw.zip")));
        var result = await RangeProbe.ProbeAsync(transport, "https://8.8.8.8/fw.bin", CancellationToken.None);
        Assert.False(result.SupportsRanges);
        Assert.Equal(4096, result.TotalLength);
        Assert.Equal("fw.zip", result.FileName);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Probe_Partial206WithoutParsableContentRange_TreatedAsSingleStream()
    {
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            new TransportResponse(206, "application/octet-stream", "", "", "garbage", 2, "", null, new MemoryStream([1]))));
        var result = await RangeProbe.ProbeAsync(transport, "https://8.8.8.8/fw.bin", CancellationToken.None);
        Assert.False(result.SupportsRanges);
    }

    [Theory]
    [InlineData(404, "404", true)]
    [InlineData(403, "403", true)]
    [InlineData(410, "410", true)]
    [InlineData(429, "429", false)]
    [InlineData(503, "503", false)]
    public async Task Probe_NonSuccess_ThrowsClassifiedException(int status, string hint, bool terminal)
    {
        var transport = new FakeDownloadTransport((_, _) =>
            Task.FromResult(FakeDownloadTransport.Status(status)));
        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => RangeProbe.ProbeAsync(transport, "https://8.8.8.8/fw.bin", CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(terminal, error.Terminal);
        Assert.Contains(hint, error.Message);
    }
}
