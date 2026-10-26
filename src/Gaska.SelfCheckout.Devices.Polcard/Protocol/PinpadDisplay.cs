using System.Text.RegularExpressions;
using Gaska.SelfCheckout.Application.Abstractions;

namespace Gaska.SelfCheckout.Devices.Polcard.Protocol;

/// <summary>&lt;pinpad_display&gt; message 2820010: |2820010|LLL|Text|a|b|[timeout]|</summary>
public sealed partial record PinpadDisplay(string Text, int Display, int KeyboardOptions, int? TimeoutSeconds)
{
    public static PinpadDisplay From(EcrMessage m)
    {
        var text = DecodeText(m.Field(0), m.Field(1));
        _ = int.TryParse(m.Field(2), out var display);
        _ = int.TryParse(m.Field(3), out var kb);
        int? timeout = int.TryParse(m.Field(4), out var t) && t > 0 ? t : null;
        return new PinpadDisplay(text, display, kb, timeout);
    }

    /// <summary>The text may be sent HEX-encoded (protocol rule) – detected by its length.</summary>
    internal static string DecodeText(string lengthField, string text)
    {
        if (int.TryParse(lengthField, out var len) && len > 0 && text.Length == len * 2 && HexRegex().IsMatch(text))
        {
            try
            {
                return EcrEncoding.Text.GetString(Convert.FromHexString(text));
            }
            catch (FormatException)
            {
            }
        }

        return text;
    }

    /// <summary>Options in &lt;n&gt;label$… format (KEYBOARD OPTIONS 5, 6, 8).</summary>
    public (string? Header, IReadOnlyList<TerminalPromptOption> Options) ParseOptions()
    {
        string? header = null;
        var options = new List<TerminalPromptOption>();
        foreach (Match match in OptionRegex().Matches(Text))
        {
            var key = match.Groups[1].Value;
            var label = match.Groups[2].Value.Trim();
            if (int.TryParse(key, out _))
                options.Add(new TerminalPromptOption(key, label));
            else
                header = label; // e.g. <D1>currency selection, <P1>…, <L1>…
        }

        return (header, options);
    }

    /// <summary>Text to show on screen (without option markers).</summary>
    public string DisplayText
    {
        get
        {
            if (KeyboardOptions is 5 or 6 or 8)
            {
                var (header, _) = ParseOptions();
                return header ?? "Wybierz opcję";
            }

            return Text.Replace("$", " ").Trim();
        }
    }

    [GeneratedRegex("<([^>]+)>([^$]*)\\$?")]
    private static partial Regex OptionRegex();

    [GeneratedRegex("^[0-9A-Fa-f]+$")]
    private static partial Regex HexRegex();
}
