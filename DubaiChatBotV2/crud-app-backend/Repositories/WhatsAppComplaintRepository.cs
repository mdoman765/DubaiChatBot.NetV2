using crud_app_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace crud_app_backend.Repositories
{
    public class WhatsAppComplaintRepository : IWhatsAppComplaintRepository
    {
        private readonly AppDbContext _db;
        private readonly ILogger<WhatsAppComplaintRepository> _logger;

        public WhatsAppComplaintRepository(AppDbContext db, ILogger<WhatsAppComplaintRepository> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── Existing method — unchanged ───────────────────────────────────────

        public async Task<WhatsAppComplaint> AddAsync(WhatsAppComplaint complaint, CancellationToken ct = default)
        {
            _db.WhatsAppComplaints.Add(complaint);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "[ComplaintRepo] Saved complaint Id={Id} Phone={Phone} TicketType={Type} Status={Status} ExternalId={Ext}",
                complaint.Id, complaint.Phone, complaint.TicketType, complaint.Status, complaint.ExternalTicketId);
            return complaint;
        }

        // ── New methods for CRM status-sync ──────────────────────────────────

        /// <inheritdoc/>
        public Task<WhatsAppComplaint?> GetByExternalTicketIdAsync(
            string externalTicketId, CancellationToken ct = default)
        {
            return _db.WhatsAppComplaints
                      .FirstOrDefaultAsync(c => c.ExternalTicketId == externalTicketId, ct);
        }

        /// <inheritdoc/>
        public Task SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);
    }
}
