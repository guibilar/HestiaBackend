using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Hestia.Modules.Home.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "home");

            migrationBuilder.CreateTable(
                name: "EventInbox",
                schema: "home",
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

            migrationBuilder.CreateTable(
                name: "EventOutbox",
                schema: "home",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventType = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rooms",
                schema: "home",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                schema: "home",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    PurchasedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PurchasePrice = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    CurrentValue = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    WarrantyUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Tags = table.Column<string>(type: "character varying(1021)", maxLength: 1021, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('english'::regconfig, \"Name\"), 'A') || setweight(to_tsvector('simple'::regconfig, \"Name\" || ' ' || coalesce(\"Brand\", '') || ' ' || coalesce(\"Model\", '') || ' ' || coalesce(\"SerialNumber\", '') || ' ' || \"Tags\"), 'A') || setweight(to_tsvector('english'::regconfig, coalesce(\"Category\", '')), 'B') || setweight(to_tsvector('simple'::regconfig, coalesce(\"Category\", '')), 'B') || setweight(to_tsvector('english'::regconfig, coalesce(\"Notes\", '')), 'C') || setweight(to_tsvector('simple'::regconfig, coalesce(\"Notes\", '')), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Items_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalSchema: "home",
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemLinks",
                schema: "home",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemLinks_Items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "home",
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventOutbox_NextAttemptAt",
                schema: "home",
                table: "EventOutbox",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_ItemLinks_ItemId_Kind_TargetId",
                schema: "home",
                table: "ItemLinks",
                columns: new[] { "ItemId", "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemLinks_Kind_TargetId",
                schema: "home",
                table: "ItemLinks",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_RoomId",
                schema: "home",
                table: "Items",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_SearchVector",
                schema: "home",
                table: "Items",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_Items_WarrantyUntil",
                schema: "home",
                table: "Items",
                column: "WarrantyUntil");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_Name",
                schema: "home",
                table: "Rooms",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventInbox",
                schema: "home");

            migrationBuilder.DropTable(
                name: "EventOutbox",
                schema: "home");

            migrationBuilder.DropTable(
                name: "ItemLinks",
                schema: "home");

            migrationBuilder.DropTable(
                name: "Items",
                schema: "home");

            migrationBuilder.DropTable(
                name: "Rooms",
                schema: "home");
        }
    }
}
