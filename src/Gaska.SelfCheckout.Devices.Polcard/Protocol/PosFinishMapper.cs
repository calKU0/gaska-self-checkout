using System.Globalization;
using Gaska.SelfCheckout.Domain.Payments;

namespace Gaska.SelfCheckout.Devices.Polcard.Protocol;

/// <summary>Authorization response &lt;pos_finish&gt; / &lt;pos_last_transaction&gt; assembled from all parts.</summary>
public sealed record PosFinish(string CardReadType, string AuthorizationResult, TlvBag Tags);

public static class PosFinishMapper
{
    private static readonly Dictionary<string, string> ResultDescriptions = new()
    {
        ["0000"] = "Transakcja zaakceptowana",
        ["0002"] = "Zaakceptowano na częściową kwotę",
        ["0003"] = "Automatycznie zaakceptowano na częściową kwotę",
        ["1000"] = "Transakcja odrzucona",
        ["4400"] = "Nie znaleziono transakcji oryginalnej",
        ["4401"] = "Transakcja oryginalna została już rozliczona",
        ["4402"] = "Transakcja oryginalna nie była zaakceptowana",
        ["4403"] = "Kwota unieważnienia niezgodna z transakcją oryginalną",
        ["4407"] = "Terminal nie mógł zakończyć transakcji",
        ["4408"] = "Niepoprawna konfiguracja profilu akceptanta",
        ["4411"] = "Pamięć transakcji terminala pełna – wymagane zamknięcie dnia",
        ["4412"] = "System karty niezgodny z numerem karty",
        ["4413"] = "System karty nieobsługiwany przez terminal",
        ["4415"] = "Wymagane pobranie konfiguracji terminala (zamknięcie dnia)",
        ["4615"] = "Zbyt niski poziom baterii terminala",
        ["4616"] = "Zbyt słaby sygnał GSM terminala",
        ["9000"] = "Brak ostatniej transakcji w terminalu"
    };

    private static readonly Dictionary<string, string> CardReadTypes = new()
    {
        ["6"] = "Nie można odczytać karty",
        ["7"] = "Błąd odczytu karty",
        ["8"] = "Nie odczytano karty (przerwano lub upłynął czas)"
    };

    public static string Describe(string resultCode) =>
        ResultDescriptions.TryGetValue(resultCode, out var d) ? d : $"Transakcja odrzucona (kod {resultCode})";

    public static bool IsApproved(string resultCode) => resultCode is "0000" or "0002" or "0003";

    public static CardTransaction Map(PosFinish finish, decimal requestedAmount, bool abortRequested, bool reversal)
    {
        var t = finish.Tags;
        var approved = IsApproved(finish.AuthorizationResult);

        // card not read (read error / timeout / red key on the PIN pad before the card was read): nothing was sent to the
        // authorization host, so no money could have been taken – shown as a cancelled attempt the customer can repeat
        var cardNotRead = CardReadTypes.ContainsKey(finish.CardReadType) && t.Text("8A") is null or "";
        var outcome = approved
            ? CardTransactionOutcome.Approved
            : abortRequested || cardNotRead ? CardTransactionOutcome.Cancelled : CardTransactionOutcome.Declined;

        var description = approved
            ? Describe(finish.AuthorizationResult)
            : t.Text("1B03") is { Length: > 0 } hostText
                ? hostText
                : (cardNotRead || finish.AuthorizationResult == "1000") && CardReadTypes.TryGetValue(finish.CardReadType, out var readError)
                    ? readError
                    : Describe(finish.AuthorizationResult);

        var kind = reversal
            ? CardTransactionKind.Reversal
            : t.Text("1B12") switch
            {
                "20" => CardTransactionKind.Refund,
                "00" or "09" or null or "" => CardTransactionKind.Sale,
                _ => CardTransactionKind.Other
            };

        return new CardTransaction
        {
            Outcome = outcome,
            Kind = kind,
            ResultCode = finish.AuthorizationResult,
            ResultDescription = description,
            Amount = ParseBcdAmount(t.Hex("9F02")) ?? requestedAmount,
            Currency = t.Text("1A1B") is { Length: > 0 } cur ? cur : "PLN",
            Timestamp = ParseTimestamp(t.Hex("9A"), t.Hex("9F21")),
            MerchantName = t.Text("1A1C"),
            MerchantStreet = t.Text("1A1D"),
            MerchantCity = t.Text("1A1E"),
            MerchantId = t.Text("1A1F"),
            TerminalId = t.Text("1A20"),
            ReceiptNumber = t.Text("1B01"),
            CardName = t.Text("1A04"),
            MaskedPan = t.Text("1A05"),
            ExpiryDate = t.Text("1A0D"),
            ReadMethod = t.Text("1A06"),
            AuthorizationCodeSource = t.Text("1A07"),
            AuthorizationCode = t.Text("1A08"),
            Aid = t.Hex("4F"),
            Atc = t.Hex("9F36"),
            ApplicationCryptogram = t.Hex("9F26"),
            ApplicationLabel = t.Text("50"),
            CvmList = t.Text("1B08"),
            CdcvmText = t.Text("1C05"),
            DeclineText = t.Text("1B03"),
            AuthorizationResponseCode = t.Text("8A"),
            MobileReference = t.Text("1B09"),
            BlikCode = t.Text("1B10"),
            BlikEntryMode = t.Text("1B11"),
            MaskingRules = t.Text("1B02"),
            AdditionalText = t.Text("1A17"),
            DccOptOutText = t.Text("1A19"),
            InstallmentText = t.Text("1C25"),
            InstallmentIssuerText = t.Text("1C26"),
            EcrTransactionId = t.Text("1C06"),
            Dcc = t.Has("1A0B") || t.Has("1A0C")
                ? new DccInfo(
                    decimal.TryParse(t.Text("1A0A")?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) ? rate : null,
                    t.Text("1A0B"),
                    FormatMinorUnits(t.Text("1A0C"), t.Text("1C07")),
                    t.Text("1B05"),
                    t.Text("1A15"),
                    t.Text("1C1A"))
                : null,
            RawTags = t.ToDictionary()
        };
    }

    /// <summary>EMV n12 (BCD) amount – the last two digits are minor units.</summary>
    public static decimal? ParseBcdAmount(string? hex) =>
        !string.IsNullOrEmpty(hex) && long.TryParse(hex, out var minor) ? minor / 100m : null;

    public static DateTime? ParseTimestamp(string? dateHex, string? timeHex)
    {
        if (string.IsNullOrEmpty(dateHex) || dateHex.Length < 6)
            return null;
        var text = dateHex[..6] + (timeHex is { Length: >= 6 } ? timeHex[..6] : "000000");
        return DateTime.TryParseExact(text, "yyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    private static string? FormatMinorUnits(string? amount, string? exponent)
    {
        if (string.IsNullOrEmpty(amount))
            return null;
        if (!long.TryParse(amount, out var minor) || !int.TryParse(exponent, out var exp) || exp is < 0 or > 4 || amount.Contains('.') || amount.Contains(','))
            return amount;
        var value = minor / (decimal)Math.Pow(10, exp);
        return value.ToString("N" + exp, CultureInfo.GetCultureInfo("pl-PL"));
    }
}
