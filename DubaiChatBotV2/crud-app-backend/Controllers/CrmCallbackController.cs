using crud_app_backend.Bot.Models;
using crud_app_backend.Bot.Services;
using crud_app_backend.DTOs;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Controllers
{
    /// <summary>
    /// Receives status-update and shop-assignment callbacks pushed by the CRM system
    /// (routed here via webhook-gateway's /api/crm/* endpoints).
    ///
    /// This controller is purely additive and does not modify any existing
    /// controller, service, repository, or data flow in the application.
    /// </summary>
    [ApiController]
    [Route("api/crm")]
    public class CrmCallbackController : ControllerBase
    {
        private readonly IWhatsAppComplaintRepository _complaintRepo;
        private readonly IWhatsAppSessionService _sessionSvc;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CrmCallbackController> _logger;

        public CrmCallbackController(
            IWhatsAppComplaintRepository complaintRepo,
            IWhatsAppSessionService sessionSvc,
            IMemoryCache cache,
            ILogger<CrmCallbackController> logger)
        {
            _complaintRepo = complaintRepo;
            _sessionSvc = sessionSvc;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Accepts a CRM ticket-status callback and updates the corresponding
        /// <c>WhatsAppComplaints</c> row.
        /// Endpoint: POST /api/crm/ticket-status
        /// </summary>
        [HttpPost("ticket-status")]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UpdateTicketStatus(
            [FromBody] CrmStatusCallbackDto dto,
            CancellationToken ct)
        {
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

                var previousStatus = complaint.Status;
                complaint.Status = dto.Status;
                complaint.UpdatedAt = DateTime.UtcNow;

                await _complaintRepo.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[CrmCallback] Complaint Id={Id} ExternalTicketId={ExternalTicketId} " +
                    "status updated {PreviousStatus} → {NewStatus}",
                    complaint.Id, dto.ExternalTicketId, previousStatus, dto.Status);

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

        /// <summary>
        /// Accepts a CRM shop-assignment callback (forwarded via webhook-gateway's
        /// ChatbotRouting) and marks the shop as verified for that phone's session.
        ///
        /// Endpoint: POST /api/crm/shop-assignment
        /// Body: { "phone": "971581260024", "shopCode": "1" }
        ///
        /// Effect on the session's TempData JSON blob:
        ///   shopCode      → set to the incoming ShopCode
        ///   shopVerified  → set to true
        ///   everything else (state, previousState, lang, media*) is preserved
        /// </summary>
        [HttpPost("shop-assignment")]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AssignShopCode(
            [FromBody] CrmShopAssignmentDto dto,
            CancellationToken ct)
        {
            if (!ModelState.IsValid)
            {
                var errors = string.Join("; ", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage));

                _logger.LogWarning(
                    "[CrmCallback] Invalid shop-assignment request — {Errors}", errors);

                return BadRequest(ApiResponseDto<object>.Fail($"Validation failed: {errors}"));
            }

            var phone = dto.Phone.Trim();
            var shopCode = dto.ShopCode.Trim();

            _logger.LogInformation(
                "[CrmCallback] Shop assignment received — Phone={Phone} ShopCode={ShopCode}",
                phone, shopCode);

            try
            {
                // ── 1. Load current session (creates a clean INIT default if none exists) ──
                var row = await _sessionSvc.GetSessionAsync(phone, ct);
                var session = UaeSession.Load(phone, row.TempData);

                if (session.State == "INIT" && row.CurrentStep != "INIT")
                    session.State = row.CurrentStep;

                // ── 2. Apply the shop verification ─────────────────────────────
                session.ShopCode = shopCode;
                session.ShopVerified = true;

                // ── 3. Persist — DB write + history row via existing service ───
                await _sessionSvc.UpsertSessionAsync(new UpsertSessionRequestDto
                {
                    Phone = session.Phone,
                    CurrentStep = session.State,
                    PreviousStep = session.PreviousState,
                    TempData = session.Save(),
                    RawMessage = $"[CRM] Shop code {shopCode} verified"
                }, ct);

                // ── 4. Invalidate UaeBotService's in-memory cache ───────────────
                // Without this, an active conversation could keep using the old
                // ShopVerified=false session for up to 60 minutes (sliding cache).
                _cache.Remove($"uae:{phone}");

                _logger.LogInformation(
                    "[CrmCallback] Shop verified — Phone={Phone} ShopCode={ShopCode}",
                    phone, shopCode);

                return Ok(ApiResponseDto<object>.Ok(
                    new
                    {
                        phone,
                        shopCode = session.ShopCode,
                        shopVerified = session.ShopVerified
                    },
                    "Shop code assigned and verified successfully."));
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "[CrmCallback] Shop-assignment request cancelled — Phone={Phone}", phone);

                return StatusCode(StatusCodes.Status499ClientClosedRequest,
                    ApiResponseDto<object>.Fail("Request cancelled."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[CrmCallback] Unexpected error processing shop-assignment for Phone={Phone}",
                    phone);

                return StatusCode(StatusCodes.Status500InternalServerError,
                    ApiResponseDto<object>.Fail("An unexpected error occurred. Please try again later."));
            }
        }
    }
}