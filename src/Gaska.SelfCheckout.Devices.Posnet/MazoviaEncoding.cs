using System.Text;

namespace Gaska.SelfCheckout.Devices.Posnet;

/// <summary>
/// Mazovia code page (Polish DOS encoding used by fiscal printers): ASCII plus the 18 Polish letters.
/// Other characters above 0x7F are sent as '?' (the printer would print them as spaces anyway).
/// </summary>
public sealed class MazoviaEncoding : Encoding
{
    private const string Polish = "ĄĆĘŁŃÓŚŹŻąćęłńóśźż";
    private static readonly byte[] Codes =
        [0x8F, 0x95, 0x90, 0x9C, 0xA5, 0xA3, 0x98, 0xA0, 0xA1, 0x86, 0x8D, 0x91, 0x92, 0xA4, 0xA2, 0x9E, 0xA6, 0xA7];

    private static readonly Dictionary<char, byte> ToByte = Polish.Select((c, i) => (c, Codes[i])).ToDictionary(p => p.c, p => p.Item2);
    private static readonly Dictionary<byte, char> ToChar = Polish.Select((c, i) => (Codes[i], c)).ToDictionary(p => p.Item1, p => p.c);

    public static MazoviaEncoding Instance { get; } = new();

    public override string EncodingName => "Mazovia";

    public override int GetByteCount(char[] chars, int index, int count) => count;

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        for (var i = 0; i < charCount; i++)
        {
            var c = chars[charIndex + i];
            bytes[byteIndex + i] = c < 0x80 ? (byte)c : ToByte.GetValueOrDefault(c, (byte)'?');
        }

        return charCount;
    }

    public override int GetCharCount(byte[] bytes, int index, int count) => count;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        for (var i = 0; i < byteCount; i++)
        {
            var b = bytes[byteIndex + i];
            chars[charIndex + i] = b < 0x80 ? (char)b : ToChar.GetValueOrDefault(b, '?');
        }

        return byteCount;
    }

    public override int GetMaxByteCount(int charCount) => charCount;

    public override int GetMaxCharCount(int byteCount) => byteCount;
}
