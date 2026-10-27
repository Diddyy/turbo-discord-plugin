using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboDiscord.Database;

/// <summary>
/// One post: an embed for a webhook, waiting (<see cref="QUEUED"/>), taken by Discord
/// (<see cref="SENT"/>), given up (<see cref="FAILED"/>) or pushed out of a full queue
/// (<see cref="DROPPED"/>). The queue survives a restart because it is this table.
/// <see cref="DedupeKey"/> names the thing posted about: unique, so an event heard twice (a batch
/// of movements delivered again, a notice raised twice) is posted once.
/// </summary>
[Table("deliveries")]
[Index(nameof(DedupeKey), IsUnique = true)]
[Index(nameof(Status), nameof(WebhookId), nameof(Id))]
[Index(nameof(CreatedAt))]
public class DeliveryEntity
{
    public const string QUEUED = "queued";
    public const string SENT = "sent";
    public const string FAILED = "failed";
    public const string DROPPED = "dropped";

    public const int MAX_DEDUPE = 191;
    public const int MAX_ERROR = 256;

    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("event_key")]
    [MaxLength(RouteEntity.MAX_KEY)]
    public string EventKey { get; set; } = string.Empty;

    [Column("webhook_id")]
    public int WebhookId { get; set; }

    [Column("dedupe_key")]
    [MaxLength(MAX_DEDUPE)]
    public string? DedupeKey { get; set; }

    [Column("status")]
    [MaxLength(8)]
    public string Status { get; set; } = QUEUED;

    /// <summary>The embed as Discord takes it (JSON).</summary>
    [Column("embed", TypeName = "text")]
    [MaxLength(16000)]
    public string Embed { get; set; } = string.Empty;

    /// <summary>The embed's title, for the Deliveries page.</summary>
    [Column("title")]
    [MaxLength(RouteEntity.MAX_TITLE)]
    public string Title { get; set; } = string.Empty;

    [Column("attempts")]
    public int Attempts { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>When it may be tried next (after a failure); null: at once.</summary>
    [Column("next_attempt_at")]
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>When Discord took it, it was given up or dropped.</summary>
    [Column("done_at")]
    public DateTime? DoneAt { get; set; }

    [Column("error")]
    [MaxLength(MAX_ERROR)]
    public string? Error { get; set; }
}
