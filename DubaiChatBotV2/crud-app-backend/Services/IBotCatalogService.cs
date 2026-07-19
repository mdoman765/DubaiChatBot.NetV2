using crud_app_backend.Models;

namespace crud_app_backend.Bot.Services
{
    public interface IBotCatalogService
    {
        /// <summary>Never throws. Falls back to the raw SKU if no match found.</summary>
        Task<string> GetProductNameAsync(string sku);

        /// <summary>Sku → ProductName map, cached 30 minutes.</summary>
        Task<Dictionary<string, string>> GetAllNamesAsync();

        /// <summary>Catalog settings (CatalogId/CatalogPhone/ThumbSku), cached 30 minutes.</summary>
        Task<BotCatalogSettings?> GetSettingsAsync();
    }
}