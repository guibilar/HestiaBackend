using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hestia.Modules.People.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "people");

            migrationBuilder.CreateTable(
                name: "EventInbox",
                schema: "people",
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
                schema: "people",
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
                name: "People",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Relationship = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Groups = table.Column<string>(type: "character varying(1021)", maxLength: 1021, nullable: false),
                    BirthMonth = table.Column<int>(type: "integer", nullable: true),
                    BirthDay = table.Column<int>(type: "integer", nullable: true),
                    BirthYear = table.Column<int>(type: "integer", nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContactEveryDays = table.Column<int>(type: "integer", nullable: true),
                    LastContactOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GiftIdeas",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    Occasion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    GivenOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftIdeas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GiftIdeas_People_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "people",
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Interactions",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Interactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Interactions_People_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "people",
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LentOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReturnedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loans_People_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "people",
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonLinks",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonLinks_People_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "people",
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventOutbox_NextAttemptAt",
                schema: "people",
                table: "EventOutbox",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_GiftIdeas_PersonId",
                schema: "people",
                table: "GiftIdeas",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Interactions_PersonId_OccurredOn",
                schema: "people",
                table: "Interactions",
                columns: new[] { "PersonId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_PersonId",
                schema: "people",
                table: "Loans",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_ReturnedOn",
                schema: "people",
                table: "Loans",
                column: "ReturnedOn");

            migrationBuilder.CreateIndex(
                name: "IX_People_Name",
                schema: "people",
                table: "People",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PersonLinks_Kind_TargetId",
                schema: "people",
                table: "PersonLinks",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonLinks_PersonId_Kind_TargetId",
                schema: "people",
                table: "PersonLinks",
                columns: new[] { "PersonId", "Kind", "TargetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventInbox",
                schema: "people");

            migrationBuilder.DropTable(
                name: "EventOutbox",
                schema: "people");

            migrationBuilder.DropTable(
                name: "GiftIdeas",
                schema: "people");

            migrationBuilder.DropTable(
                name: "Interactions",
                schema: "people");

            migrationBuilder.DropTable(
                name: "Loans",
                schema: "people");

            migrationBuilder.DropTable(
                name: "PersonLinks",
                schema: "people");

            migrationBuilder.DropTable(
                name: "People",
                schema: "people");
        }
    }
}
