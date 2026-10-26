namespace Gaska.SelfCheckout.Domain.Customers;

/// <summary>Polish tax identification number (NIP) helpers.</summary>
public static class TaxId
{
    private static readonly int[] Weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];

    /// <summary>Removes separators and an optional "PL" prefix: "PL 592-15-84-599" → "5921584599".</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var text = value.Trim().ToUpperInvariant();
        if (text.StartsWith("PL", StringComparison.Ordinal))
            text = text[2..];
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>Validates a 10-digit Polish NIP including its checksum.</summary>
    public static bool IsValid(string? value)
    {
        var nip = Normalize(value);
        if (nip.Length != 10 || !nip.All(char.IsAsciiDigit) || nip.Distinct().Count() == 1)
            return false;

        var sum = 0;
        for (var i = 0; i < 9; i++)
            sum += (nip[i] - '0') * Weights[i];
        var check = sum % 11;
        return check != 10 && check == nip[9] - '0';
    }

    /// <summary>Formats as XXX-XXX-XX-XX.</summary>
    public static string Format(string? value)
    {
        var nip = Normalize(value);
        return nip.Length == 10 ? $"{nip[..3]}-{nip[3..6]}-{nip[6..8]}-{nip[8..]}" : nip;
    }
}
