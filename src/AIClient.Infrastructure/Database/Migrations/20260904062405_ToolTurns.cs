using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIClient.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class ToolTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ToolCallId",
                table: "Messages",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolCallsJson",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolName",
                table: "Messages",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ToolSucceeded",
                table: "Messages",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ToolCallId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ToolCallsJson",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ToolName",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ToolSucceeded",
                table: "Messages");
        }
    }
}
