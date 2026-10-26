using System.Text;

namespace Gaska.SelfCheckout.Devices.Posnet;

/// <summary>POSNET protocol frame: [STX]command[TAB]parameter[TAB]...#CRC16[ETX].</summary>
public static class PosnetProtocol
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const char Tab = '\t';

    /// <summary>Default text encoding (printer code page WINDOWS-1250).</summary>
    public static Encoding Encoding { get; } = GetEncoding(PosnetCodePage.Windows1250);

    /// <summary>Text encoding of a printer code page (pccfgget cp: 0 – WIN1250, 1 – LATIN2, 2 – MAZOVIA).</summary>
    public static Encoding GetEncoding(PosnetCodePage codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return codePage switch
        {
            PosnetCodePage.Latin2 => Encoding.GetEncoding(852),
            PosnetCodePage.Mazovia => MazoviaEncoding.Instance,
            _ => Encoding.GetEncoding(1250)
        };
    }

    /// <summary>CRC16-CCITT (poly 0x1021, init 0x0000, no reflection) – "123456789" → 0x31C3.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }

        return crc;
    }

    public static byte[] BuildFrame(string command, IEnumerable<(string Name, string Value)> parameters, Encoding? encoding = null)
    {
        encoding ??= Encoding;
        var body = new StringBuilder(command).Append(Tab);
        foreach (var (name, value) in parameters)
            body.Append(name).Append(value).Append(Tab);

        var bodyBytes = encoding.GetBytes(body.ToString());
        var crc = Crc16(bodyBytes);
        var tail = Encoding.ASCII.GetBytes("#" + crc.ToString("X4"));

        var frame = new byte[bodyBytes.Length + tail.Length + 2];
        frame[0] = Stx;
        bodyBytes.CopyTo(frame, 1);
        tail.CopyTo(frame, 1 + bodyBytes.Length);
        frame[^1] = Etx;
        return frame;
    }

    public static PosnetResponse ParseResponse(ReadOnlySpan<byte> frame, Encoding? encoding = null)
    {
        var text = (encoding ?? Encoding).GetString(frame).Trim((char)Stx, (char)Etx);
        var hash = text.LastIndexOf('#');
        var body = hash >= 0 ? text[..hash] : text;
        var fields = body.Split(Tab, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0)
            throw new PosnetException(-1, "Empty printer response.");

        var command = fields[0];
        var values = new Dictionary<string, string>();
        int? error = null;
        foreach (var f in fields.Skip(1))
        {
            if (f.StartsWith('?') && int.TryParse(f[1..], out var e))
                error = e;
            else if (f.StartsWith('@'))
                continue;
            else if (f.Length >= 2)
                values[f[..2]] = f[2..];
        }

        return new PosnetResponse(command, values, error);
    }

    /// <summary>Removes protocol control characters and doubles '&amp;' (formatting character).</summary>
    public static string Sanitize(string text) =>
        new string(text.Where(c => c >= ' ' && c != '\t').ToArray()).Replace("&", "&&");
}

public enum PosnetCodePage
{
    /// <summary>Read from the printer (pccfgget).</summary>
    Auto = -1,
    Windows1250 = 0,
    Latin2 = 1,
    Mazovia = 2
}

public sealed record PosnetResponse(string Command, IReadOnlyDictionary<string, string> Values, int? Error)
{
    public bool IsFrameError => Command == "ERR";
    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;
}

public sealed class PosnetException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
