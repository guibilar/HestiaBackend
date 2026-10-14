using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hestia.Modules.Alerts.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "alerts");

            migrationBuilder.CreateTable(
                name: "Alerts",
                schema: "alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Values = table.Column<string>(type: "text", nullable: false),
                    Link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SortKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventInbox",
                schema: "alerts",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HandledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventInbox", x => new { x.EventId, x.Consumer });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Key",
                schema: "alerts",
                table: "Alerts",
                column: "Key",
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Source_ResolvedAt",
                schema: "alerts",
                table: "Alerts",
                columns: new[] { "Source", "ResolvedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts",
                schema: "alerts");

            migrationBuilder.DropTable(
                name: "EventInbox",
                schema: "alerts");
        }
    }
}
