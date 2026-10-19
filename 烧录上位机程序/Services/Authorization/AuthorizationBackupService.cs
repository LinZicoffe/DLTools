using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace 烧录上位机程序.Services.Authorization;

internal static class AuthorizationBackupService
{
    internal const int MaxPayloadBytes = 4 * 1024 * 1024;
    internal const int MaxFileBytes = MaxPayloadBytes + 56;
    private const int Iterations = 600_000;
    private const int HeaderLength = 40;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DLABK001");

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 16 || password.Length > 1024)
            throw new ArgumentException("备份密码需要 16～1024 个字符，请使用独立的强密码，不能使用六位授权码。");
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string password)
    {
        ValidatePassword(password);
        if (plaintext.Length > MaxPayloadBytes) throw new InvalidDataException("档案库过大，无法备份。");
        var output = new byte[HeaderLength + plaintext.Length + 16];
        Magic.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(8, 4), Iterations);
        RandomNumberGenerator.Fill(output.AsSpan(12, 28)); // salt(16) + nonce(12)
        var key = Rfc2898DeriveBytes.Pbkdf2(password, output.AsSpan(12, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(28, 12), plaintext, output.AsSpan(HeaderLength, plaintext.Length),
                output.AsSpan(HeaderLength + plaintext.Length, 16), output.AsSpan(0, HeaderLength));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> input, string password)
    {
        ValidatePassword(password);
        if (input.Length < HeaderLength + 16 || input.Length > MaxFileBytes || !input[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32BigEndian(input.Slice(8, 4)) != Iterations)
            throw new InvalidDataException("备份格式、版本或大小不受支持。");
        var plaintext = new byte[input.Length - HeaderLength - 16];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, input.Slice(12, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(input.Slice(28, 12), input.Slice(HeaderLength, plaintext.Length), input[^16..], plaintext, input[..HeaderLength]);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("备份密码错误或文件已损坏，原档案未修改。");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
