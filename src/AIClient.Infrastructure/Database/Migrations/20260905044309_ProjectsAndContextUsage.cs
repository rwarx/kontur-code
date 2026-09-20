using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIClient.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class ProjectsAndContextUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CacheWriteTokens",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompacted",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsContextSummary",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReasoningTokens",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    WorkspacePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsExpanded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProjectId_UpdatedAt",
                table: "Conversations",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_SortOrder_Name",
                table: "Projects",
                columns: new[] { "SortOrder", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Projects_ProjectId",
                table: "Conversations",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Projects_ProjectId",
                table: "Conversations");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ProjectId_UpdatedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsCompacted",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsContextSummary",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReasoningTokens",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Conversations");
        }
    }
}
