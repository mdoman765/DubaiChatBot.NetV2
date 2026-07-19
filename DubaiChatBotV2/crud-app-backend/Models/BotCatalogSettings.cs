using System.ComponentModel.DataAnnotations;

namespace crud_app_backend.Models
{
    /// <summary>
    /// Meta Commerce Catalog identifiers needed to actively send a WhatsApp
    /// catalog browsing message. Matches existing dbo.BotCatalogSettings table.
    /// </summary>
    public class BotCatalogSettings
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Meta Commerce Manager Catalog ID.</summary>
        public string CatalogId { get; set; } = "";

        /// <summary>The WhatsApp Business phone number this catalog is attached to.</summary>
        public string CatalogPhone { get; set; } = "";

        /// <summary>SKU shown as the header thumbnail when the catalog message is sent.</summary>
        public string ThumbSku { get; set; } = "";

        public DateTime UpdatedAt { get; set; }
    }
}