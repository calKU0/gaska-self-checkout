namespace Gaska.SelfCheckout.Application.Abstractions;

/// <summary>Image file attached in the ERP: original bytes and the file extension (jpg, png…).</summary>
public sealed record ErpImage(byte[] Data, string Extension);

/// <summary>Product photos and category icons stored in the ERP (attachments of the product card / product group).</summary>
public interface IProductImages
{
    /// <summary>The first photo of the product (original file bytes), or null when it has none.</summary>
    Task<byte[]?> GetFirstImageAsync(int productId, CancellationToken ct = default);

    /// <summary>The icon of the category (product group), or null when it has none.</summary>
    Task<ErpImage?> GetCategoryImageAsync(int categoryId, CancellationToken ct = default);
}
