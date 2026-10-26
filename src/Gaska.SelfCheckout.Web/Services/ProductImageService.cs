using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Infrastructure.Imaging;
using Microsoft.Extensions.Caching.Memory;

namespace Gaska.SelfCheckout.Web.Services;

/// <summary>
/// Product photo thumbnails (GET /img/product/{id}) and category icons (GET /img/category/{id}) for the kiosk: read from
/// the ERP once and kept in memory. Photos are scaled down to JPEG; icons are small and served as they are (PNG keeps
/// its transparency).
/// </summary>
public sealed class ProductImageService(IProductImages images, ILogger<ProductImageService> logger) : IDisposable
{
    private const int ThumbnailSize = 360;
    private static readonly byte[] None = [];

    // ~64 MB of thumbnails (a thumbnail is usually 10–30 KB); products without a photo are remembered too
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

    public async Task<byte[]?> GetThumbnailAsync(int productId, CancellationToken ct)
    {
        if (_cache.TryGetValue(productId, out byte[]? cached))
            return cached!.Length > 0 ? cached : null;

        byte[] thumbnail;
        try
        {
            var original = await images.GetFirstImageAsync(productId, ct);
            thumbnail = original is null ? None : Thumbnail.ToJpeg(original, ThumbnailSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Photo of product {ProductId} could not be loaded", productId);
            thumbnail = None;
        }

        _cache.Set(productId, thumbnail, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, thumbnail.Length),
            SlidingExpiration = TimeSpan.FromHours(thumbnail.Length > 0 ? 12 : 1)
        });
        return thumbnail.Length > 0 ? thumbnail : null;
    }

    public async Task<ErpImage?> GetCategoryIconAsync(int categoryId, CancellationToken ct)
    {
        var key = ("category", categoryId);
        if (_cache.TryGetValue(key, out ErpImage? cached))
            return cached!.Data.Length > 0 ? cached : null;

        ErpImage icon;
        try
        {
            icon = await images.GetCategoryImageAsync(categoryId, ct) ?? new ErpImage(None, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Icon of category {CategoryId} could not be loaded", categoryId);
            icon = new ErpImage(None, "");
        }

        _cache.Set(key, icon, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, icon.Data.Length),
            SlidingExpiration = TimeSpan.FromHours(icon.Data.Length > 0 ? 12 : 1)
        });
        return icon.Data.Length > 0 ? icon : null;
    }

    public static string ContentType(string extension) => extension switch
    {
        "png" => "image/png",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "webp" => "image/webp",
        _ => "image/jpeg"
    };

    public void Dispose() => _cache.Dispose();
}
