using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace crud_app_backend.Models
{
    /// <summary>
    /// Mirrors dbo.WhatsAppComplaints in UaeChatBotDb.
    /// One row per ticket submitted to CRM via the UAE chatbot.
    /// </summary>
    public class WhatsAppComplaint
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("Id")]
        public long Id { get; set; }

        /// <summary>Sender WhatsApp number e.g. 971XXXXXXXXX</summary>
        [MaxLength(30)]
        [Column("Phone")]
        public string? Phone { get; set; }

        [MaxLength(50)]
        [Column("ShopCode")]
        public string? ShopCode { get; set; }

        [MaxLength(255)]
        [Column("ShopName")]
        public string? ShopName { get; set; }

        /// <summary>PLACE_ORDER | PRODUCT_REPLACEMENT | CONNECT_TO_AGENT | COMPLAIN</summary>
        [MaxLength(50)]
        [Column("TicketType")]
        public string? TicketType { get; set; }

        /// <summary>Always "UAE_Chatbot" for this bot.</summary>
        [MaxLength(50)]
        [Column("TicketCategory")]
        public string? TicketCategory { get; set; }

        [Column("Description", TypeName = "nvarchar(max)")]
        public string? Description { get; set; }

        /// <summary>JSON array of cart items (for PLACE_ORDER), otherwise null.</summary>
        [Column("CartItems", TypeName = "nvarchar(max)")]
        public string? CartItems { get; set; }

        /// <summary>PENDING | SUCCESS | FAILED</summary>
        [MaxLength(50)]
        [Column("Status")]
        public string Status { get; set; } = "PENDING";

        /// <summary>Ticket ID returned by CRM on success (data.id).</summary>
        [MaxLength(30)]
        [Column("ExternalTicketId")]
        public string? ExternalTicketId { get; set; }

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("UpdatedAt")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
