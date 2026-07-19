using crud_app_backend.Models;
using crud_app_backend.Repositories;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Bot.Services
{
    public class BotCatalogService : IBotCatalogService
    {
        private readonly IBotCatalogRepository _repo;
        private readonly IMemoryCache _cache;
        private readonly ILogger<BotCatalogService> _logger;

        private const string ProductMapKey = "catalog:products";
        private const string SettingsKey = "catalog:settings";

        public BotCatalogService(
            IBotCatalogRepository repo,
            IMemoryCache cache,
            ILogger<BotCatalogService> logger)
        {
            _repo = repo;
            _cache = cache;
            _logger = logger;
        }

        public async Task<string> GetProductNameAsync(string sku)
        {
            var map = await GetAllNamesAsync();
            return map.TryGetValue(sku, out var name) ? name : sku;
        }

        public async Task<Dictionary<string, string>> GetAllNamesAsync()
        {
            if (_cache.TryGetValue(ProductMapKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            try
            {
                var map = await _repo.GetProductNameMapAsync();
                _cache.Set(ProductMapKey, map,
                    new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(30)));
                return map;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Catalog] Failed to load product names");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task<BotCatalogSettings?> GetSettingsAsync()
        {
            if (_cache.TryGetValue(SettingsKey, out BotCatalogSettings? cached) && cached != null)
                return cached;

            var settings = await _repo.GetSettingsAsync();
            if (settings != null)
                _cache.Set(SettingsKey, settings,
                    new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(30)));

            return settings;
        }
    }
}