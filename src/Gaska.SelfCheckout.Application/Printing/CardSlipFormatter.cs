using System.Globalization;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Domain.Payments;

namespace Gaska.SelfCheckout.Application.Printing;

public enum SlipCopy
{
    Cardholder,
    Merchant
}

/// <summary>
/// Builds the card payment confirmation according to the Fiserv/Polcard requirements
/// ("Payment card acceptance through ECR", chapter 15 – Transaction receipt printing) and the receipt templates of the
/// certification sheet (sheet "printouts"). A DCC receipt is printed in English, as the sheet requires.
/// </summary>
public sealed class CardSlipFormatter
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public PrintDocument Format(CardTransaction tx, SlipCopy copy, int width, string stationId)
    {
        var l = new List<PrintLine>();
        var kind = tx.Kind == CardTransactionKind.Reversal
            ? PrintDocumentKind.CardReversal
            : tx.IsApproved ? PrintDocumentKind.CardPayment : PrintDocumentKind.CardDeclined;
        var en = tx.Dcc is not null;

        // Card acceptor
        l.Add(PrintLine.Blank);
        AddCentered(l, tx.MerchantName, bold: true);
        AddCentered(l, tx.MerchantStreet);
        AddCentered(l, tx.MerchantCity);
        l.Add(PrintLine.Blank);

        l.Add(new PrintLine(Cols($"KASA: {stationId}", $"MID: {tx.MerchantId}", width)));
        l.Add(new PrintLine(Cols("", $"TID: {tx.TerminalId}", width)));
        if (!string.IsNullOrEmpty(tx.ReceiptNumber))
            l.Add(new PrintLine(Cols("", $"{(en ? "RECEIPT NO" : "RACH. NR")}: {tx.ReceiptNumber}", width)));

        // Card / BLIK
        if (!string.IsNullOrEmpty(tx.CardName) || !string.IsNullOrEmpty(tx.MaskedPan))
        {
            var cardLeft = $"{tx.CardName}{(string.IsNullOrEmpty(tx.ReadMethod) ? "" : $" ({tx.ReadMethod})")}";
            var expiry = MaskIfRequired(tx.ExpiryDate, copy, tx.MaskingRules, concealIndex: 3, copyIndex: 4);
            l.Add(new PrintLine(Cols(cardLeft, string.IsNullOrEmpty(expiry) ? "" : $"{(en ? "VALID DATE" : "WAŻNA DO")}: {expiry}", width)));
            if (!string.IsNullOrEmpty(tx.MaskedPan))
                l.Add(new PrintLine(tx.MaskedPan));
        }

        l.Add(PrintLine.Blank);
        if (tx.Kind == CardTransactionKind.Reversal)
        {
            // template "Reversal": the original sale, then what happened to it
            l.Add(new PrintLine(Cols("SPRZEDAŻ:", $"{Money(tx.Amount)} {tx.Currency}", width), Bold: true));
            l.Add(PrintLine.Center("UNIEWAŻNIENIE", bold: true));
            if (!string.IsNullOrEmpty(tx.OriginalReceiptNumber))
                l.Add(PrintLine.Center($"DLA RACHUNKU: {tx.OriginalReceiptNumber}"));
        }
        else
        {
            l.Add(new PrintLine(Cols(KindLabel(tx, en), $"{Money(tx.Amount)} {tx.Currency}", width), Bold: true));
        }

        if (tx.IsBlik)
        {
            if (!string.IsNullOrEmpty(tx.BlikCode))
                l.Add(new PrintLine(Cols("PŁATNOŚĆ BLIK:", $"{tx.BlikCode}{(string.IsNullOrEmpty(tx.BlikEntryMode) ? "" : $" ({tx.BlikEntryMode})")}", width)));
            if (!string.IsNullOrEmpty(tx.MobileReference))
                l.Add(new PrintLine(Cols("NR REFERENCYJNY:", tx.MobileReference!, width)));
        }

        if (tx.Dcc is { } dcc && tx.IsApproved)
        {
            if (dcc.ExchangeRate is { } rate)
            {
                var rateText = !string.IsNullOrEmpty(dcc.ConversionFactor) && dcc.ConversionFactor != "0"
                    ? $"{dcc.ConversionFactor} {dcc.Currency} = {rate.ToString("0.######", CultureInfo.InvariantCulture)} {tx.Currency}"
                    : $"1 {tx.Currency} = {rate.ToString("0.######", CultureInfo.InvariantCulture)} {dcc.Currency}";
                l.Add(new PrintLine(Cols("EXCHANGE RATE:", rateText, width)));
            }
            if (!string.IsNullOrEmpty(dcc.Markup))
                l.Add(new PrintLine(Cols("CURRENCY CONVERSION FEE:", $"{dcc.Markup}%", width)));
            if (!string.IsNullOrEmpty(dcc.Currency))
                l.Add(new PrintLine(Cols("TRANSACTION CURRENCY:", dcc.Currency!, width)));
            if (!string.IsNullOrEmpty(dcc.Amount))
                l.Add(new PrintLine(Cols("TOTAL TRANSACTION AMOUNT:", $"{dcc.Amount} {dcc.Currency}", width), Bold: true));
        }

        l.Add(PrintLine.Blank);
        if (tx.IsApproved)
        {
            if (copy == SlipCopy.Merchant && tx.Kind == CardTransactionKind.Sale)
                l.Add(PrintLine.Center(en ? "PLEASE DEBIT MY ACCOUNT" : "PROSZĘ OBCIĄŻYĆ MOJE KONTO"));
            else if (copy == SlipCopy.Merchant && tx.Kind == CardTransactionKind.Refund)
                l.Add(PrintLine.Center(en ? "PLEASE CREDIT MY ACCOUNT" : "PROSZĘ UZNAĆ MOJE KONTO"));
            if (tx.Kind == CardTransactionKind.Reversal)
                l.Add(PrintLine.Center("TRANSAKCJA UNIEWAŻNIONA", bold: true));

            if (tx.Dcc?.Disclaimer is { Length: > 0 } disclaimer)
                foreach (var line in Wrap(disclaimer, width))
                    l.Add(new PrintLine(line));

            if (tx.CdcvmVerified && !string.IsNullOrEmpty(tx.CdcvmText))
                l.Add(PrintLine.Center(tx.CdcvmText!));
            else if (tx.PinVerified && tx.Kind != CardTransactionKind.Refund)
                l.Add(PrintLine.Center(en ? "PIN CODE VERIFIED" : "KOD PIN ZGODNY"));

            var auth = MaskIfRequired(tx.AuthorizationCode, copy, tx.MaskingRules, concealIndex: 5, copyIndex: 6);
            if (!string.IsNullOrEmpty(auth))
                l.Add(PrintLine.Center($"{(en ? "AUTH CODE" : "KOD AUTORYZACJI")}: "
                                       + (string.IsNullOrEmpty(tx.AuthorizationCodeSource) ? "" : $"({tx.AuthorizationCodeSource}) ") + auth));
        }
        else if (tx.Outcome == CardTransactionOutcome.Cancelled)
        {
            l.Add(PrintLine.Center("TRANSAKCJA PRZERWANA", bold: true, wide: true));
        }
        else
        {
            l.Add(PrintLine.Center("TRANSAKCJA ODRZUCONA", bold: true));
            l.Add(PrintLine.Center("ODMOWA", bold: true, wide: true));
            if (!string.IsNullOrEmpty(tx.DeclineText) && !string.Equals(tx.DeclineText.Trim(), "ODMOWA", StringComparison.OrdinalIgnoreCase))
                foreach (var line in Wrap(tx.DeclineText!, width))
                    l.Add(PrintLine.Center(line));
            // the authorization host's response code (tag 8A, e.g. 05) explains the decline; the terminal result code
            // (e.g. 4407) only when the host was not reached
            var responseCode = !string.IsNullOrEmpty(tx.AuthorizationResponseCode) ? tx.AuthorizationResponseCode : tx.ResultCode;
            if (!string.IsNullOrEmpty(responseCode))
                l.Add(PrintLine.Center($"KOD ODPOWIEDZI: {responseCode}"));
        }

        // EMV data of chip and contactless transactions
        if (!string.IsNullOrEmpty(tx.Aid))
            l.Add(new PrintLine($"AID: {tx.Aid}"));
        if (!string.IsNullOrEmpty(tx.Atc))
            l.Add(new PrintLine($"ATC: {tx.Atc}"));
        if (!string.IsNullOrEmpty(tx.ApplicationCryptogram))
            l.Add(new PrintLine($"TC: {tx.ApplicationCryptogram}"));

        if (tx.IsApproved && tx.SignatureRequired && copy == SlipCopy.Merchant)
        {
            l.Add(PrintLine.Blank);
            l.Add(PrintLine.Blank);
            l.Add(PrintLine.Center(new string('.', Math.Min(width, 32))));
            l.Add(PrintLine.Center(en ? "SIGNATURE" : "PODPIS"));
        }

        l.Add(PrintLine.Blank);
        if (copy == SlipCopy.Cardholder)
        {
            l.Add(PrintLine.Center(en ? "CUSTOMER RECEIPT" : "RACHUNEK DLA KLIENTA", bold: true));
            if (!en)
                l.Add(PrintLine.Center("ZAPRASZAMY PONOWNIE"));
            l.Add(PrintLine.Center(en ? "PLEASE RETAIN THIS RECEIPT" : "PROSZĘ ZACHOWAĆ RACHUNEK"));
        }
        else
        {
            l.Add(PrintLine.Center(en ? "MERCHANT COPY" : "KOPIA DLA SPRZEDAWCY", bold: true));
        }

        if (copy == SlipCopy.Cardholder)
        {
            if (tx.IsApproved && !string.IsNullOrEmpty(tx.DccOptOutText))
                foreach (var line in Wrap(tx.DccOptOutText!, width)) l.Add(PrintLine.Center(line));
            // 1A17 – message from the terminal parameters, on the cardholder copy of every result
            if (!string.IsNullOrEmpty(tx.AdditionalText))
                foreach (var line in Wrap(tx.AdditionalText!, width)) l.Add(PrintLine.Center(line));
        }

        if (tx.IsContactless)
        {
            l.Add(PrintLine.Blank);
            l.Add(PrintLine.Center("ZBLIŻENIOWO / CONTACTLESS"));
        }

        if (tx.IsApproved)
        {
            if (!string.IsNullOrEmpty(tx.InstallmentText))
                foreach (var line in Wrap(tx.InstallmentText!, width)) l.Add(PrintLine.Center(line));
            if (!string.IsNullOrEmpty(tx.InstallmentIssuerText))
                foreach (var line in Wrap(tx.InstallmentIssuerText!, width)) l.Add(PrintLine.Center(line));
        }

        l.Add(PrintLine.Blank);
        var ts = tx.Timestamp ?? DateTime.Now;
        l.Add(new PrintLine(Cols($"DATA: {ts:yyyy-MM-dd}", $"GODZINA: {ts:HH:mm:ss}", width)));

        return new PrintDocument(kind, l);
    }

    private static string KindLabel(CardTransaction tx, bool en) => tx.Kind switch
    {
        CardTransactionKind.Refund => en ? "REFUND AMOUNT:" : "UZNANIE:",
        _ => en ? "SALE AMOUNT:" : "SPRZEDAŻ:"
    };

    private static void AddCentered(List<PrintLine> lines, string? text, bool bold = false)
    {
        if (!string.IsNullOrWhiteSpace(text))
            lines.Add(PrintLine.Center(text.Trim(), bold));
    }

    /// <summary>
    /// Masking according to tag 1B02: position <paramref name="concealIndex"/> = 1 → conceal,
    /// position <paramref name="copyIndex"/>: 0 – both copies, 1 – cardholder copy only.
    /// </summary>
    private static string? MaskIfRequired(string? value, SlipCopy copy, string? rules, int concealIndex, int copyIndex)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(rules) || rules.Length <= concealIndex || rules[concealIndex] != '1')
            return value;
        var onlyCardholder = rules.Length > copyIndex && rules[copyIndex] == '1';
        if (onlyCardholder && copy == SlipCopy.Merchant)
            return value;
        return new string('*', value.Length);
    }

    public static string Money(decimal amount) => amount.ToString("#,##0.00", Pl);

    public static string Cols(string left, string right, int width)
    {
        left ??= "";
        right ??= "";
        var space = width - left.Length - right.Length;
        if (space >= 1)
            return left + new string(' ', space) + right;
        // does not fit – right column goes to a new line
        return left.Length == 0 ? right.PadLeft(width) : left + "\n" + right.PadLeft(width);
    }

    public static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            foreach (var w in words)
            {
                var word = w;
                while (word.Length > width)
                {
                    if (current.Length > 0) { yield return current; current = ""; }
                    yield return word[..width];
                    word = word[width..];
                }
                if (current.Length == 0) current = word;
                else if (current.Length + 1 + word.Length <= width) current += " " + word;
                else { yield return current; current = word; }
            }
            if (current.Length > 0) yield return current;
        }
    }
}
