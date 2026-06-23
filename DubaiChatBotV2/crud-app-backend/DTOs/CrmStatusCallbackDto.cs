using System.ComponentModel.DataAnnotations;

namespace crud_app_backend.DTOs
{
    /// <summary>
    /// Payload sent by the CRM system to notify us of a ticket status change.
    /// </summary>
    public class CrmStatusCallbackDto
    {
        /// <summary>
        /// The CRM ticket identifier stored in WhatsAppComplaints.ExternalTicketId.
        /// </summary>
        [Required(ErrorMessage = "ExternalTicketId is required.")]
        [MaxLength(30, ErrorMessage = "ExternalTicketId must not exceed 30 characters.")]
        public string ExternalTicketId { get; set; } = string.Empty;

        /// <summary>
        /// The new status value to persist (e.g. PENDING, SUCCESS, FAILED, CLOSED …).
        /// </summary>
        [Required(ErrorMessage = "Status is required.")]
        [MaxLength(50, ErrorMessage = "Status must not exceed 50 characters.")]
        public string Status { get; set; } = string.Empty;
    }
}
