using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboDiscord.Database;

/// <summary>
/// Where one event goes (<see cref="Events.EventCatalog"/>, or a plugin's notice topic): on or off,
/// to which webhook, with which title and text (null: the event's own), over which threshold. A
/// row is made, off, the first time the event is listed or heard.
/// </summary>
[Table("routes")]
[Index(nameof(Key), IsUnique = true)]
public class RouteEntity
{
    public const int MAX_KEY = 96;
    public const int MAX_TITLE = 256;
    public const int MAX_TEXT = 2000;

    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("event_key")]
    [MaxLength(MAX_KEY)]
    public string Key { get; set; } = string.Empty;

    [Column("enabled")]
    public bool Enabled { get; set; }

    [Column("webhook_id")]
    public int? WebhookId { get; set; }

    [Column("title")]
    [MaxLength(MAX_TITLE)]
    public string? Title { get; set; }

    [Column("text")]
    [MaxLength(MAX_TEXT)]
    public string? Text { get; set; }

    /// <summary>The least an event must be worth to be posted (its amount, price or items); 0: every one.</summary>
    [Column("threshold")]
    public long Threshold { get; set; }

    /// <summary>The currency <see cref="Threshold"/> counts in, for an event that has one (<c>credits</c>).</summary>
    [Column("currency")]
    [MaxLength(16)]
    public string? Currency { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
