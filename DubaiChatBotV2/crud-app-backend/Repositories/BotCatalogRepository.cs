using crud_app_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace crud_app_backend.Repositories
{
    public class BotCatalogRepository : IBotCatalogRepository
    {
        private readonly AppDbContext _db;
        private readonly ILogger<BotCatalogRepository> _logger;

        public BotCatalogRepository(AppDbContext db, ILogger<BotCatalogRepository> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<Dictionary<string, string>> GetProductNameMapAsync(CancellationToken ct = default)
        {
            try
            {
                return await _db.BotCatalogProducts
                    .AsNoTracking()
                    .Where(p => p.IsActive)
                    .ToDictionaryAsync(p => p.Sku, p => p.ProductName, StringComparer.OrdinalIgnoreCase, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CatalogRepo] Could not load BotCatalogProducts — falling back to raw SKUs");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task<BotCatalogSettings?> GetSettingsAsync(CancellationToken ct = default)
        {
            try
            {
                return await _db.BotCatalogSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CatalogRepo] Could not load BotCatalogSettings");
                return null;
            }
        }
    }
}