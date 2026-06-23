using crud_app_backend.DTOs;
using crud_app_backend.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace crud_app_backend.Controllers
{
    /// <summary>
    /// Receives status-update callbacks pushed by the CRM system.
    ///
    /// Endpoint: POST /api/crm/ticket-status
    ///
    /// The CRM sends:
    ///   { "ExternalTicketId": "…", "Status": "…" }
    ///
    /// Processing:
    ///   1. Validate the request body.
    ///   2. Look up the matching row in WhatsAppComplaints by ExternalTicketId.
    ///   3. If found, update only the Status field (and UpdatedAt timestamp).
    ///   4. Return a standard ApiResponseDto envelope.
    ///
    /// This controller is purely additive and does not modify any existing
    /// controller, service, repository, or data flow in the application.
    /// </summary>
    [ApiController]
    [Route("api/crm")]
    public class CrmCallbackController : ControllerBase
    {
        private readonly IWhatsAppComplaintRepository _complaintRepo;
        private readonly ILogger<CrmCallbackController> _logger;

        public CrmCallbackController(
            IWhatsAppComplaintRepository complaintRepo,
            ILogger<CrmCallbackController> logger)
        {
            _complaintRepo = complaintRepo;
            _logger = logger;
        }

        /// <summary>
        /// Accepts a CRM ticket-status callback and updates the corresponding
        /// <c>WhatsAppComplaints</c> row.
        /// </summary>
        /// <param name="dto">Payload from the CRM containing ExternalTicketId and Status.</param>
        /// <param name="ct">Cancellation token provided by the ASP.NET Core pipeline.</param>
        [HttpPost("ticket-status")]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UpdateTicketStatus(
            [FromBody] CrmStatusCallbackDto dto,
            CancellationToken ct)
        {
            // ── 1. Model validation (attributes on the DTO cover Required / MaxLength) ─
            if (!ModelState.IsValid)
            {
                var errors = string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage));

                _logger.LogWarning(
                    "[CrmCallback] Invalid request body — {Errors}", errors);

                return BadRequest(ApiResponseDto<object>.Fail($"Validation failed: {errors}"));
            }

            _logger.LogInformation(
                "[CrmCallback] Received status callback — ExternalTicketId={ExternalTicketId} Status={Status}",
                dto.ExternalTicketId, dto.Status);

            try
            {
                // ── 2. Look up the complaint ──────────────────────────────────
                var complaint = await _complaintRepo.GetByExternalTicketIdAsync(
                    dto.ExternalTicketId, ct);

                if (complaint is null)
                {
                    _logger.LogWarning(
                        "[CrmCallback] No complaint found for ExternalTicketId={ExternalTicketId}",
                        dto.ExternalTicketId);

                    return NotFound(ApiResponseDto<object>.Fail(
                        $"No complaint found with ExternalTicketId '{dto.ExternalTicketId}'."));
                }

                // ── 3. Update only the Status field ──────────────────────────
                var previousStatus = complaint.Status;
                complaint.Status = dto.Status;
                complaint.UpdatedAt = DateTime.UtcNow;

                await _complaintRepo.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[CrmCallback] Complaint Id={Id} ExternalTicketId={ExternalTicketId} " +
                    "status updated {PreviousStatus} → {NewStatus}",
                    complaint.Id, dto.ExternalTicketId, previousStatus, dto.Status);

                // ── 4. Return success ─────────────────────────────────────────
                return Ok(ApiResponseDto<object>.Ok(
                    new
                    {
                        complaint.Id,
                        complaint.ExternalTicketId,
                        complaint.Status,
                        complaint.UpdatedAt
                    },
                    "Ticket status updated successfully."));
            }
            catch (OperationCanceledException)
            {
                // Request was cancelled by the client — log quietly and return.
                _logger.LogInformation(
                    "[CrmCallback] Request cancelled for ExternalTicketId={ExternalTicketId}",
                    dto.ExternalTicketId);

                return StatusCode(StatusCodes.Status499ClientClosedRequest,
                    ApiResponseDto<object>.Fail("Request cancelled."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[CrmCallback] Unexpected error processing callback for ExternalTicketId={ExternalTicketId}",
                    dto.ExternalTicketId);

                return StatusCode(StatusCodes.Status500InternalServerError,
                    ApiResponseDto<object>.Fail("An unexpected error occurred. Please try again later."));
            }
        }
    }
}
