using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hestia.Modules.Tasks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tasks");

            migrationBuilder.CreateTable(
                name: "EventInbox",
                schema: "tasks",
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
                schema: "tasks",
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
                name: "Tasks",
                schema: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Tags = table.Column<string>(type: "character varying(1021)", maxLength: 1021, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: true),
                    RepeatEvery = table.Column<int>(type: "integer", nullable: true),
                    RepeatUnit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RepeatMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RepeatDay = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskCompletions",
                schema: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCompletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskCompletions_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: "tasks",
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskLinks",
                schema: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskLinks_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: "tasks",
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventOutbox_NextAttemptAt",
                schema: "tasks",
                table: "EventOutbox",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskCompletions_TaskId_DoneAt",
                schema: "tasks",
                table: "TaskCompletions",
                columns: new[] { "TaskId", "DoneAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskLinks_Kind_TargetId",
                schema: "tasks",
                table: "TaskLinks",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskLinks_TaskId_Kind_TargetId",
                schema: "tasks",
                table: "TaskLinks",
                columns: new[] { "TaskId", "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Status_DueOn",
                schema: "tasks",
                table: "Tasks",
                columns: new[] { "Status", "DueOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventInbox",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "EventOutbox",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "TaskCompletions",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "TaskLinks",
                schema: "tasks");

            migrationBuilder.DropTable(
                name: "Tasks",
                schema: "tasks");
        }
    }
}
