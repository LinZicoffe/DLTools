using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 烧录上位机程序.Services.Authorization;

/// <summary>Version 1 offline ACTIVATE challenge/response; not a client authorization state machine.</summary>
public static class OfflineAuthorizationService
{
    public static string NormalizeChallenge(string challenge)
    {
        var value = challenge?.Trim();
        if (value is null || value.Length != 6 || value.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("申请码必须是六位数字（0–9），例如 000012。", nameof(challenge));
        return value;
    }

    public static string NormalizeDeviceId(string deviceId)
    {
        if (!Guid.TryParseExact(deviceId, "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("设备标识无效。", nameof(deviceId));
        return id.ToString("N").ToUpperInvariant();
    }

    public static string Generate(ReadOnlySpan<byte> key, string deviceId, string challenge)
    {
        if (key.Length != 32) throw new ArgumentException("设备密钥必须为 32 字节。", nameof(key));
        var message = Encoding.ASCII.GetBytes($"DLTOOLS-AUTH|1|{NormalizeDeviceId(deviceId)}|ACTIVATE|{NormalizeChallenge(challenge)}");
        Span<byte> hash = stackalloc byte[32];
        try
        {
            HMACSHA256.HashData(key, message, hash);
            var offset = hash[31] & 0x0F;
            var value = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;
            return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    /// <summary>Calculation comparison only. Callers must enforce persistence, request lifetime and throttling.</summary>
    public static bool Verify(ReadOnlySpan<byte> key, string deviceId, string challenge, string response)
    {
        var supplied = response?.Trim();
        if (supplied is null || supplied.Length != 6 || supplied.Any(c => c is < '0' or > '9')) return false;
        var expected = Generate(key, deviceId, challenge);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));
    }
}
