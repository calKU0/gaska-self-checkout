using System.Text;

namespace Gaska.SelfCheckout.Devices.Polcard.Protocol;

public sealed record TlvItem(string Tag, byte[] Value)
{
    public string Text => EcrEncoding.Text.GetString(Value).TrimEnd('\0');
    public string Hex => Convert.ToHexString(Value);
    public bool IsPolcard => Tlv.IsPolcardTag(Tag);
    public override string ToString() => $"{Tag}={(IsPolcard ? Text : Hex)}";
}

/// <summary>
/// ECR-POS TLV block coding: the block is transmitted as a HEX (ASCII) string.
/// POLCARD tags (first byte 0x1A-0x3F): T – 2 bytes, L – 2 bytes, V – L bytes.
/// EMV tags: BER-TLV (T 1-2 bytes, L 1-2 bytes).
/// </summary>
public static class Tlv
{
    public static bool IsPolcardTag(string tag) =>
        tag.Length == 4 && byte.TryParse(tag[..2], System.Globalization.NumberStyles.HexNumber, null, out var b) && b is >= 0x1A and <= 0x3F;

    public static IReadOnlyList<TlvItem> Parse(string hex)
    {
        var items = new List<TlvItem>();
        if (string.IsNullOrWhiteSpace(hex))
            return items;

        var data = Convert.FromHexString(hex.Trim());
        var i = 0;
        while (i < data.Length)
        {
            string tag;
            int length;
            if (data[i] is >= 0x1A and <= 0x3F && i + 4 <= data.Length)
            {
                tag = Convert.ToHexString(data, i, 2);
                length = (data[i + 2] << 8) | data[i + 3];
                i += 4;
            }
            else
            {
                // EMV BER-TLV
                var tagLen = (data[i] & 0x1F) == 0x1F ? 2 : 1;
                if (i + tagLen > data.Length) break;
                tag = Convert.ToHexString(data, i, tagLen);
                i += tagLen;
                if (i >= data.Length) break;
                if ((data[i] & 0x80) == 0)
                {
                    length = data[i];
                    i += 1;
                }
                else
                {
                    var lenBytes = data[i] & 0x7F;
                    length = 0;
                    for (var k = 1; k <= lenBytes && i + k < data.Length; k++)
                        length = (length << 8) | data[i + k];
                    i += 1 + lenBytes;
                }
            }

            if (length < 0 || i + length > data.Length)
                length = Math.Max(0, data.Length - i); // damaged block – take the rest
            items.Add(new TlvItem(tag, data.AsSpan(i, length).ToArray()));
            i += length;
        }

        return items;
    }

    /// <summary>Builds a TLV block of POLCARD tags (text values).</summary>
    public static string BuildPolcard(IEnumerable<(string Tag, string Value)> tags)
    {
        var sb = new StringBuilder();
        foreach (var (tag, value) in tags)
        {
            var bytes = EcrEncoding.Text.GetBytes(value);
            sb.Append(tag.ToUpperInvariant()).Append(bytes.Length.ToString("X4")).Append(Convert.ToHexString(bytes));
        }

        return sb.ToString();
    }
}

/// <summary>Tag dictionary of a TLV block (last value wins on repetition; the full list is kept too).</summary>
public sealed class TlvBag
{
    private readonly Dictionary<string, TlvItem> _map = new(StringComparer.OrdinalIgnoreCase);

    public TlvBag(IEnumerable<TlvItem> items)
    {
        Items = items.ToList();
        foreach (var item in Items)
            _map[item.Tag] = item;
    }

    public IReadOnlyList<TlvItem> Items { get; }
    public bool Has(string tag) => _map.ContainsKey(tag);
    public string? Text(string tag) => _map.TryGetValue(tag, out var v) ? v.Text.Trim() : null;
    public string? Hex(string tag) => _map.TryGetValue(tag, out var v) ? v.Hex : null;
    public IReadOnlyDictionary<string, string> ToDictionary() => _map.ToDictionary(k => k.Key, v => v.Value.IsPolcard ? v.Value.Text : v.Value.Hex);
}

public static class EcrEncoding
{
    /// <summary>Polcard terminal texts use the Windows-1250 code page.</summary>
    public static Encoding Text { get; } = CreateText();

    private static Encoding CreateText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1250);
    }
}
