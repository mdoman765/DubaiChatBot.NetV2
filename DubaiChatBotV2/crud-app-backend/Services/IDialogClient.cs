namespace crud_app_backend.Bot.Services
{
    public interface IDialogClient
    {
        /// <summary>Send a plain-text WhatsApp message.</summary>
        Task SendTextAsync(string phone, string message,
            CancellationToken ct = default);

        /// <summary>
        /// Send an image message with a caption.
        /// imageUrl must be a publicly accessible HTTPS URL.
        /// e.g. https://webhook.prangroup.com/images/pran-rfl-logo.jpg
        /// The provider fetches the image directly from this URL — no upload needed.
        /// caption supports WhatsApp markdown (*bold*, _italic_, etc).
        /// Falls back to plain text automatically if imageUrl is empty or fetch fails.
        /// </summary>
        Task SendImageAsync(string phone, string imageUrl, string caption,
            CancellationToken ct = default);

        /// <summary>
        /// Send a voice/audio message. audioUrl must be a publicly accessible
        /// HTTPS URL pointing to a supported audio format
        /// (audio/aac, audio/mp4, audio/mpeg, audio/amr, audio/ogg [opus codec]).
        /// WhatsApp displays this as a playable voice note.
        /// </summary>
        Task SendVoiceAsync(string phone, string audioUrl,
            CancellationToken ct = default);

        /// <summary>
        /// Download a media file (image, voice note, etc).
        /// Returns (bytes, mimeType). Throws on failure.
        /// </summary>
        Task<(byte[] Data, string MimeType)> DownloadMediaAsync(
            string mediaId, string fallbackMime,
            CancellationToken ct = default);
    }
}