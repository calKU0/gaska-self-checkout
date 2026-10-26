using System.Globalization;
using Gaska.SelfCheckout.Application.Abstractions;

namespace Gaska.SelfCheckout.Devices.Polcard.Protocol;

/// <summary>
/// Report from &lt;pinpad_report_finish&gt;: header, transaction list (each starts with 9F21) and summary –
/// upload report: 1C13 per type, 1C17 total, 1C18 DCC; cumulative (informational) report: 1C14 per card system,
/// 1C15 total, 1C16 start of the counted period.
/// </summary>
public static class UploadReportBuilder
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static TerminalReport Build(IReadOnlyList<TlvItem> items, string? batchNo, string? transactionCount)
    {
        var header = new List<TlvItem>();
        var transactions = new List<List<TlvItem>>();
        var summary = new List<TlvItem>();
        List<TlvItem>? current = null;

        foreach (var item in items)
        {
            if (item.Tag is "1C13" or "1C14" or "1C15" or "1C16" or "1C17" or "1C18" or "1B22" or "1B23" || item.Tag.StartsWith("1D", StringComparison.Ordinal))
            {
                summary.Add(item);
                continue;
            }

            if (item.Tag == "9F21")
            {
                current = [];
                transactions.Add(current);
            }

            (current ?? header).Add(item);
        }

        var h = new TlvBag(header);
        var lines = new List<string>();
        Add(lines, h.Text("1B21"));
        Add(lines, h.Text("1A1C"));
        Add(lines, h.Text("1A1D"));
        Add(lines, h.Text("1A1E"));
        lines.Add($"MID: {h.Text("1A1F")}   TID: {h.Text("1A20")}");
        if (!string.IsNullOrEmpty(batchNo))
            lines.Add($"Nr pliku: {batchNo}   Transakcji: {(transactionCount?.TrimStart('0') is { Length: > 0 } count ? count : "0")}");
        lines.Add($"Data raportu: {DateTime.Now:yyyy-MM-dd HH:mm}");
        lines.Add("");

        foreach (var tx in transactions)
        {
            var t = new TlvBag(tx);
            var amount = PosFinishMapper.ParseBcdAmount(t.Hex("9F02"));
            var ts = PosFinishMapper.ParseTimestamp(t.Hex("9A"), t.Hex("9F21"));
            var pan = t.Text("1A05") is { Length: >= 4 } p ? p[^4..] : t.Text("1B09");
            lines.Add($"{t.Text("1B01")} {t.Text("1B12") ?? t.Hex("9C")} {ts:dd.MM HH:mm} {pan} {t.Text("1A06")} {amount?.ToString("N2", Pl)} {t.Text("1A1B")}");
            lines.Add($"   {t.Text("1A04")} AUT: {t.Text("1A08")}({t.Text("1A07")})");
        }

        if (transactions.Count > 0)
            lines.Add("");

        var s = new TlvBag(summary);
        if (s.Text("1C13") is { Length: > 0 } perType)
        {
            lines.Add("ZESTAWIENIE RAZEM");
            lines.AddRange(FormatSummary(perType));
        }

        if (s.Text("1C17") is { Length: > 0 } total)
            lines.AddRange(FormatSummary("RAZEM;" + total.Trim('$').Split('$').Last()));

        if (s.Text("1C18") is { Length: > 0 } dcc)
        {
            lines.Add("W TYM DCC");
            lines.AddRange(FormatSummary(dcc));
        }

        // cumulative report: per card system and in total; rows without transactions are left out
        if (s.Text("1C16") is { Length: > 0 } since)
            lines.Add($"Liczniki od: {since}");
        if (s.Text("1C14") is { Length: > 0 } perCard)
            lines.AddRange(FormatSummary(perCard, skipEmpty: true));
        if (s.Text("1C15") is { Length: > 0 } cumulative)
        {
            lines.Add("");
            lines.AddRange(FormatSummary(cumulative, skipEmpty: true));
        }

        if (s.Text("1B23") is { Length: > 0 } mismatch)
            lines.Add(mismatch);

        return new TerminalReport(true, "Raport wysyłki / zamknięcie dnia", lines, null);
    }

    private static IEnumerable<string> FormatSummary(string data, bool skipEmpty = false)
    {
        foreach (var entry in data.Split('$', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(';');
            if (parts.Length < 2)
            {
                yield return entry; // MID/PID, card system, section name
                continue;
            }

            if (skipEmpty && parts[1].Trim() is "0" or "" && !parts[0].StartsWith("RAZEM", StringComparison.OrdinalIgnoreCase))
                continue;

            var amount = parts.Length > 2 && long.TryParse(parts[2].Trim(), out var minor) ? (minor / 100m).ToString("N2", Pl) : "";
            var label = parts[0].Trim();
            yield return $"{(label.Length > 22 ? label[..22] : label),-22}{parts[1].Trim(),5}{amount,13}";
        }
    }

    private static void Add(List<string> lines, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            lines.Add(text);
    }
}
