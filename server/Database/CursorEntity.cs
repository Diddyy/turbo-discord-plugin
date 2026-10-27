using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TurboDiscord.Database;

/// <summary>How far the asset movement outbox was read, written with the posts made from it.</summary>
[Table("cursors")]
public class CursorEntity
{
    public const string MOVEMENTS = "movements";

    [Key]
    [Column("name")]
    [MaxLength(32)]
    public string Name { get; set; } = string.Empty;

    [Column("last_id")]
    public long LastId { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
