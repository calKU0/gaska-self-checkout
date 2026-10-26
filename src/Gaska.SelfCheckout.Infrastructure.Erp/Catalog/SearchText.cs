using System.Globalization;
using System.Text;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

/// <summary>Search text normalization: upper case without Polish diacritics; a "compact" variant without separators (codes).</summary>
public static class SearchText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var decomposed = text.ToUpperInvariant().Replace('Ł', 'L').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Letters and digits only – "00.1001.000.34" → "00100100034".</summary>
    public static string Compact(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        return sb.ToString();
    }

    public static string[] Tokens(string query) =>
        Normalize(query).Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
}
