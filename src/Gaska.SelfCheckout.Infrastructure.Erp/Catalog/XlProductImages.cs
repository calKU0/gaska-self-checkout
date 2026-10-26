using System.IO.Compression;
using Dapper;
using Gaska.SelfCheckout.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

/// <summary>
/// Product photos and category icons from the XL binary data (cdn.DaneBinarne attached to the product card / product
/// group through cdn.DaneObiekty). XL stores some files zlib-compressed – then DAB_Rozmiar holds the original size.
/// </summary>
public sealed class XlProductImages(IOptionsMonitor<ErpOptions> options, ILogger<XlProductImages> logger) : IProductImages
{
    private const int ProductType = 16;
    private const int ProductGroupType = -16;

    public async Task<byte[]?> GetFirstImageAsync(int productId, CancellationToken ct = default) =>
        (await GetFirstAsync(ProductType, productId, ct))?.Data;

    public Task<ErpImage?> GetCategoryImageAsync(int categoryId, CancellationToken ct = default) =>
        GetFirstAsync(ProductGroupType, categoryId, ct);

    private async Task<ErpImage?> GetFirstAsync(int objectType, int objectId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(options.CurrentValue.ConnectionString);
        var row = await conn.QueryFirstOrDefaultAsync<(int Id, int Size, string Extension, byte[]? Data)>(new CommandDefinition("""
            SELECT TOP 1 b.DAB_ID, b.DAB_Rozmiar, LOWER(RTRIM(b.DAB_Rozszerzenie)), b.DAB_Dane
            FROM cdn.DaneObiekty o
            JOIN cdn.DaneBinarne b ON b.DAB_ID = o.DAO_DABId
            WHERE o.DAO_ObiTyp = @objectType AND o.DAO_ObiNumer = @objectId
              AND b.DAB_Archiwalny = 0
              AND LOWER(b.DAB_Rozszerzenie) IN ('jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp')
              AND DATALENGTH(b.DAB_Dane) > 0
            ORDER BY o.DAO_Pozycja, o.DAO_ObiLp, b.DAB_ID
            """, new { objectType, objectId }, cancellationToken: ct));
        if (row.Data is not { Length: > 0 } data)
            return null;

        if (row.Size <= 0)
            return new ErpImage(data, row.Extension);

        try
        {
            using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream(row.Size);
            await input.CopyToAsync(output, ct);
            return new ErpImage(output.ToArray(), row.Extension);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Image {DataId} of object {Type}:{Id} could not be decompressed", row.Id, objectType, objectId);
            return null;
        }
    }
}
