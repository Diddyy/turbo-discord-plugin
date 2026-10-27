using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboDiscord.Database;

/// <summary>
/// A Discord webhook: one channel, by a name the routing table uses. Its URL holds the webhook's
/// token, so it is a secret: the panel shows its host and webhook id only.
/// </summary>
[Table("webhooks")]
[Index(nameof(Name), IsUnique = true)]
public class WebhookEntity
{
    public const int MAX_NAME = 32;
    public const int MAX_URL = 512;

    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    [MaxLength(MAX_NAME)]
    public string Name { get; set; } = string.Empty;

    [Column("url")]
    [MaxLength(MAX_URL)]
    public string Url { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("last_ok_at")]
    public DateTime? LastOkAt { get; set; }

    [Column("last_error_at")]
    public DateTime? LastErrorAt { get; set; }

    [Column("last_error")]
    [MaxLength(DeliveryEntity.MAX_ERROR)]
    public string? LastError { get; set; }
}
