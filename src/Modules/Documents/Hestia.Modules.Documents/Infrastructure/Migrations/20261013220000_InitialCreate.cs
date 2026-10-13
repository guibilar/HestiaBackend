using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Hestia.Modules.Documents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Tags = table.Column<string>(type: "character varying(1021)", maxLength: 1021, nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OcrStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OcrText = table.Column<string>(type: "text", nullable: true),
                    OcrError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('english'::regconfig, \"Title\"), 'A') || setweight(to_tsvector('simple'::regconfig, \"Title\" || ' ' || \"Tags\"), 'A') || setweight(to_tsvector('english'::regconfig, coalesce(\"Notes\", '')), 'B') || setweight(to_tsvector('simple'::regconfig, coalesce(\"Notes\", '')), 'B') || setweight(to_tsvector('english'::regconfig, coalesce(\"OcrText\", '')), 'C') || setweight(to_tsvector('simple'::regconfig, coalesce(\"OcrText\", '')), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventInbox",
                schema: "documents",
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
                schema: "documents",
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
                name: "DocumentFiles",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(67)", maxLength: 67, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentFiles_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "documents",
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentLinks",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentLinks_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "documents",
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFiles_DocumentId",
                schema: "documents",
                table: "DocumentFiles",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFiles_StorageKey",
                schema: "documents",
                table: "DocumentFiles",
                column: "StorageKey");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLinks_DocumentId_Kind_TargetId",
                schema: "documents",
                table: "DocumentLinks",
                columns: new[] { "DocumentId", "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLinks_Kind_TargetId",
                schema: "documents",
                table: "DocumentLinks",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ExpiresOn",
                schema: "documents",
                table: "Documents",
                column: "ExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SearchVector",
                schema: "documents",
                table: "Documents",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_EventOutbox_NextAttemptAt",
                schema: "documents",
                table: "EventOutbox",
                column: "NextAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentFiles",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "DocumentLinks",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "EventInbox",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "EventOutbox",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "documents");
        }
    }
}
