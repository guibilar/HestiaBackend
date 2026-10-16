using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hestia.Modules.Inbox.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmailNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentIds",
                schema: "inbox",
                table: "Items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "From",
                schema: "inbox",
                table: "Items",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageId",
                schema: "inbox",
                table: "Items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "inbox",
                table: "Items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Note");

            migrationBuilder.CreateIndex(
                name: "IX_Items_MessageId",
                schema: "inbox",
                table: "Items",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_MessageId",
                schema: "inbox",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "DocumentIds",
                schema: "inbox",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "From",
                schema: "inbox",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "MessageId",
                schema: "inbox",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "inbox",
                table: "Items");
        }
    }
}
