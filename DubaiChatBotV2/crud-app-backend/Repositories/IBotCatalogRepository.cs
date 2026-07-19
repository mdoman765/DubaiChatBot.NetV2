using crud_app_backend.Models;

namespace crud_app_backend.Repositories
{
    public interface IBotCatalogRepository
    {
        /// <summary>Returns Sku → ProductName map for all ACTIVE catalog products.</summary>
        Task<Dictionary<string, string>> GetProductNameMapAsync(CancellationToken ct = default);

        /// <summary>Returns the current catalog settings row, or null if not configured.</summary>
        Task<BotCatalogSettings?> GetSettingsAsync(CancellationToken ct = default);
    }
}