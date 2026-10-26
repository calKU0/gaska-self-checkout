using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

/// <summary>One search request to the product index.</summary>
/// <param name="OnlyIds">Restrict to these products (null = no restriction, empty = nothing).</param>
/// <param name="ExcludedIds">Leave these products out.</param>
public sealed record IndexQuery(string? Text, int? CategoryId, IReadOnlyCollection<int>? OnlyIds, IReadOnlyCollection<int>? ExcludedIds, int From, int Size);

public sealed record IndexHits(IReadOnlyList<int> Ids, int Total);

/// <summary>
/// Product search index in Elasticsearch 7: returns product ids only – prices and stock always come from XL. Plain HTTP + System.Text.Json, no client library.
/// </summary>
public sealed class ElasticProductIndex(IHttpClientFactory httpFactory, IOptionsMonitor<ErpOptions> options)
{
    public const string HttpClientName = "elasticsearch";

    /// <summary>Query analyzer of the index: whitespace tokens, lower case, no diacritics/punctuation, synonyms.</summary>
    private const string Analyzer = "whitespace_ascii";

    private const int ScrollSlices = 4;
    private const int ScrollPageSize = 10_000;
    private static readonly TimeSpan LinksTimeout = TimeSpan.FromMinutes(2);

    public async Task<IndexHits> SearchAsync(IndexQuery query, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{IndexPath}/_search?filter_path=hits.total,hits.hits.fields",
            BuildSearch(query), Timeout, ct);
        var hits = doc.RootElement.GetProperty("hits");
        var ids = new List<int>(query.Size);
        if (hits.TryGetProperty("hits", out var list))
            foreach (var hit in list.EnumerateArray())
                if (hit.TryGetProperty("fields", out var fields) && fields.TryGetProperty("id", out var id) && id.GetArrayLength() > 0)
                    ids.Add(id[0].GetInt32());
        return new IndexHits(ids, hits.GetProperty("total").GetProperty("value").GetInt32());
    }

    /// <summary>Number of documents in the index (status in the service panel).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{IndexPath}/_count", null, Timeout, ct);
        return doc.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>
    /// Product groups of every product in the index (product id → TwG_GIDNumer of its groups). ~110k documents –
    /// read with a sliced scroll in parallel (a few seconds).
    /// </summary>
    public async Task<Dictionary<int, int[]>> LoadCategoryLinksAsync(CancellationToken ct = default)
    {
        var slices = await Task.WhenAll(Enumerable.Range(0, ScrollSlices).Select(slice => LoadSliceAsync(slice, ct)));
        var result = new Dictionary<int, int[]>(slices.Sum(s => s.Count));
        foreach (var slice in slices)
            foreach (var (id, groups) in slice)
                result[id] = groups;
        return result;
    }

    private async Task<List<(int Id, int[] Groups)>> LoadSliceAsync(int slice, CancellationToken ct)
    {
        var result = new List<(int, int[])>();
        var body = Json(w =>
        {
            w.WriteStartObject("slice");
            w.WriteNumber("id", slice);
            w.WriteNumber("max", ScrollSlices);
            w.WriteEndObject();
            w.WriteNumber("size", ScrollPageSize);
            w.WriteStartArray("sort");
            w.WriteStringValue("_doc");
            w.WriteEndArray();
            w.WriteStartArray("_source");
            w.WriteStringValue("id");
            w.WriteStringValue("categories.id");
            w.WriteEndArray();
        });

        string? scrollId = null;
        try
        {
            var doc = await SendAsync(HttpMethod.Post, $"{IndexPath}/_search?scroll=2m&filter_path=_scroll_id,hits.hits._source", body, LinksTimeout, ct);
            while (true)
            {
                using (doc)
                {
                    scrollId = doc.RootElement.TryGetProperty("_scroll_id", out var sid) ? sid.GetString() : null;
                    if (!doc.RootElement.TryGetProperty("hits", out var hits) || !hits.TryGetProperty("hits", out var list) || list.GetArrayLength() == 0)
                        break;
                    foreach (var hit in list.EnumerateArray())
                    {
                        var source = hit.GetProperty("_source");
                        if (!source.TryGetProperty("id", out var id) || !id.TryGetInt32(out var productId))
                            continue;
                        var groups = source.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                            ? categories.EnumerateArray()
                                .Select(c => c.TryGetProperty("id", out var g) && g.TryGetInt32(out var groupId) ? groupId : 0)
                                .Where(g => g != 0)
                                .ToArray()
                            : [];
                        result.Add((productId, groups));
                    }
                }

                if (scrollId is null)
                    break;
                var next = scrollId;
                doc = await SendAsync(HttpMethod.Post, "_search/scroll?filter_path=_scroll_id,hits.hits._source",
                    Json(w => { w.WriteString("scroll", "2m"); w.WriteString("scroll_id", next); }), LinksTimeout, ct);
            }
        }
        finally
        {
            if (scrollId is not null)
                await ClearScrollAsync(scrollId);
        }

        return result;
    }

    private async Task ClearScrollAsync(string scrollId)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Delete, "_search/scroll", Json(w => w.WriteString("scroll_id", scrollId)), Timeout, CancellationToken.None);
        }
        catch (Exception)
        {
            // the scroll context expires by itself
        }
    }

    /// <summary>
    /// Search request: every word must match (AND) the code, EAN, name, OEM numbers or description, or a supplier code
    /// (nested kntCodes); filters: category (nested categories, the index lists all groups of the product path) and
    /// product ids. Text: relevance, then the index order; browsing: the index order ("Order" = product code).
    /// </summary>
    internal static byte[] BuildSearch(IndexQuery query)
    {
        var text = query.Text?.Trim() ?? "";
        if (text.Length > 100)
            text = text[..100];

        return Json(w =>
        {
            w.WriteNumber("from", Math.Max(0, query.From));
            w.WriteNumber("size", Math.Max(0, query.Size));
            w.WriteBoolean("track_total_hits", true);
            w.WriteBoolean("_source", false);
            w.WriteStartArray("docvalue_fields");
            w.WriteStringValue("id");
            w.WriteEndArray();

            w.WriteStartObject("query");
            w.WriteStartObject("bool");
            if (text.Length > 0)
            {
                w.WriteStartArray("must");
                w.WriteStartObject();
                w.WriteStartObject("bool");
                w.WriteStartArray("should");

                w.WriteStartObject();
                w.WriteStartObject("multi_match");
                w.WriteString("query", text);
                w.WriteStartArray("fields");
                foreach (var field in (ReadOnlySpan<string>)["Code^4", "Ean^4", "Name^2", "ProductSetsOemNumbers", "Description"])
                    w.WriteStringValue(field);
                w.WriteEndArray();
                w.WriteString("type", "best_fields");
                w.WriteString("operator", "and");
                w.WriteString("analyzer", Analyzer);
                w.WriteEndObject();
                w.WriteEndObject();

                w.WriteStartObject();
                w.WriteStartObject("nested");
                w.WriteString("path", "kntCodes");
                w.WriteStartObject("query");
                w.WriteStartObject("match");
                w.WriteStartObject("kntCodes.Code");
                w.WriteString("query", text);
                w.WriteString("operator", "and");
                w.WriteString("analyzer", Analyzer);
                w.WriteNumber("boost", 3);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();

                w.WriteEndArray();
                w.WriteNumber("minimum_should_match", 1);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteStartArray("filter");
            if (query.CategoryId is { } categoryId)
            {
                w.WriteStartObject();
                w.WriteStartObject("nested");
                w.WriteString("path", "categories");
                w.WriteStartObject("query");
                w.WriteStartObject("term");
                w.WriteNumber("categories.id", categoryId);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }

            if (query.OnlyIds is not null)
                WriteIds(w, query.OnlyIds);
            w.WriteEndArray();

            if (query.ExcludedIds is { Count: > 0 })
            {
                w.WriteStartArray("must_not");
                WriteIds(w, query.ExcludedIds);
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartArray("sort");
            if (text.Length > 0)
                w.WriteStringValue("_score");
            w.WriteStartObject();
            w.WriteStartObject("Order");
            w.WriteString("order", "asc");
            w.WriteString("missing", "_last");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject(); // stable paging
            w.WriteString("id", "asc");
            w.WriteEndObject();
            w.WriteEndArray();
        });
    }

    private static void WriteIds(Utf8JsonWriter w, IReadOnlyCollection<int> ids)
    {
        w.WriteStartObject();
        w.WriteStartObject("terms");
        w.WriteStartArray("id");
        foreach (var id in ids)
            w.WriteNumberValue(id);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private ElasticsearchOptions Settings => options.CurrentValue.Elasticsearch;
    private string IndexPath => Uri.EscapeDataString(Settings.Index.Trim());
    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1, Settings.TimeoutSeconds));

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Settings.Url.TrimEnd('/') + "/"), path));
        if (body is not null)
            request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };

        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(stream);
            var error = await reader.ReadToEndAsync(cts.Token);
            throw new HttpRequestException($"Elasticsearch {(int)response.StatusCode}: {error[..Math.Min(error.Length, 500)]}", null, response.StatusCode);
        }

        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
    }
}
