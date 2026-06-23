using crud_app_backend.Models;

namespace crud_app_backend.Repositories
{
    public interface IWhatsAppComplaintRepository
    {
        /// <summary>Insert a new complaint row and return the saved entity (with Id populated).</summary>
        Task<WhatsAppComplaint> AddAsync(WhatsAppComplaint complaint, CancellationToken ct = default);

        /// <summary>
        /// Find a complaint by its CRM-issued external ticket ID.
        /// Returns <c>null</c> when no matching row exists.
        /// </summary>
        Task<WhatsAppComplaint?> GetByExternalTicketIdAsync(string externalTicketId, CancellationToken ct = default);

        /// <summary>
        /// Persist any in-memory changes to <paramref name="complaint"/> back to the database.
        /// </summary>
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
