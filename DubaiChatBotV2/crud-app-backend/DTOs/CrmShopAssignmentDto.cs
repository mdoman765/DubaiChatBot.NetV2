using System.ComponentModel.DataAnnotations;

namespace crud_app_backend.DTOs
{
    /// <summary>
    /// Payload forwarded by webhook-gateway's /api/crm/shop-assignment route.
    /// Gateway sends: { "shopCode": "...", "phone": "..." }
    /// (phone must be included via the CRM's AdditionalParameters on the
    /// gateway side, since ChatbotName/ShopCode alone don't identify a session.)
    /// </summary>
    public class CrmShopAssignmentDto
    {
        [Required(ErrorMessage = "Phone is required.")]
        [MaxLength(30, ErrorMessage = "Phone must not exceed 30 characters.")]
        public string Phone { get; set; } = string.Empty;

        [Required(ErrorMessage = "ShopCode is required.")]
        [MaxLength(50, ErrorMessage = "ShopCode must not exceed 50 characters.")]
        public string ShopCode { get; set; } = string.Empty;
    }
}