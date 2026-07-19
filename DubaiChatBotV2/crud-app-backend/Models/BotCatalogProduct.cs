using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace crud_app_backend.Models
{
    /// <summary>
    /// SKU → product name lookup, used to make WhatsApp Catalog cart orders
    /// (product_retailer_id) readable in CRM tickets and bot replies.
    /// Matches existing dbo.BotCatalogProducts table.
    /// </summary>
    public class BotCatalogProduct
    {
        [Key]
        public int Id { get; set; }

        public string Sku { get; set; } = "";

        public string ProductName { get; set; } = "";

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}