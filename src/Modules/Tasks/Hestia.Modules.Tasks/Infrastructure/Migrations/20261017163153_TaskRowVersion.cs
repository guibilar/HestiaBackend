using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hestia.Modules.Tasks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No SQL: xmin is a system column of PostgreSQL, and Npgsql leaves it out. The model gets the concurrency token.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "tasks",
                table: "Tasks",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "tasks",
                table: "Tasks");
        }
    }
}
