using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Sends/receives WhatsApp messages directly through Meta's Cloud API,
    /// replacing the 360dialog BSP layer. Implements the same IDialogClient
    /// contract as DialogClient, so UaeBotService needs zero changes.
    ///
    ///   Send text:  POST https://graph.facebook.com/{apiVersion}/{phoneNumberId}/messages
    ///   Send image: POST ...  (type=image, link=url)
    ///   Send voice: POST ...  (type=audio, link=url)
    ///   Media:      GET  https://graph.facebook.com/{apiVersion}/{mediaId}   (metadata → url)
    ///               GET  {url}                                              (binary, same Bearer token)
    ///   Auth:       Authorization: Bearer {accessToken} (registered as "Meta" named client)
    /// </summary>
    public class MetaWhatsAppClient : IDialogClient
    {
        private readonly IHttpClientFactory _factory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MetaWhatsAppClient> _logger;

        private string PhoneNumberId => _configuration["Meta:PhoneNumberId"]
            ?? throw new InvalidOperationException("Meta:PhoneNumberId is not configured.");

        private string ApiVersion => _configuration["Meta:ApiVersion"] ?? "v20.0";

        private string MessagesUrl => $"https://graph.facebook.com/{ApiVersion}/{PhoneNumberId}/messages";
        private string MediaBaseUrl => $"https://graph.facebook.com/{ApiVersion}";

        public MetaWhatsAppClient(
            IHttpClientFactory factory,
            IConfiguration configuration,
            ILogger<MetaWhatsAppClient> logger)
        {
            _factory = factory;
            _configuration = configuration;
            _logger = logger;
        }

        // ── Send text ─────────────────────────────────────────────────────────

        public async Task SendTextAsync(string phone, string message,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var client = _factory.CreateClient("Meta");
            var payload = new
            {
                messaging_product = "whatsapp",
                to = phone,
                type = "text",
                text = new { body = message }
            };

            var resp = await client.PostAsJsonAsync(MessagesUrl, payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Meta] SendText failed {Code} to {Phone}: {Body}",
                    (int)resp.StatusCode, phone, body.Length > 200 ? body[..200] : body);
            }
            else
            {
                _logger.LogDebug("[Meta] Text sent to {Phone}", phone);
            }
        }

        // ── Send image with caption ───────────────────────────────────────────
        // imageUrl = public HTTPS URL of the image on your server.
        // Meta fetches the image directly from this URL — no upload needed.

        public async Task SendImageAsync(string phone, string imageUrl, string caption,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                _logger.LogWarning("[Meta] SendImage — no imageUrl, falling back to text");
                await SendTextAsync(phone, caption, ct);
                return;
            }

            var client = _factory.CreateClient("Meta");
            var payload = new
            {
                messaging_product = "whatsapp",
                to = phone,
                type = "image",
                image = new
                {
                    link = imageUrl,
                    caption = caption
                }
            };

            var resp = await client.PostAsJsonAsync(MessagesUrl, payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Meta] SendImage failed {Code} to {Phone}: {Body}",
                    (int)resp.StatusCode, phone, body.Length > 200 ? body[..200] : body);

                _logger.LogInformation("[Meta] Falling back to text for {Phone}", phone);
                await SendTextAsync(phone, caption, ct);
            }
            else
            {
                _logger.LogDebug("[Meta] Image sent to {Phone}", phone);
            }
        }

        // ── Send voice note ────────────────────────────────────────────────────
        // audioUrl must be a publicly accessible HTTPS URL. Meta fetches the
        // file directly — no upload step needed. Supported formats:
        // audio/aac, audio/mp4, audio/mpeg, audio/amr, audio/ogg (opus codec).

        public async Task SendVoiceAsync(string phone, string audioUrl,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                _logger.LogWarning("[Meta] SendVoice — no audioUrl, skipping");
                return;
            }

            var client = _factory.CreateClient("Meta");
            var payload = new
            {
                messaging_product = "whatsapp",
                to = phone,
                type = "audio",
                audio = new { link = audioUrl }
            };

            var resp = await client.PostAsJsonAsync(MessagesUrl, payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Meta] SendVoice failed {Code} to {Phone}: {Body}",
                    (int)resp.StatusCode, phone, body.Length > 200 ? body[..200] : body);
            }
            else
            {
                _logger.LogDebug("[Meta] Voice sent to {Phone}", phone);
            }
        }

        // ── Download media (images, voice notes, any incoming media) ─────────

        public async Task<(byte[] Data, string MimeType)> DownloadMediaAsync(
            string mediaId, string fallbackMime,
            CancellationToken ct = default)
        {
            var client = _factory.CreateClient("Meta");

            // Step 1 — get the CDN download URL + mime type for this media ID.
            var metaResp = await client.GetAsync($"{MediaBaseUrl}/{mediaId}", ct);
            metaResp.EnsureSuccessStatusCode();

            var metaJson = await metaResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(metaJson);

            var url = doc.RootElement.TryGetProperty("url", out var urlEl)
                ? urlEl.GetString() ?? string.Empty
                : string.Empty;

            var mimeFromMeta = doc.RootElement.TryGetProperty("mime_type", out var mimeEl)
                ? mimeEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException(
                    $"Meta returned no URL for mediaId={mediaId}");

            // Step 2 — download binary. Meta's media CDN URLs require the SAME
            // Bearer token as the API itself — no separate proxy/rewrite needed
            // (unlike 360dialog, which required rewriting lookaside.fbsbx.com).
            var binResp = await client.GetAsync(url, ct);
            binResp.EnsureSuccessStatusCode();

            var mime = binResp.Content.Headers.ContentType?.MediaType ?? mimeFromMeta ?? fallbackMime;
            var bytes = await binResp.Content.ReadAsByteArrayAsync(ct);

            _logger.LogDebug("[Meta] Downloaded mediaId={Id}: {Bytes}b mime={Mime}",
                mediaId, bytes.Length, mime);

            return (bytes, mime);
        }
    }
}