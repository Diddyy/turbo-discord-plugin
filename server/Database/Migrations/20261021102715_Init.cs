using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboDiscord.Database.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase().Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "discord_cursors",
                    columns: table => new
                    {
                        name = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        last_id = table.Column<long>(type: "bigint", nullable: false),
                        updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_discord_cursors", x => x.name);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "discord_deliveries",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        event_key = table
                            .Column<string>(type: "varchar(96)", maxLength: 96, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        webhook_id = table.Column<int>(type: "int", nullable: false),
                        dedupe_key = table
                            .Column<string>(type: "varchar(191)", maxLength: 191, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        status = table
                            .Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        embed = table
                            .Column<string>(type: "text", maxLength: 16000, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        title = table
                            .Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        attempts = table.Column<int>(type: "int", nullable: false),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        next_attempt_at = table.Column<DateTime>(
                            type: "datetime(6)",
                            nullable: true
                        ),
                        done_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        error = table
                            .Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_discord_deliveries", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "discord_routes",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        event_key = table
                            .Column<string>(type: "varchar(96)", maxLength: 96, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        webhook_id = table.Column<int>(type: "int", nullable: true),
                        title = table
                            .Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        text = table
                            .Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        threshold = table.Column<long>(type: "bigint", nullable: false),
                        currency = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_discord_routes", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "discord_webhooks",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        name = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        url = table
                            .Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        last_ok_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        last_error_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        last_error = table
                            .Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_discord_webhooks", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_discord_deliveries_created_at",
                table: "discord_deliveries",
                column: "created_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_discord_deliveries_dedupe_key",
                table: "discord_deliveries",
                column: "dedupe_key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_discord_deliveries_status_webhook_id_id",
                table: "discord_deliveries",
                columns: new[] { "status", "webhook_id", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_discord_routes_event_key",
                table: "discord_routes",
                column: "event_key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_discord_webhooks_name",
                table: "discord_webhooks",
                column: "name",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "discord_cursors");

            migrationBuilder.DropTable(name: "discord_deliveries");

            migrationBuilder.DropTable(name: "discord_routes");

            migrationBuilder.DropTable(name: "discord_webhooks");
        }
    }
}
