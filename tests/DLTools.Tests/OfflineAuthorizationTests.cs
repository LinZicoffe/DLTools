using Xunit;
using 烧录上位机程序.Services.Authorization;

namespace DLTools.Tests;

public class OfflineAuthorizationTests
{
    private const string DeviceId = "11112222333344445555666677778888";
    private static readonly byte[] PublicTestKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData("000000", "200533")]
    [InlineData("583194", "677046")]
    [InlineData("999999", "077174")]
    [InlineData("000006", "011864")]
    public void Generate_MatchesIndependentlyCalculatedProtocolVectors(string challenge, string expected)
    {
        Assert.Equal(expected, OfflineAuthorizationService.Generate(PublicTestKey, DeviceId, challenge));
        Assert.True(OfflineAuthorizationService.Verify(PublicTestKey, DeviceId, challenge, expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("１２３４５６")]
    [InlineData("١٢٣٤٥٦")]
    [InlineData("12 456")]
    [InlineData("+12345")]
    public void Generate_RejectsInvalidChallenge(string challenge) =>
        Assert.Throws<ArgumentException>(() => OfflineAuthorizationService.Generate(PublicTestKey, DeviceId, challenge));

    [Fact]
    public void Verify_RejectsInvalidOrIncorrectResponse()
    {
        Assert.False(OfflineAuthorizationService.Verify(PublicTestKey, DeviceId, "583194", "677047"));
        Assert.False(OfflineAuthorizationService.Verify(PublicTestKey, DeviceId, "583194", "６７７０４６"));
        Assert.False(OfflineAuthorizationService.Verify(PublicTestKey, DeviceId, "583194", ""));
        Assert.Equal("677046", OfflineAuthorizationService.Generate(PublicTestKey, DeviceId, " 583194\r\n"));
        Assert.Throws<ArgumentException>(() => OfflineAuthorizationService.Generate(new byte[16], DeviceId, "583194"));
        Assert.Throws<ArgumentException>(() => OfflineAuthorizationService.Generate(PublicTestKey, "bad-id", "583194"));
    }
}
