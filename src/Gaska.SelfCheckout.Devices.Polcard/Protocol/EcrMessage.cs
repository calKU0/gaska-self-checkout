using System.Text;

namespace Gaska.SelfCheckout.Devices.Polcard.Protocol;

/// <summary>
/// ECR-POS/PINPAD application layer message: <c>|ID|field1|field2|...|</c>.
/// </summary>
public sealed class EcrMessage
{
    public EcrMessage(string id, IEnumerable<string?> fields)
    {
        Id = id;
        Fields = fields.Select(f => f ?? "").ToArray();
    }

    public EcrMessage(string id, params string?[] fields) : this(id, (IEnumerable<string?>)fields)
    {
    }

    public string Id { get; }

    /// <summary>Fields after the identifier (index 0 = second message field).</summary>
    public IReadOnlyList<string> Fields { get; }

    public string Field(int index) => index < Fields.Count ? Fields[index] : "";

    public string ToWire()
    {
        var sb = new StringBuilder("|").Append(Id).Append('|');
        foreach (var f in Fields)
            sb.Append(f).Append('|');
        return sb.ToString();
    }

    public static EcrMessage Parse(string wire)
    {
        var text = wire.Trim('\0', '\r', '\n');
        if (!text.StartsWith('|'))
            throw new FormatException($"Invalid ECR message: {wire}");
        var inner = text.EndsWith('|') && text.Length > 1 ? text[1..^1] : text[1..];
        var parts = inner.Split('|');
        return new EcrMessage(parts[0], parts.Skip(1));
    }

    public override string ToString() => ToWire();

    public static string Num(long value, int length) => value.ToString().PadLeft(length, '0');

    /// <summary>Amount in minor units as n12.</summary>
    public static string Amount(decimal amount) => Num((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero), 12);
}

/// <summary>Protocol message identifiers (v4.75).</summary>
public static class EcrMessageIds
{
    // ECR → POS
    public const string EcrHelloExtended = "2841111";
    public const string EcrStartExtended = "2811111";
    public const string EcrGetLastTransaction = "2811211";
    public const string EcrDisplay = "2821000";
    public const string EcrPrompt = "2821211";
    public const string EcrReportStart = "2881311";
    public const string EcrCutoffStart = "2881111";
    public const string EcrPrint = "2821311";

    // POS → ECR
    public const string PosHelloExtended = "2840100";
    public const string PosHello = "2840000";
    public const string PosFinish = "2810000";
    public const string PosLastTransaction = "2810200";
    public const string PinpadDisplay = "2820010";
    public const string PinpadStopDisplay = "2820110";
    public const string PinpadWait = "2830110";
    public const string PosError = "2830010";
    public const string PinpadPrompt = "2820200";
    public const string PinpadReportFinish = "2880500";
    public const string PinpadCutoffFinish = "2880300";
    public const string PosPrint = "2820300";
}
