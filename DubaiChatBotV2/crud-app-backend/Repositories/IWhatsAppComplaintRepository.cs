using crud_app_backend.Models;

namespace crud_app_backend.Repositories
{
    public interface IWhatsAppComplaintRepository
    {
        /// <summary>Insert a new complaint row and return the saved entity (with Id populated).</summary>
        Task<WhatsAppComplaint> AddAsync(WhatsAppComplaint complaint, CancellationToken ct = default);
    }
}
