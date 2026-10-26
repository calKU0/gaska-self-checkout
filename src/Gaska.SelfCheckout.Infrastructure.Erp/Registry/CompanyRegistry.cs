using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Domain.Customers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Registry;

/// <summary>
/// Company lookup by tax id: GUS REGON database (BIR 1.1) first, the Ministry of Finance VAT white list as a fallback.
/// </summary>
public sealed class CompanyRegistry(GusBirClient gus, VatWhiteListClient whiteList, IOptionsMonitor<ErpOptions> options, ILogger<CompanyRegistry> logger)
    : ICompanyRegistry
{
    public async Task<CompanyRegistryEntry?> FindByTaxIdAsync(string taxId, CancellationToken ct = default)
    {
        var nip = TaxId.Normalize(taxId);
        if (!TaxId.IsValid(nip))
            return null;

        var o = options.CurrentValue.Registry;
        if (o.GusEnabled)
        {
            try
            {
                if (await gus.FindByTaxIdAsync(nip, ct) is { } entry)
                    return entry;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "GUS lookup failed for tax id {TaxId}", nip);
            }
        }

        if (o.WhiteListEnabled)
        {
            try
            {
                return await whiteList.FindByTaxIdAsync(nip, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "VAT white list lookup failed for tax id {TaxId}", nip);
            }
        }

        return null;
    }
}

/// <summary>Client of the GUS REGON BIR 1.1 web service (SOAP 1.2, WS-Addressing, MTOM responses).</summary>
public sealed partial class GusBirClient(HttpClient http, IOptionsMonitor<ErpOptions> options, ILogger<GusBirClient> logger)
{
    private const string Namespace = "http://CIS/BIR/PUBL/2014/07";
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _sid;
    private DateTime _sidExpires;

    public async Task<CompanyRegistryEntry?> FindByTaxIdAsync(string nip, CancellationToken ct)
    {
        var sid = await LoginAsync(ct);
        var result = await CallAsync("DaneSzukajPodmioty",
            $"<ns:pParametryWyszukiwania><dat:Nip>{nip}</dat:Nip></ns:pParametryWyszukiwania>", sid, ct);
        if (string.IsNullOrWhiteSpace(result))
            return null;

        var data = XDocument.Parse(result).Root?.Element("dane");
        if (data is null || data.Element("ErrorCode") is not null)
            return null;

        string? Get(string name) => data.Element(name)?.Value is { Length: > 0 } v ? v.Trim() : null;
        if (Get("DataZakonczeniaDzialalnosci") is not null)
            logger.LogInformation("GUS: company {TaxId} has ended its activity", nip);

        return new CompanyRegistryEntry(
            nip, Get("Nazwa") ?? nip, Get("Regon"),
            Get("Ulica")?.Replace("ul. ", "", StringComparison.OrdinalIgnoreCase), Get("NrNieruchomosci"), Get("NrLokalu"),
            Get("KodPocztowy"), Get("Miejscowosc"), Get("MiejscowoscPoczty"),
            Capitalize(Get("Wojewodztwo")), Capitalize(Get("Powiat")), Capitalize(Get("Gmina")), "GUS");
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        await _loginGate.WaitAsync(ct);
        try
        {
            if (_sid is not null && DateTime.UtcNow < _sidExpires)
                return _sid;
            var key = SecurityElement.Escape(options.CurrentValue.Registry.GusKey);
            var sid = await CallAsync("Zaloguj", $"<ns:pKluczUzytkownika>{key}</ns:pKluczUzytkownika>", null, ct);
            if (string.IsNullOrWhiteSpace(sid))
                throw new InvalidOperationException("GUS login failed (invalid user key?).");
            _sid = sid;
            _sidExpires = DateTime.UtcNow.AddMinutes(50);
            return sid;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task<string?> CallAsync(string operation, string body, string? sid, CancellationToken ct)
    {
        var o = options.CurrentValue.Registry;
        var envelope = $"""
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:ns="{Namespace}" xmlns:dat="{Namespace}/DataContract">
              <soap:Header xmlns:wsa="http://www.w3.org/2005/08/addressing">
                <wsa:To>{o.GusUrl}</wsa:To>
                <wsa:Action>{Namespace}/IUslugaBIRzewnPubl/{operation}</wsa:Action>
              </soap:Header>
              <soap:Body><ns:{operation}>{body}</ns:{operation}></soap:Body>
            </soap:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, o.GusUrl)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml")
        };
        if (sid is not null)
            request.Headers.Add("sid", sid);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        using var response = await http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cts.Token);

        // MTOM: the SOAP envelope is one of the multipart sections
        var match = EnvelopeRegex().Match(text);
        if (!match.Success)
            throw new InvalidOperationException("Unexpected GUS response.");
        var xml = XDocument.Parse(match.Value);
        return xml.Descendants(XName.Get($"{operation}Result", Namespace)).FirstOrDefault()?.Value;
    }

    private static string? Capitalize(string? value) =>
        string.IsNullOrEmpty(value) ? value : System.Globalization.CultureInfo.GetCultureInfo("pl-PL").TextInfo.ToTitleCase(value.ToLowerInvariant());

    [GeneratedRegex("<s:Envelope.*</s:Envelope>", RegexOptions.Singleline)]
    private static partial Regex EnvelopeRegex();
}

/// <summary>Client of the Ministry of Finance VAT taxpayer white list (wl-api.mf.gov.pl).</summary>
public sealed partial class VatWhiteListClient(HttpClient http, IOptionsMonitor<ErpOptions> options)
{
    public async Task<CompanyRegistryEntry?> FindByTaxIdAsync(string nip, CancellationToken ct)
    {
        var o = options.CurrentValue.Registry;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        using var response = await http.GetAsync($"{o.WhiteListUrl.TrimEnd('/')}/api/search/nip/{nip}?date={DateTime.Today:yyyy-MM-dd}", cts.Token);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cts.Token);
        if (!json.TryGetProperty("result", out var result) || !result.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.Object)
            return null;

        string? Get(string name) => subject.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var address = Get("workingAddress") ?? Get("residenceAddress") ?? "";

        // "AL. JANA PAWŁA II 39A, 31-864 KRAKÓW"
        string? street = address, postalCode = null, city = null;
        var m = AddressRegex().Match(address);
        if (m.Success)
        {
            street = m.Groups["street"].Value.Trim();
            postalCode = m.Groups["postal"].Value;
            city = m.Groups["city"].Value.Trim();
        }

        return new CompanyRegistryEntry(nip, Get("name") ?? nip, Get("regon"), street, null, null, postalCode, city, city,
            null, null, null, "Biała lista VAT");
    }

    [GeneratedRegex(@"^(?<street>.*),\s*(?<postal>\d{2}-\d{3})\s+(?<city>.+)$")]
    private static partial Regex AddressRegex();
}
