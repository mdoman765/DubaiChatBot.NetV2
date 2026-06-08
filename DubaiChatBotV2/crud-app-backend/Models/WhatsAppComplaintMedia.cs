using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace crud_app_backend.Models
{
    /// <summary>
    /// One row per media file (image or audio) attached to a WhatsAppComplaint.
    /// FK → dbo.WhatsAppComplaints.Id  (CASCADE DELETE)
    /// Matches dbo.WhatsAppComplaintMedia exactly.
    /// </summary>
    public class WhatsAppComplaintMedia
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("Id")]
        public long Id { get; set; }

        /// <summary>FK to WhatsAppComplaints.Id</summary>
        [Column("ComplaintId")]
        public long ComplaintId { get; set; }

        /// <summary>image | audio  — NOT NULL in DB</summary>
        [Required]
        [MaxLength(20)]
        [Column("MediaType")]
        public string MediaType { get; set; } = default!;

        /// <summary>Full public URL — NOT NULL in DB</summary>
        [Required]
        [MaxLength(2048)]
        [Column("FileUrl")]
        public string FileUrl { get; set; } = default!;

        [MaxLength(255)]
        [Column("FileName")]
        public string? FileName { get; set; }

        /// <summary>File size in bytes — nullable in DB</summary>
        [Column("FileSizeBytes")]
        public long? FileSizeBytes { get; set; }

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
