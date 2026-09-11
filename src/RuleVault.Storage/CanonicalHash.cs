using System.Security.Cryptography;
using System.Text;

namespace RuleVault.Storage;

public enum HashKind
{
    CanonicalText,
    RawBytes
}

public static class CanonicalHash
{
    public static string Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var canonical = CanonicalizeText(value);
        return Raw(Encoding.UTF8.GetBytes(canonical));
    }

    public static string Raw(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    public static string CanonicalizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var start = value.Length > 0 && value[0] == '\uFEFF' ? 1 : 0;
        var builder = new StringBuilder(value.Length - start);
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\r')
            {
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                builder.Append('\n');
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public static string DecodeUtf8(ReadOnlySpan<byte> bytes, bool allowBom = true)
    {
        var start = allowBom && bytes.StartsWith("\xEF\xBB\xBF"u8) ? 3 : 0;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return encoding.GetString(bytes[start..]);
    }
}
