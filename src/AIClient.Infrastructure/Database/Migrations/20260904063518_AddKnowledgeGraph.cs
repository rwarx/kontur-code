using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIClient.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GraphChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MutationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    InverseJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GraphNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StartLine = table.Column<int>(type: "INTEGER", nullable: true),
                    EndLine = table.Column<int>(type: "INTEGER", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CanvasViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RootNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    PanX = table.Column<double>(type: "REAL", nullable: false),
                    PanY = table.Column<double>(type: "REAL", nullable: false),
                    Zoom = table.Column<double>(type: "REAL", nullable: false),
                    LayoutMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanvasViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanvasViews_GraphNodes_RootNodeId",
                        column: x => x.RootNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GraphEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphEdges_GraphNodes_FromId",
                        column: x => x.FromId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GraphEdges_GraphNodes_ToId",
                        column: x => x.ToId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CanvasAreas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ViewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GroupNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<double>(type: "REAL", nullable: false),
                    Height = table.Column<double>(type: "REAL", nullable: false),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanvasAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanvasAreas_CanvasViews_ViewId",
                        column: x => x.ViewId,
                        principalTable: "CanvasViews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CanvasAreas_GraphNodes_GroupNodeId",
                        column: x => x.GroupNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CanvasPlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ViewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<double>(type: "REAL", nullable: true),
                    Height = table.Column<double>(type: "REAL", nullable: true),
                    IsCollapsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Accent = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanvasPlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanvasPlacements_CanvasViews_ViewId",
                        column: x => x.ViewId,
                        principalTable: "CanvasViews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CanvasPlacements_GraphNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CanvasAreas_GroupNodeId",
                table: "CanvasAreas",
                column: "GroupNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CanvasAreas_ViewId",
                table: "CanvasAreas",
                column: "ViewId");

            migrationBuilder.CreateIndex(
                name: "IX_CanvasPlacements_NodeId",
                table: "CanvasPlacements",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CanvasPlacements_ViewId_NodeId",
                table: "CanvasPlacements",
                columns: new[] { "ViewId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CanvasViews_RootNodeId",
                table: "CanvasViews",
                column: "RootNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphChanges_CreatedAt",
                table: "GraphChanges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GraphChanges_State",
                table: "GraphChanges",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_FromId",
                table: "GraphEdges",
                column: "FromId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_FromId_ToId_Kind",
                table: "GraphEdges",
                columns: new[] { "FromId", "ToId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphEdges_ToId",
                table: "GraphEdges",
                column: "ToId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_Kind_Key",
                table: "GraphNodes",
                columns: new[] { "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphNodes_Status",
                table: "GraphNodes",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanvasAreas");

            migrationBuilder.DropTable(
                name: "CanvasPlacements");

            migrationBuilder.DropTable(
                name: "GraphChanges");

            migrationBuilder.DropTable(
                name: "GraphEdges");

            migrationBuilder.DropTable(
                name: "CanvasViews");

            migrationBuilder.DropTable(
                name: "GraphNodes");
        }
    }
}
