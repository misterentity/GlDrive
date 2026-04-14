namespace GlDrive.Irc;

/// <summary>
/// FiSH-specific base64 encoding/decoding.
/// Uses a non-standard alphabet and encodes 8-byte blocks to 12 chars.
/// </summary>
public static class FishBase64
{
    private const string Alphabet = "./0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Encode(byte[] data)
    {
        var result = new char[(data.Length / 8) * 12];
        var pos = 0;

        for (var i = 0; i < data.Length; i += 8)
        {
            // Split 8-byte block into two 32-bit big-endian values
            var left = (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
            var right = (uint)((data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7]);

            // Encode right half first (6 chars), then left half (6 chars)
            for (var j = 0; j < 6; j++)
            {
                result[pos++] = Alphabet[(int)(right & 0x3F)];
                right >>= 6;
            }
            for (var j = 0; j < 6; j++)
            {
                result[pos++] = Alphabet[(int)(left & 0x3F)];
                left >>= 6;
            }
        }

        return new string(result);
    }

    public static byte[] Decode(string encoded)
    {
        var blocks = encoded.Length / 12;
        var result = new byte[blocks * 8];

        for (var block = 0; block < blocks; block++)
        {
            var offset = block * 12;

            // Decode right half (first 6 chars) then left half (next 6 chars)
            uint right = 0;
            for (var j = 5; j >= 0; j--)
            {
                right <<= 6;
                var idx = Alphabet.IndexOf(encoded[offset + j]);
                if (idx < 0) throw new FormatException($"Invalid FiSH base64 character: '{encoded[offset + j]}'");
                right |= (uint)idx;
            }

            uint left = 0;
            for (var j = 5; j >= 0; j--)
            {
                left <<= 6;
                var idx = Alphabet.IndexOf(encoded[offset + 6 + j]);
                if (idx < 0) throw new FormatException($"Invalid FiSH base64 character: '{encoded[offset + 6 + j]}'");
                left |= (uint)idx;
            }

            var outOffset = block * 8;
            result[outOffset] = (byte)(left >> 24);
            result[outOffset + 1] = (byte)(left >> 16);
            result[outOffset + 2] = (byte)(left >> 8);
            result[outOffset + 3] = (byte)left;
            result[outOffset + 4] = (byte)(right >> 24);
            result[outOffset + 5] = (byte)(right >> 16);
            result[outOffset + 6] = (byte)(right >> 8);
            result[outOffset + 7] = (byte)right;
        }

        return result;
    }
}
