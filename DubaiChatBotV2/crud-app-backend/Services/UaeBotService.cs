using System.Collections.Concurrent;
using System.Text.Json;
using crud_app_backend.Bot.Models;
using crud_app_backend.DTOs;
using crud_app_backend.Models;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Bot.Services
{

    public class UaeBotService : IUaeBotService
    {
        private readonly IWhatsAppSessionService _sessionSvc;
        private readonly IWhatsAppMessageRepository _msgRepo;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IDialogClient _dialog;
        private readonly IUaeCrmService _crm;
        private readonly IMemoryCache _cache;
        private readonly BotStateService _state;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<UaeBotService> _logger;
        private readonly IWhatsAppComplaintRepository _complaintRepo;
        private readonly IBotCatalogService _catalog;

        // ── Website base URL (cont_id=3 for UAE) ─────────────────────────────
        private const string WebsiteBaseUrl = "https://myorder.prangroup.com";
        private const string WebsiteContId = "3";

        public UaeBotService(
            IWhatsAppSessionService sessionSvc,
            IWhatsAppMessageRepository msgRepo,
            IWebHostEnvironment env,
            IConfiguration config,
            IDialogClient dialog,
            IUaeCrmService crm,
            IMemoryCache cache,
            BotStateService state,
            IHttpClientFactory httpFactory,
            ILogger<UaeBotService> logger,
            IWhatsAppComplaintRepository complaintRepo,
            IBotCatalogService catalog)
        {
            _sessionSvc = sessionSvc;
            _msgRepo = msgRepo;
            _env = env;
            _config = config;
            _dialog = dialog;
            _crm = crm;
            _cache = cache;
            _state = state;
            _httpFactory = httpFactory;
            _logger = logger;
            _complaintRepo = complaintRepo;
            _catalog = catalog;
        }



        public async Task ProcessAsync(JsonElement body)
        {
            try
            {
                var msg = UaeMessageParser.Parse(body);
                if (msg is null) return;

                _logger.LogInformation("[UAE] {Type} from {Phone} id={Id}",
                    msg.MsgType, msg.From, msg.MessageId);

                // ★ Universal media download for ANY state
                if (msg.MsgType == "image" || msg.MsgType == "audio")
                {
                    var isAudio = msg.MsgType == "audio";
                    var mediaId = isAudio ? msg.AudioId : msg.ImageId;
                    var mime = isAudio ? msg.AudioMime : msg.ImageMime;
                    var subFolder = isAudio ? "audio" : "images";
                    var caption = isAudio ? null : msg.ImageCaption;

                    var savedPath = await SaveMediaToDiskAsync(
                        msg.MessageId, mediaId, mime,
                        msg.From, msg.SenderName, msg.Timestamp,
                        subFolder, caption);

                    if (savedPath != null)
                    {
                        var baseUrl = (_config["App:BaseUrl"] ?? "http://localhost:8041").TrimEnd('/');
                        var fileName = Path.GetFileName(savedPath);
                        msg.SavedFileUrl = $"{baseUrl}/wa-media/{subFolder}/{fileName}";
                        msg.SavedFilePath = savedPath;
                        _logger.LogInformation("[UAE] Universal media saved → {Url}", msg.SavedFileUrl);
                    }
                }

                var userLock = _state.UserLocks.GetOrAdd(msg.From, _ => new SemaphoreSlim(1, 1));
                await userLock.WaitAsync();
                try
                {
                    var session = await LoadSessionAsync(msg.From);

                    var ack = GetAckMessage(session, msg);
                    if (ack != null)
                        await _dialog.SendTextAsync(msg.From, ack);

                    var reply = await RouteAsync(session, msg);

                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        await PersistSessionAsync(session, msg.RawText, msg.SavedFileUrl);
                        return;
                    }

                    await Task.WhenAll(
                        PersistSessionAsync(session, msg.RawText, msg.SavedFileUrl),
                        _dialog.SendTextAsync(msg.From, reply)
                    );
                }
                finally { userLock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] ProcessAsync unhandled crash");
            }
        }


        private string? GetAckMessage(UaeSession s, UaeIncomingMessage msg)
        {
            if (s.State == "AWAITING_SHOP_CODE" && msg.MsgType == "text")
                return s.T("🔍 Verifying shop...", "🔍 শপ যাচাই করা হচ্ছে...", "🔍 दुकान की जाँच हो रही है...");

            if (s.State == "AWAITING_CATEGORY" && msg.MsgType == "text"
                && msg.RawText != "0" && !string.IsNullOrEmpty(msg.RawText))
                return s.T("⏳ Loading categories...", "⏳ ক্যাটাগরি লোড হচ্ছে...", "⏳ श्रेणियाँ लोड हो रही हैं...");

            if (s.State == "AWAITING_SUBCATEGORY" && msg.MsgType == "text"
                && msg.RawText != "0" && !string.IsNullOrEmpty(msg.RawText))
                return s.T("⏳ Loading products...", "⏳ পণ্য লোড হচ্ছে...", "⏳ उत्पाद लोड हो रहे हैं...");

            // ── Gallery burst suppression for ACK ──────────────────────────────
            if ((s.State == "AWAITING_RETURN_DETAILS" || s.State == "AWAITING_COMPLAINT_DETAILS"
                 || s.State == "AWAITING_RETURN_CONFIRM" || s.State == "AWAITING_COMPLAINT_CONFIRM")
                && (msg.MsgType == "image" || msg.MsgType == "audio"))
            {
                var ackNow = msg.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                    : DateTime.UtcNow;
                var ackKey = $"ack:{s.Phone}";
                if (_state.LastImageTime.TryGetValue(ackKey, out var lastAck)
                    && Math.Abs((ackNow - lastAck).TotalSeconds) <= 5)
                    return null;
                _state.LastImageTime[ackKey] = ackNow;
                return s.T("⏳ Uploading media...", "⏳ মিডিয়া আপলোড হচ্ছে...", "⏳ मीडिया अपलोड हो रहा है...");
            }

            if (s.State == "AWAITING_ORDER_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Placing order...", "⏳ অর্ডার দেওয়া হচ্ছে...", "⏳ ऑर्डर दिया जा रहा है...");

            if (s.State == "AWAITING_COMPLAINT_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Submitting complaint...", "⏳ অভিযোগ জমা হচ্ছে...", "⏳ शिकायत जमा हो रही है...");

            if (s.State == "AWAITING_RETURN_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Submitting return request...", "⏳ রিটার্ন জমা হচ্ছে...", "⏳ वापसी जमा हो रही है...");

            if ((s.State == "AWAITING_AGENT_CONFIRM_1" || s.State == "AWAITING_AGENT_CONFIRM_2")
                && (msg.RawText == "y" || msg.RawText == "1"))
                return s.T("⏳ Connecting to agent...", "⏳ এজেন্টের সাথে সংযোগ...", "⏳ एजेंट से जोड़ा जा रहा है...");

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ROUTER
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> RouteAsync(UaeSession s, UaeIncomingMessage msg)
        {
            var raw = msg.RawText;

            // ── Cart order from WhatsApp Catalog — handled regardless of session state ──
            if (msg.MsgType == "order" && msg.CartItems.Count > 0)
                return await HandleCartOrderAsync(s, msg);

            // Global resets — only for users who are already past onboarding.
            // A brand-new (INIT) phone sending "hi"/"hello"/etc. must still
            // go through QR/Shop Code verification like any other first
            // message, so this shortcut is skipped while s.State == "INIT".
            if (msg.MsgType == "text" &&
                s.State != "INIT" &&
                new[] { "hi", "hello", "start", "hey", "new" }.Contains(raw))
            {
                ResetSession(s);
                Transition(s, "AWAITING_LANG");
                await SendWelcomeAsync(msg.From);
                return string.Empty;
            }

            // ── Global QR re-verification — works from ANY state, as long as
            //    this phone hasn't verified a shop yet. Lets a user who never
            //    completed verification (skipped/failed at INIT, or started
            //    with a random message) scan/send a QR code later in the
            //    conversation to link their shop at that point. ─────────────
            if (!s.ShopVerified && msg.MsgType == "text")
            {
                var qrResult = await TryHandleQrCodeAsync(s, msg);
                if (qrResult != null) return qrResult;
            }

            // ── INIT: two ways a user can start a conversation ─────────────────
            // 1) QR code — a bare 6-character code (min. 2 letters + 4 digits,
            //    e.g. "AB1234") scanned via QR. Caught by the global QR check
            //    above (which also covers INIT, since a brand-new phone is
            //    always unverified) — verified, then goes to language screen.
            // 2) Random first message — the user is asked to type their Shop
            //    Code (state AWAITING_SHOP_CODE below). Once they reply, it's
            //    verified against the shopDetails API, then we go to the same
            //    language screen. Either path ends at the language screen —
            //    verification status is flagged on the session so the main
            //    menu can hide Order/Return for unverified shops later.
            if (s.State == "INIT")
            {
                // Not a QR code (already checked above) — ask the user to
                // enter their Shop Code, then verify it in AWAITING_SHOP_CODE.
                Transition(s, "AWAITING_SHOP_CODE");
                return s.T(
                    "👋 Welcome! Please enter your *Shop Code* to continue.\nExample: *12345678*",
                    "👋 স্বাগতম! চালিয়ে যেতে আপনার *শপ কোড* লিখুন।\nউদাহরণ: *12345678*",
                    "👋 स्वागत है! जारी रखने के लिए अपना *शॉप कोड* दर्ज करें।\nउदाहरण: *12345678*");
            }

            // Global shortcuts (shop-verified users only)
            if (s.ShopVerified)
            {
                if (msg.MsgType == "text" && raw == "menu")
                    return BuildMainMenu(s);

                if (msg.MsgType == "text" && raw == "s")
                {
                    Transition(s, "AWAITING_AGENT_CONFIRM_1");
                    return BuildAgentConfirm1(s);
                }
            }
            else
            {
                // Unverified shops can still reach the menu keyword and agent shortcut,
                // since Complaint/Feedback and Support Agent remain available to them.
                if (msg.MsgType == "text" && raw == "menu")
                    return BuildMainMenu(s);

                if (msg.MsgType == "text" && raw == "s")
                {
                    Transition(s, "AWAITING_AGENT_CONFIRM_1");
                    return BuildAgentConfirm1(s);
                }
            }

            return s.State switch
            {
                "AWAITING_LANG" => await HandleLangAsync(s, msg),
                "AWAITING_SHOP_CODE" => await HandleShopCodeAsync(s, msg),
                "MAIN_MENU" => await HandleMainMenu(s, msg),

                "AWAITING_RETURN_DETAILS" => await HandleMediaDetailsAsync(s, msg, "return"),
                "AWAITING_RETURN_CONFIRM" => await HandleReturnConfirmAsync(s, msg),
                "AWAITING_COMPLAINT_DETAILS" => await HandleMediaDetailsAsync(s, msg, "complaint"),
                "AWAITING_COMPLAINT_CONFIRM" => await HandleComplaintConfirmAsync(s, msg),
                "AWAITING_AGENT_CONFIRM_1" => await HandleAgentConfirm1Async(s, msg),
                "AWAITING_AGENT_CONFIRM_2" => await HandleAgentConfirm1Async(s, msg),
                _ => BuildMainMenu(s),
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // QR CODE — VERIFY/RE-VERIFY (used at INIT and globally, any state,
        // as long as the phone is still unverified)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks whether the inbound message is a QR code and, if so, verifies
        /// it and updates the session. Returns null if the message is NOT a QR
        /// code (caller should keep routing normally). Returns a non-null
        /// string (possibly empty, since welcome messages are sent directly)
        /// if the QR code WAS handled — caller should return that immediately.
        /// </summary>
        private async Task<string?> TryHandleQrCodeAsync(UaeSession s, UaeIncomingMessage msg)
        {
            var qrCode = ExtractQrCode(msg.RawTextOriginal);
            if (qrCode == null) return null;

            var qr = await CheckQrCodeAsync(qrCode);
            var customerName = msg.SenderName;

            if (qr != null)
            {
                s.ShopVerified = true;
                s.ShopCode = qr.Value.SiteCode;   // site_code from API — the real shop code
                s.ShopName = qr.Value.SiteName;
                s.QrCode = qrCode;
                customerName = qr.Value.SiteName;
            }
            else
            {
                s.ShopVerified = false;
                s.QrCode = qrCode;
                // ShopCode intentionally left unset — the scanned QR code
                // is NOT a shop code, and no site_code exists on failure.
            }

            // ── Brand-new conversation (still INIT) — same as before: go
            //    straight to the language screen. ─────────────────────────
            if (s.State == "INIT")
            {
                Transition(s, "AWAITING_LANG");
                await SendWelcomeAsync(msg.From, customerName);
                return string.Empty;
            }

            // ── Re-scan mid-conversation (any later state) — don't restart
            //    the whole flow or touch language; just confirm the result
            //    and drop them back at the main menu. ─────────────────────
            if (qr != null)
            {
                Transition(s, "MAIN_MENU");
                return s.T(
                    $"✅ *Shop Verified!*\nYour number is now linked to *{s.ShopName}*.\n\n{BuildMainMenuBody(s.Lang ?? "en", true)}",
                    $"✅ *শপ যাচাই হয়েছে!*\nআপনার নম্বরটি এখন *{s.ShopName}* এর সাথে যুক্ত।\n\n{BuildMainMenuBody(s.Lang ?? "en", true)}",
                    $"✅ *दुकान सत्यापित!*\nआपका नंबर अब *{s.ShopName}* से जुड़ा है।\n\n{BuildMainMenuBody(s.Lang ?? "en", true)}");
            }

            return s.T(
                $"❌ QR Code *{qrCode}* not recognised.\n\n👉 Try scanning again, or send *S* to talk to a Support Agent.",
                $"❌ QR কোড *{qrCode}* শনাক্ত হয়নি।\n\n👉 আবার স্ক্যান করুন, বা *S* পাঠিয়ে সাপোর্ট এজেন্টের সাথে কথা বলুন।",
                $"❌ QR कोड *{qrCode}* पहचाना नहीं गया।\n\n👉 फिर से स्कैन करें, या *S* भेजकर सपोर्ट एजेंट से बात करें।");
        }

        // ─────────────────────────────────────────────────────────────────────
        // QR CODE DETECTION (start-path #1)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects a 6-character QR code made of a minimum of 2 letters
        /// followed by 4 digits (e.g. "AB1234"). Uppercased since the
        /// QR-check API expects/returns uppercase codes. Any first message
        /// that does NOT match this pattern is treated as a random message,
        /// and the user is asked to enter their Shop Code instead.
        /// </summary>
        private static string? ExtractQrCode(string rawTextOriginal)
        {
            var trimmed = (rawTextOriginal ?? "").Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]{2,}\d{4}$")
                && trimmed.Length == 6
                ? trimmed.ToUpperInvariant()
                : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // LANGUAGE SELECTION
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleLangAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return LangPrompt();

            switch (msg.RawText.Trim())
            {
                case "1": s.Lang = "en"; break;
                case "2": s.Lang = "bn"; break;
                case "3": s.Lang = "hi"; break;
                default:
                    return "❌ Invalid. Reply *1*, *2* or *3*.\n\n" + LangPrompt();
            }

            // ── Shop verification was already attempted at INIT. Always go
            //    straight to MAIN_MENU; the menu body itself adapts based on
            //    s.ShopVerified (Order/Return hidden for unverified shops). ──
            Transition(s, "MAIN_MENU");

            if (s.ShopVerified)
            {
                return s.T(
                    $"✅ Language updated.\n\n{BuildMainMenuBody("en", true)}",
                    $"✅ ভাষা পরিবর্তন হয়েছে।\n\n{BuildMainMenuBody("bn", true)}",
                    $"✅ भाषा बदल गई।\n\n{BuildMainMenuBody("hi", true)}");
            }

            return s.T(
                $"⚠️ Shop Code *{s.ShopCode}* not recognised. Some options are limited.\n\n{BuildMainMenuBody("en", false)}",
                $"⚠️ শপ কোড *{s.ShopCode}* শনাক্ত হয়নি। কিছু অপশন সীমিত।\n\n{BuildMainMenuBody("bn", false)}",
                $"⚠️ शॉप कोड *{s.ShopCode}* पहचाना नहीं गया। कुछ विकल्प सीमित हैं।\n\n{BuildMainMenuBody("hi", false)}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHOP AUTHENTICATION — start-path #2 (AWAITING_SHOP_CODE state)
        // Reached when the user's very first message was NOT a QR code; they
        // were asked to type their Shop Code (see INIT above). On success this
        // verifies the code against the shopDetails API, then hands off to the
        // language screen exactly like the QR path does.
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleShopCodeAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text" || string.IsNullOrWhiteSpace(msg.RawText))
                return s.T(
                    "👉 Enter your *Shop Code*.\nExample: *12345678*",
                    "👉 আপনার *শপ কোড* দিন।\nউদাহরণ: *12345678*",
                    "👉 अपना *शॉप कोड* दर्ज करें।\nउदाहरण: *12345678*");

            var code = msg.RawText.Trim();
            var shop = await ValidateShopAsync(code);

            if (shop == null)
                return s.T(
                    $"❌ *Shop Code not found.*\n\n*{code}* is not recognised.\n\n👉 Check and try again.\nExample: *12345678*",
                    $"❌ *শপ কোড পাওয়া যায়নি।*\n\n*{code}* সঠিক নয়।\n\n👉 আবার চেষ্টা করুন।\nউদাহরণ: *12345678*",
                    $"❌ *शॉप कोड नहीं मिला।*\n\n*{code}* सही नहीं।\n\n👉 पुनः प्रयास करें।\nउदाहरण: *12345678*");

            s.ShopVerified = true;
            s.ShopCode = code;
            s.ShopUserId = shop.Value.Id;

            var ownerTitleCase = System.Globalization.CultureInfo.InvariantCulture
                .TextInfo.ToTitleCase((shop.Value.OwnerName ?? "").ToLowerInvariant()).Trim();
            s.ShopName = string.IsNullOrWhiteSpace(ownerTitleCase)
                ? shop.Value.SiteName
                : $"{ownerTitleCase} | {shop.Value.SiteName}";

            var customerName = string.IsNullOrWhiteSpace(ownerTitleCase)
                ? (string.IsNullOrWhiteSpace(msg.SenderName) ? shop.Value.SiteName : msg.SenderName)
                : ownerTitleCase;

            Transition(s, "AWAITING_LANG");
            await SendWelcomeAsync(msg.From, customerName);
            return string.Empty;
        }

        /// <summary>
        /// POST http://spro.prgfms.com/api/v3/siteQrCheck — validates a scanned
        /// QR code and returns the matching shop's site_code/site_name.
        /// Reuses the same ApiKey/base URL already configured for GetSrAgentsAsync
        /// (Spror:AgentApiKey / Spror:AgentBaseUrl).
        /// </summary>
        private async Task<(string SiteCode, string SiteName)?> CheckQrCodeAsync(string qrCode)
        {
            try
            {
                var apiKey = _config["Spror:AgentApiKey"] ?? "f06ff43be3310989";
                var baseUrl = (_config["Spror:AgentBaseUrl"] ?? "http://spro.prgfms.com").TrimEnd('/');
                var countryId = _config["Spror:QrCountryId"] ?? "3";

                var client = _httpFactory.CreateClient("Spror");
                client.Timeout = TimeSpan.FromSeconds(15);

                // The "Spror" named client attaches a default Bearer Authorization
                // header (Spror:BearerToken) for other endpoints. siteQrCheck only
                // accepts the ApiKey header and rejects requests carrying that
                // extra Authorization header with a 401 — so strip it here.
                client.DefaultRequestHeaders.Remove("Authorization");

                // Body: country_id, qr_code (form-urlencoded, per API spec)
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/siteQrCheck")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["country_id"] = countryId,
                        ["qr_code"] = qrCode
                    })
                };
                request.Headers.TryAddWithoutValidation("ApiKey", apiKey);

                _logger.LogInformation("[UAE] CheckQrCode qr={Qr} countryId={Cid}", qrCode, countryId);
                var resp = await client.SendAsync(request);

                var json = await resp.Content.ReadAsStringAsync();
                _logger.LogInformation("[UAE] CheckQrCode {Code} response: {J}",
                    (int)resp.StatusCode, json.Length > 400 ? json[..400] : json);

                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var status = root.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
                var isRegistered = root.TryGetProperty("is_registered", out var irEl) &&
                                    irEl.ValueKind == JsonValueKind.True;

                if (status != "success" || !isRegistered) return null;

                if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                    return null;

                var siteCode = dataEl.TryGetProperty("site_code", out var scEl) ? scEl.GetString() ?? "" : "";
                var siteName = dataEl.TryGetProperty("site_name", out var snEl) ? snEl.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(siteCode)) return null;

                return (siteCode, siteName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] CheckQrCode failed qr={Qr}", qrCode);
                return null;
            }
        }

        private async Task<(string SiteName, string Id, string OwnerName)?> ValidateShopAsync(string shopCode)
        {
            try
            {
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";
                var contName = _config["Spror:ContName"] ?? "United Arab Emirates";
                var baseUrl = (_config["Spror:BaseUrl"] ?? "http://spror.prgfms.com/api/v1").TrimEnd('/');

                var client = _httpFactory.CreateClient("Spror");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.PostAsJsonAsync(
                    $"{baseUrl}/retail/shopDetails",
                    new { shop_code = shopCode, cont_name = contName });

                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync();
                _logger.LogDebug("[UAE] ValidateShop response: {J}", json.Length > 200 ? json[..200] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("status", out var st) || !st.GetBoolean())
                    return null;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array ||
                    dataEl.GetArrayLength() == 0) return null;

                var shop = dataEl[0];
                var id = shop.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                var siteName = shop.TryGetProperty("site_name", out var snEl) ? snEl.GetString() ?? "" : "";
                var ownerName = shop.TryGetProperty("site_ownm", out var ownEl) ? ownEl.GetString() ?? "" : "";

                return string.IsNullOrEmpty(id) ? null : (siteName, id, ownerName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] ValidateShop failed for {Code}", shopCode);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN MENU
        // ─────────────────────────────────────────────────────────────────────

        private string BuildMainMenu(UaeSession s)
        {
            Transition(s, "MAIN_MENU");
            return BuildMainMenuBody(s.Lang ?? "en", s.ShopVerified);
        }

        /// <summary>
        /// Verified shops see the full menu (Order, Return, Complaint, Agent).
        /// Unverified shops see a restricted menu — Order and Return/Replacement
        /// are hidden, leaving only Complaint/Feedback and Support Agent.
        /// </summary>
        private static string BuildMainMenuBody(string lang, bool shopVerified = true)
        {
            // ── Unverified: Complaint + Agent only ─────────────────────────────
            if (!shopVerified) return lang switch
            {
                "bn" =>
                    "1️⃣  অভিযোগ / ফিডব্যাক\n" +
                    "2️⃣  সাপোর্ট এজেন্ট\n" +
                    "0️⃣  ভাষা পরিবর্তন\n\n" +
                    "👉 *1*, *2* বা *0* পাঠান।",
                "hi" =>
                    "1️⃣  शिकायत / फ़ीडबैक\n" +
                    "2️⃣  सपोर्ट एजेंट\n" +
                    "0️⃣  भाषा बदलें\n\n" +
                    "👉 *1*, *2* या *0* भेजें।",
                _ =>
                    "1️⃣  Complaint / Feedback\n" +
                    "2️⃣  Connect with Support Agent\n" +
                    "0️⃣  Change Language\n\n" +
                    "👉 Reply *1*, *2* or *0*.",
            };

            // ── Verified: full menu ───────────────────────────────────────────
            return lang switch
            {
                "bn" =>
                    "1️⃣  অর্ডার দিন\n" +
                    "2️⃣  রিটার্ন / রিপ্লেসমেন্ট\n" +
                    "3️⃣  অভিযোগ / ফিডব্যাক\n" +
                    "4️⃣  সাপোর্ট এজেন্ট\n" +
                    "0️⃣  ভাষা পরিবর্তন\n\n" +
                    "👉 *1*, *2*, *3*, *4* বা *0* পাঠান।",
                "hi" =>
                    "1️⃣  ऑर्डर करें\n" +
                    "2️⃣  वापसी / प्रतिस्थापन\n" +
                    "3️⃣  शिकायत / फ़ीडबैक\n" +
                    "4️⃣  सपोर्ट एजेंट\n" +
                    "0️⃣  भाषा बदलें\n\n" +
                    "👉 *1*, *2*, *3*, *4* या *0* भेजें।",
                _ =>
                    "1️⃣  Place Order\n" +
                    "2️⃣  Return / Replacement\n" +
                    "3️⃣  Complaint / Feedback\n" +
                    "4️⃣  Connect with Support Agent\n" +
                    "0️⃣  Change Language\n\n" +
                    "👉 Reply *1*, *2*, *3*, *4* or *0*.",
            };
        }

        /// <summary>
        /// Routes MAIN_MENU input.
        /// Verified:   1 = Place Order, 2 = Return/Replacement, 3 = Complaint, 4 = Agent, 0 = Change Language
        /// Unverified: 1 = Complaint,   2 = Agent,                              0 = Change Language
        /// </summary>
        private async Task<string> HandleMainMenu(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return BuildUnknown(s);
            if (msg.RawText == "0") return ResetToLang(s);

            if (!s.ShopVerified)
            {
                if (msg.RawText == "1") return StartComplaint(s);
                if (msg.RawText == "2") return StartAgent(s);
                return BuildUnknown(s);
            }

            if (msg.RawText == "1") return BuildOrderWebsiteReply(s);
            if (msg.RawText == "2") return BuildReturnWebsiteReply(s);
            if (msg.RawText == "3") return StartComplaint(s);
            if (msg.RawText == "4") return StartAgent(s);
            return BuildUnknown(s);
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 1 — PLACE ORDER  (website URL only) — verified shops only
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the website order URL reply and transitions back to MAIN_MENU.
        /// URL pattern: https://myorder.prangroup.com/?cont_id=3&order=1&shopCode={shopCode}&phone={phone}
        /// </summary>
        private string BuildOrderWebsiteReply(UaeSession s)
        {
            var shopCode = s.ShopCode ?? "";
            var phone = s.Phone ?? "";
            var url = $"{WebsiteBaseUrl}/?cont_id={WebsiteContId}&order=1&shopCode={shopCode}&phone={phone}";

            Transition(s, "MAIN_MENU");

            return s.T(
                $"🌐 *Place your order on our website:*\n\n" +
                $"{url}\n\n" +
                "👉 Send *menu* for Main Menu",

                $"🌐 *আমাদের ওয়েবসাইটে অর্ডার করুন:*\n\n" +
                $"{url}\n\n" +
                "👉 *menu* — মূল মেনু",

                $"🌐 *हमारी वेबसाइट पर ऑर्डर करें:*\n\n" +
                $"{url}\n\n" +
                "👉 *menu* — मुख्य मेनू");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 2 — RETURN / REPLACEMENT  (website URL only) — verified shops only
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the website return URL reply and transitions back to MAIN_MENU.
        /// URL pattern: https://myorder.prangroup.com/?cont_id=3&order=0&shopCode={shopCode}
        /// </summary>
        private string BuildReturnWebsiteReply(UaeSession s)
        {
            var shopCode = s.ShopCode ?? "";
            var url = $"{WebsiteBaseUrl}/?cont_id={WebsiteContId}&order=0&shopCode={shopCode}";

            Transition(s, "MAIN_MENU");

            return s.T(
                $"🌐 *Submit your return request on our website:*\n\n" +
                $"{url}\n\n" +
                "👉 Send *menu* for Main Menu",

                $"🌐 *আমাদের ওয়েবসাইটে রিটার্ন রিকোয়েস্ট করুন:*\n\n" +
                $"{url}\n\n" +
                "👉 *menu* — মূল মেনু",

                $"🌐 *हमारी वेबसाइट पर वापसी अनुरोध करें:*\n\n" +
                $"{url}\n\n" +
                "👉 *menu* — मुख्य मेनू");
        }

        private async Task<string> HandleReturnConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "PRODUCT_REPLACEMENT");
            if (msg.RawText == "n") { ClearMedia(s); return BuildMainMenu(s); }
            Transition(s, "AWAITING_RETURN_DETAILS");
            return await HandleMediaDetailsAsync(s, msg, "return");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 3 — COMPLAINT / FEEDBACK  (unchanged — available to all shops)
        // ─────────────────────────────────────────────────────────────────────

        private string StartComplaint(UaeSession s)
        {
            ClearMedia(s);
            Transition(s, "AWAITING_COMPLAINT_DETAILS");
            return s.T(
                "📝 *Complaint / Feedback*\n\n" +
                "Tell us your problem.\n\n" +
                "Send *Text*, *Image*, or *Voice*\n\n" +
                "👉 Send *0* to go back to main menu",

                "📝 *অভিযোগ / ফিডব্যাক*\n\n" +
                "আপনার সমস্যা জানান।\n\n" +
                "*টেক্সট*, *ছবি* বা *ভয়েস* পাঠান\n\n" +
                "👉 মূল মেনুতে ফিরতে *0* পাঠান",

                "📝 *शिकायत / फ़ीडबैक*\n\n" +
                "अपनी समस्या बताएं।\n\n" +
                "*टेक्स्ट*, *फ़ोटो* या *आवाज़* भेजें\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");
        }

        private async Task<string> HandleComplaintConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "COMPLAIN");
            if (msg.RawText == "n") { ClearMedia(s); return StartComplaint(s); }
            Transition(s, "AWAITING_COMPLAINT_DETAILS");
            return await HandleMediaDetailsAsync(s, msg, "complaint");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHARED MEDIA HANDLER (Complaint only — Return now uses website)
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleMediaDetailsAsync(
            UaeSession s, UaeIncomingMessage msg, string flowType)
        {
            var confirmState = flowType == "return"
                ? "AWAITING_RETURN_CONFIRM"
                : "AWAITING_COMPLAINT_CONFIRM";

            if (msg.MsgType == "text")
            {
                if (msg.RawText == "0") return BuildMainMenu(s);
                s.MediaDescription = string.IsNullOrWhiteSpace(s.MediaDescription)
                    ? msg.RawText
                    : s.MediaDescription + "\n" + msg.RawText;
            }
            else if (msg.MsgType == "image")
            {
                var imageId = await SaveMediaToDiskAsync(
                    msg.MessageId, msg.ImageId, msg.ImageMime,
                    msg.From, msg.SenderName, msg.Timestamp, "images",
                    caption: msg.ImageCaption);
                if (imageId != null)
                {
                    s.MediaImages.Add(imageId);
                    if (string.IsNullOrWhiteSpace(msg.SavedFileUrl))
                    {
                        var baseUrl2 = (_config["App:BaseUrl"] ?? "http://localhost:8041").TrimEnd('/');
                        msg.SavedFileUrl = $"{baseUrl2}/wa-media/images/{Path.GetFileName(imageId)}";
                    }
                }
                else
                    return s.T(
                        "⚠️ Image could not be uploaded. Please try again.",
                        "⚠️ ছবি আপলোড হয়নি। আবার পাঠান।",
                        "⚠️ फ़ोटो अपलोड नहीं हुई। पुनः भेजें।");

                // ── Confirm message burst suppression ──────────────────────────
                {
                    var now = msg.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                        : DateTime.UtcNow;
                    var confirmKey = $"confirm:{s.Phone}";
                    var isBurst = _state.LastImageTime.TryGetValue(confirmKey, out var last)
                        && Math.Abs((now - last).TotalSeconds) <= 5;
                    _state.LastImageTime[confirmKey] = now;
                    if (isBurst) return string.Empty;
                }
            }
            else if (msg.MsgType == "audio")
            {
                var voiceId = await SaveMediaToDiskAsync(
                    msg.MessageId, msg.AudioId, msg.AudioMime,
                    msg.From, msg.SenderName, msg.Timestamp, "audio");
                if (voiceId != null)
                {
                    s.MediaVoices.Add(voiceId);
                    if (string.IsNullOrWhiteSpace(msg.SavedFileUrl))
                    {
                        var baseUrl2 = (_config["App:BaseUrl"] ?? "http://localhost:8041").TrimEnd('/');
                        msg.SavedFileUrl = $"{baseUrl2}/wa-media/audio/{Path.GetFileName(voiceId)}";
                    }
                }
                else
                    return s.T(
                        "⚠️ Voice note could not be uploaded. Please try again.",
                        "⚠️ ভয়েস আপলোড হয়নি। আবার পাঠান।",
                        "⚠️ आवाज़ अपलोड नहीं हुई। पुनः भेजें।");
            }
            else
            {
                return string.Empty;
            }

            Transition(s, confirmState);



            return s.T(
    "✅ *Received.*\n\n" +
    "Send *Y* to Complete the request or To add more details, send another *Image*, *Voice* or *Text*",

    "✅ *পাওয়া গেছে।*\n\n" +
    "অনুরোধ সম্পন্ন করতে *Y* পাঠান অথবা আরও তথ্য যোগ করতে *ছবি*, *ভয়েস* বা *টেক্সট* পাঠান",

    "✅ *प्राप्त हुआ।*\n\n" +
    "अनुरोध पूरा करने के लिए *Y* भेजें या अधिक जानकारी जोड़ने के लिए *फ़ोटो*, *आवाज़* या *टेक्स्ट* भेजें"
);
        }
        // ─────────────────────────────────────────────────────────────────────
        // CART ORDER (inbound webhook type="order" — WhatsApp Catalog)
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleCartOrderAsync(UaeSession s, UaeIncomingMessage msg)
        {
            _logger.LogInformation(
                "[UAE] Cart order from {Phone} — {Count} items, catalogId={Cat}",
                msg.From, msg.CartItems.Count, msg.OrderCatalogId);

            var nameMap = await _catalog.GetAllNamesAsync();

            var itemLines = msg.CartItems
                .Select(i =>
                {
                    var name = nameMap.TryGetValue(i.Sku, out var n) ? n : i.Sku;
                    return $"• {name} ({i.Sku}) × {i.Qty}" +
                           (i.Price > 0 ? $" @ {i.Price:F2} {i.Currency}" : "");
                })
                .ToList();

            var total = msg.CartItems.Sum(i => i.Price * i.Qty);
            var currency = msg.CartItems.FirstOrDefault()?.Currency ?? "AED";

            var totalLine = total > 0
                ? $"\n\n*Total: {total:F2} {currency}*"
                : string.Empty;

            var description =
                $"WhatsApp Catalog Order — Shop: {s.ShopName ?? s.ShopCode}\n\n" +
                string.Join("\n", itemLines) +
                (total > 0 ? $"\n\nEstimated Total: {total:F2} {currency}" : "") +
                (string.IsNullOrWhiteSpace(msg.OrderText) ? "" : $"\n\nCustomer note: {msg.OrderText}") +
                $"\n\nCatalog ID: {msg.OrderCatalogId}";

            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                TicketType = "PLACE_ORDER",
                Description = description,
                CartItems = string.Join("|", msg.CartItems.Select(i => $"{i.Sku}:{i.Qty}:{i.Price}")),
            };

            var result = await _crm.SubmitAsync(req);

            await _complaintRepo.AddAsync(new crud_app_backend.Models.WhatsAppComplaint
            {
                Phone = s.Phone,
                ShopCode = req.ShopCode,
                ShopName = s.ShopName,
                TicketType = req.TicketType,
                TicketCategory = "UAE_Chatbot",
                Description = req.Description,
                CartItems = req.CartItems,
                Status = result.Success ? "SUCCESS" : "FAILED",
                ExternalTicketId = result.TicketId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            Transition(s, "MAIN_MENU");

            var itemSummary = string.Join("\n", itemLines);

            return result.Success
                ? s.T(
                    $"✅ *Order Received!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"Ticket ID: *{result.TicketId}*\n\n" : "") +
                    "Our team will confirm your order shortly.\n\n" +
                    "👉 Send *menu* for Main Menu",

                    $"✅ *অর্ডার পাওয়া গেছে!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"টিকেট আইডি: *{result.TicketId}*\n\n" : "") +
                    "আমাদের টিম শীঘ্রই আপনার অর্ডার নিশ্চিত করবে।\n\n" +
                    "👉 *মেনু* — মূল মেনু",

                    $"✅ *ऑर्डर प्राप्त हुआ!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"टिकट आईडी: *{result.TicketId}*\n\n" : "") +
                    "हमारी टीम जल्द ही आपके ऑर्डर की पुष्टि करेगी।\n\n" +
                    "👉 *मेनू* भेजें मुख्य मेनू के लिए")

                : s.T(
                    $"❌ *Could not save your order.*\n{result.Error}\n\n" +
                    "Please try again or send *4* to reach a support agent.",
                    $"❌ *অর্ডার সেভ করা যায়নি।*\n{result.Error}\n\n" +
                    "আবার চেষ্টা করুন বা *4* পাঠিয়ে এজেন্টের সাথে যোগাযোগ করুন।",
                    $"❌ *आपका ऑर्डर सेव नहीं हो सका।*\n{result.Error}\n\n" +
                    "कृपया पुनः प्रयास करें या सहायता एजेंट से संपर्क करने के लिए *4* भेजें।");
        }



        private async Task<string> SubmitMediaAsync(UaeSession s, string ticketType)
        {
            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                Description = s.MediaDescription,
                Images = new(s.MediaImages),
                VoiceFiles = new(s.MediaVoices),
                TicketType = ticketType,
            };

            var result = await _crm.SubmitAsync(req);

            await _complaintRepo.AddAsync(new crud_app_backend.Models.WhatsAppComplaint
            {
                Phone = s.Phone,
                ShopCode = req.ShopCode,
                ShopName = s.ShopName,
                TicketType = req.TicketType,
                TicketCategory = "UAE_Chatbot",
                Description = req.Description,
                CartItems = req.CartItems,
                Status = result.Success ? "SUCCESS" : "FAILED",
                ExternalTicketId = result.TicketId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            ClearMedia(s);
            Transition(s, "MAIN_MENU");

            if (!result.Success)
                return s.T(
                    $"❌ Submission failed.\n{result.Error}\n\nSend *Y* to retry.",
                    $"❌ জমা ব্যর্থ।\n{result.Error}",
                    $"❌ जमा विफल।\n{result.Error}");

            var ticketLabel = ticketType == "PRODUCT_REPLACEMENT"
                ? s.T("Return Request", "রিটার্ন রিকোয়েস্ট", "वापसी अनुरोध")
                : s.T("Complaint", "অভিযোগ", "शिकायत");

            return s.T(
                $"✅ *{ticketLabel} Submitted*\n\n" +
                (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                "Our team will contact you shortly.\n\n" +
                "👉 Send *menu* for Main Menu\n",

                $"✅ *{ticketLabel} জমা হয়েছে*\n\n" +
                (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                "আমাদের টিম শীঘ্রই যোগাযোগ করবে।\n\n" +
                "👉 *menu* — মূল মেনু\n",

                $"✅ *{ticketLabel} जमा हुआ*\n\n" +
                (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                "हमारी टीम जल्द संपर्क करेगी।\n\n" +
                "👉 *menu* — मुख्य मेनू\n");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 4 — CONNECT WITH SUPPORT AGENT  (unchanged — available to all shops)
        // ─────────────────────────────────────────────────────────────────────

        private string StartAgent(UaeSession s)
        {
            Transition(s, "AWAITING_AGENT_CONFIRM_1");
            return BuildAgentConfirm1(s);
        }

        private string BuildAgentConfirm1(UaeSession s) =>
            s.T(
                "📞 *Connect with Support Agent*\n\n" +
                "Our support agent will contact you after confirmation.\n\n" +
                "Send *Y* to Confirm\n" +
                "Send *N* to Cancel\n\n" +
                "👉 Send *0* to go back to main menu",

                "📞 *সাপোর্ট এজেন্ট*\n\n" +
                "নিশ্চিত করলে এজেন্ট আপনার সাথে যোগাযোগ করবে।\n\n" +
                "নিশ্চিত করতে *Y* পাঠান\n" +
                "বাতিল করতে *N* পাঠান\n\n" +
                "👉 মূল মেনুতে যেতে *0* পাঠান",

                "📞 *सपोर्ट एजेंट*\n\n" +
                "पुष्टि के बाद हमारा एजेंट आपसे संपर्क करेगा।\n\n" +
                "*Y* भेजें पुष्टि करने के लिए\n" +
                "*N* भेजें रद्द करने के लिए\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");

        private async Task<string> HandleAgentConfirm1Async(
            UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await ConnectAgentAsync(s);
            if (msg.RawText == "n" || msg.RawText == "0") return BuildMainMenu(s);
            return BuildAgentConfirm1(s);
        }

        private async Task<string> ConnectAgentAsync(UaeSession s)
        {
            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                TicketType = "CONNECT_TO_AGENT",
                Description = $"User requested live agent support. Shop: {s.ShopName ?? s.ShopCode}",
            };

            var result = await _crm.SubmitAsync(req);

            await _complaintRepo.AddAsync(new crud_app_backend.Models.WhatsAppComplaint
            {
                Phone = s.Phone,
                ShopCode = req.ShopCode,
                ShopName = s.ShopName,
                TicketType = req.TicketType,
                TicketCategory = "UAE_Chatbot",
                Description = req.Description,
                CartItems = req.CartItems,
                Status = result.Success ? "SUCCESS" : "FAILED",
                ExternalTicketId = result.TicketId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            Transition(s, "MAIN_MENU");

            return result.Success
                ? s.T(
                    "✅ *Agent Request Submitted*\n\n" +
                    (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                    "A support agent will contact you shortly.\n\n" +
                    "👉 Send *menu* for Main Menu",

                    "✅ *অনুরোধ পাঠানো হয়েছে*\n\n" +
                    (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                    "একজন এজেন্ট শীঘ্রই যোগাযোগ করবে।\n\n" +
                    "👉 *menu* — মূল মেনু",

                    "✅ *अनुरोध भेजा गया*\n\n" +
                    (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                    "एक एजेंट जल्द आपसे संपर्क करेगा।\n\n" +
                    "👉 *menu* — मुख्य मेनू")
                : s.T(
                    $"❌ Request failed.\n{result.Error}\n\nSend *S* to retry.",
                    $"❌ ব্যর্থ।\n{result.Error}",
                    $"❌ विफल।\n{result.Error}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // WELCOME WITH LOGO
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendWelcomeAsync(string phone, string customerName = "", CancellationToken ct = default)
        {
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://webhook.prangroup.com";
            var logoUrl = $"{baseUrl}/images/pran-rfl-logo.jpg";
            await _dialog.SendImageAsync(phone, logoUrl, LangPrompt(customerName), ct);
        }

        private static string LangPrompt(string customerName = "") =>
            (string.IsNullOrWhiteSpace(customerName)
                ? "👋 Hi! I'm *PRAN-RFL UAE Sales Support*\n\n"
                : $"👋 Hi {customerName}! I'm *PRAN-RFL UAE Sales Support*\n\n") +
            "Please choose your language:\n\n" +
            "1️⃣  English\n" +
            "2️⃣  বাংলা\n" +
            "3️⃣  हिंदी\n\n" +
            "👉 Reply *1*, *2* or *3*.";

        // ─────────────────────────────────────────────────────────────────────
        // MEDIA SAVE
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string?> SaveMediaToDiskAsync(
            string messageId, string mediaId, string mimeType,
            string from, string senderName, long timestamp,
            string subFolder, string? caption = null)
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                _logger.LogWarning("[UAE] SaveMedia skipped — empty mediaId msgId={Id}", messageId);
                return null;
            }
            if (string.IsNullOrWhiteSpace(_env.WebRootPath))
            {
                _logger.LogError("[UAE] SaveMedia failed — WebRootPath is null or empty");
                return null;
            }
            try
            {
                _logger.LogInformation("[UAE] Downloading media mediaId={Id} type={T}", mediaId, subFolder);
                var (bytes, mime) = await _dialog.DownloadMediaAsync(mediaId, mimeType);
                _logger.LogInformation("[UAE] Downloaded {B} bytes mime={M}", bytes.Length, mime);

                var ext = MimeToExt(mime, subFolder == "audio" ? ".ogg" : ".jpg");
                var fileName = $"{messageId}{ext}";
                var folder = Path.Combine(_env.WebRootPath, "wa-media", subFolder);
                Directory.CreateDirectory(folder);
                var filePath = Path.Combine(folder, fileName);
                await File.WriteAllBytesAsync(filePath, bytes);
                _logger.LogInformation("[UAE] Saved to {Path}", filePath);

                var baseUrl = _config["App:BaseUrl"] ?? "http://localhost:8041";
                var fileUrl = $"{baseUrl}/wa-media/{subFolder}/{fileName}";
                try
                {
                    await _msgRepo.InsertAsync(new WhatsAppMessage
                    {
                        MessageId = messageId,
                        FromNumber = from,
                        SenderName = senderName,
                        MessageType = subFolder == "audio" ? "audio" : "image",
                        MimeType = mime,
                        Caption = caption,
                        FileUrl = fileUrl,
                        FileSizeBytes = bytes.Length,
                        WaTimestamp = timestamp,
                        Status = "processed",
                        ProcessedAt = DateTime.UtcNow,
                    });
                }
                catch (Exception dbEx) when (
                    dbEx.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                    dbEx.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[UAE] Media duplicate skipped: {Id}", messageId);
                }
                catch (Exception dbEx)
                {
                    _logger.LogWarning(dbEx, "[UAE] Media DB insert failed (file saved OK): {Id}", messageId);
                }

                return filePath;
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx,
                    "[UAE] 360dialog download failed mediaId={Id}: {Msg}", mediaId, httpEx.Message);
                return null;
            }
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx,
                    "[UAE] Disk write failed wa-media/{Sub}: {Msg}", subFolder, ioEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[UAE] SaveMedia failed msgId={Id} mediaId={MId}", messageId, mediaId);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SESSION CACHE
        // ─────────────────────────────────────────────────────────────────────

        private async Task<UaeSession> LoadSessionAsync(string phone)
        {
            if (_cache.TryGetValue($"uae:{phone}", out UaeSession? cached) && cached != null)
                return cached;

            var row = await _sessionSvc.GetSessionAsync(phone);
            var session = UaeSession.Load(phone, row.TempData);
            if (session.State == "INIT" && row.CurrentStep != "INIT")
                session.State = row.CurrentStep;

            _cache.Set($"uae:{phone}", session,
                new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60)));
            return session;
        }

        private async Task PersistSessionAsync(UaeSession s, string rawText, string? fileUrl = null)
        {
            _cache.Set($"uae:{s.Phone}", s,
                new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60)));
            try
            {
                await _sessionSvc.UpsertSessionAsync(new UpsertSessionRequestDto
                {
                    Phone = s.Phone,
                    CurrentStep = s.State,
                    PreviousStep = s.PreviousState,
                    TempData = s.Save(),
                    RawMessage = !string.IsNullOrWhiteSpace(fileUrl)
                        ? (string.IsNullOrWhiteSpace(rawText) ? fileUrl : $"{rawText} | {fileUrl}")
                        : rawText,
                });
                _logger.LogInformation("[UAE] PersistSession OK phone={Phone} step={Step}", s.Phone, s.State);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] PersistSession FAILED phone={Phone} step={Step} error={Msg} inner={Inner}",
                    s.Phone, s.State, ex.Message, ex.InnerException?.Message ?? "none");
            }
        }

        private static void Transition(UaeSession s, string newState)
        {
            s.PreviousState = s.State;
            s.State = newState;
        }

        private static void ClearMedia(UaeSession s)
        {
            s.MediaDescription = string.Empty;
            s.MediaImages = new();
            s.MediaVoices = new();
        }

        private static void ResetSession(UaeSession s)
        {
            s.State = "INIT";
            s.PreviousState = "INIT";
            s.Lang = null;
            ClearMedia(s);
        }

        private string ResetToLang(UaeSession s)
        {
            s.Lang = null;
            Transition(s, "AWAITING_LANG");
            return LangPrompt();
        }

        private string BuildUnknown(UaeSession s) =>
            s.T(
                "❌ *Invalid input.*\n\n👉 Send *menu* to go to Main Menu.",
                "❌ *অবৈধ ইনপুট।*\n\n👉 *menu* পাঠান।",
                "❌ *अमान्य इनपुट।*\n\n👉 *menu* भेजें।");

        private static string MimeToExt(string mime, string fallback) => mime switch
        {
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/opus" => ".opus",
            "audio/mp4" => ".m4a",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => fallback
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MESSAGE PARSER
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One line-item from a WhatsApp catalog cart webhook.</summary>
    public record CartItem(string Sku, int Qty, decimal Price, string Currency);

    public class UaeIncomingMessage
    {
        public string From { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string MsgType { get; set; } = "text";
        public long Timestamp { get; set; }
        public string RawText { get; set; } = "";
        public string RawTextOriginal { get; set; } = "";
        public string AudioId { get; set; } = "";
        public string AudioMime { get; set; } = "audio/ogg";
        public string ImageId { get; set; } = "";
        public string ImageMime { get; set; } = "image/jpeg";
        public string ImageCaption { get; set; } = "";
        public string? SavedFileUrl { get; set; }
        public string? SavedFilePath { get; set; }

        // ── Catalog cart order (type = "order") ───────────────────────────────
        public string OrderCatalogId { get; set; } = "";
        public string OrderText { get; set; } = "";
        public List<CartItem> CartItems { get; set; } = new();
    }

    public static class UaeMessageParser
    {
        public static UaeIncomingMessage? Parse(JsonElement body)
        {
            try
            {
                JsonElement? msgEl = null;
                string sender = string.Empty;

                if (body.TryGetProperty("entry", out var entries) &&
                    entries.GetArrayLength() > 0)
                {
                    var value = entries[0].GetProperty("changes")[0].GetProperty("value");
                    if (value.TryGetProperty("statuses", out _) &&
                        !value.TryGetProperty("messages", out _))
                        return null;
                    if (value.TryGetProperty("messages", out var msgs) &&
                        msgs.GetArrayLength() > 0)
                        msgEl = msgs[0];
                    if (value.TryGetProperty("contacts", out var contacts) &&
                        contacts.GetArrayLength() > 0 &&
                        contacts[0].TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("name", out var nameEl))
                        sender = nameEl.GetString() ?? "";
                }
                else if (body.TryGetProperty("messages", out var directMsgs) &&
                         directMsgs.GetArrayLength() > 0)
                {
                    msgEl = directMsgs[0];
                    if (body.TryGetProperty("contacts", out var c) &&
                        c.GetArrayLength() > 0 &&
                        c[0].TryGetProperty("profile", out var p) &&
                        p.TryGetProperty("name", out var n))
                        sender = n.GetString() ?? "";
                }

                if (msgEl is null) return null;
                var msg = msgEl.Value;

                var from = S(msg, "from");
                var msgType = S(msg, "type");
                var msgId = S(msg, "id");
                var ts = long.TryParse(S(msg, "timestamp"), out var t) ? t : 0L;

                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(msgType)) return null;

                string rawText = string.Empty;
                string rawTextOriginal = string.Empty;
                if (msgType == "text" &&
                    msg.TryGetProperty("text", out var textEl) &&
                    textEl.TryGetProperty("body", out var bodyEl))
                {
                    rawTextOriginal = System.Text.RegularExpressions.Regex.Replace(
                        (bodyEl.GetString() ?? "").Trim(),
                        @"[\u200B-\u200D\uFEFF]", "");
                    rawText = rawTextOriginal.ToLowerInvariant();
                }

                string audioId = "", audioMime = "audio/ogg";
                if (msgType == "audio" && msg.TryGetProperty("audio", out var audio))
                {
                    audioId = S(audio, "id");
                    audioMime = S(audio, "mime_type") is { Length: > 0 } m ? m : "audio/ogg";
                }

                string imageId = "", imageMime = "image/jpeg", imageCap = "";
                if (msgType == "image" && msg.TryGetProperty("image", out var image))
                {
                    imageId = S(image, "id");
                    imageMime = S(image, "mime_type") is { Length: > 0 } m ? m : "image/jpeg";
                    imageCap = S(image, "caption");
                }

                // ── Catalog cart order ────────────────────────────────────────
                string orderCatalogId = "", orderText = "";
                var cartItems = new List<CartItem>();

                if (msgType == "order" && msg.TryGetProperty("order", out var order))
                {
                    orderCatalogId = S(order, "catalog_id");
                    orderText = S(order, "text");

                    if (order.TryGetProperty("product_items", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            var sku = S(item, "product_retailer_id");
                            var qty = item.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : 1;
                            var price = item.TryGetProperty("item_price", out var pEl) ? pEl.GetDecimal() : 0m;
                            var currency = S(item, "currency");

                            if (!string.IsNullOrEmpty(sku))
                                cartItems.Add(new CartItem(sku, qty, price, currency));
                        }
                    }
                }

                return new UaeIncomingMessage
                {
                    From = from,
                    SenderName = sender,
                    MessageId = msgId,
                    MsgType = msgType,
                    Timestamp = ts,
                    RawText = rawText,
                    RawTextOriginal = rawTextOriginal,
                    AudioId = audioId,
                    AudioMime = audioMime,
                    ImageId = imageId,
                    ImageMime = imageMime,
                    ImageCaption = imageCap,
                    OrderCatalogId = orderCatalogId,
                    OrderText = orderText,
                    CartItems = cartItems,
                };
            }
            catch { return null; }
        }

        private static string S(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }
}