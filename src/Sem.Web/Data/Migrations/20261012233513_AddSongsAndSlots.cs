using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSongsAndSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultPrepSeconds",
                table: "SessionEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 600);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Phases_EventId_Id",
                table: "Phases",
                columns: new[] { "EventId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_EventPartDefinitions_EventId_Id",
                table: "EventPartDefinitions",
                columns: new[] { "EventId", "Id" });

            migrationBuilder.CreateTable(
                name: "Songs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    PerformanceSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    PrepSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Songs", x => x.Id);
                    table.UniqueConstraint("AK_Songs_EventId_Id", x => new { x.EventId, x.Id });
                    table.CheckConstraint("CK_Songs_Artist", "length(\"Artist\") <= 200");
                    table.CheckConstraint("CK_Songs_Seconds", "\"PerformanceSeconds\" >= 1 AND (\"PrepSeconds\" IS NULL OR \"PrepSeconds\" >= 0)");
                    table.CheckConstraint("CK_Songs_Title", "length(\"Title\") BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "FK_Songs_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SongParts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    SongId = table.Column<string>(type: "TEXT", nullable: false),
                    PartDefinitionId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedUserId = table.Column<string>(type: "TEXT", nullable: true),
                    PhaseId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongParts", x => x.Id);
                    table.CheckConstraint("CK_SongParts_Assignment", "(\"AssignedUserId\" IS NULL AND \"PhaseId\" IS NULL) OR (\"AssignedUserId\" IS NOT NULL AND \"PhaseId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SongParts_AspNetUsers_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SongParts_EventPartDefinitions_EventId_PartDefinitionId",
                        columns: x => new { x.EventId, x.PartDefinitionId },
                        principalTable: "EventPartDefinitions",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SongParts_Phases_EventId_PhaseId",
                        columns: x => new { x.EventId, x.PhaseId },
                        principalTable: "Phases",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SongParts_Songs_EventId_SongId",
                        columns: x => new { x.EventId, x.SongId },
                        principalTable: "Songs",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SessionEvents_DefaultPrep",
                table: "SessionEvents",
                sql: "\"DefaultPrepSeconds\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_SongParts_AssignedUserId",
                table: "SongParts",
                column: "AssignedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SongParts_EventId_PartDefinitionId",
                table: "SongParts",
                columns: new[] { "EventId", "PartDefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SongParts_EventId_PhaseId",
                table: "SongParts",
                columns: new[] { "EventId", "PhaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_SongParts_EventId_SongId",
                table: "SongParts",
                columns: new[] { "EventId", "SongId" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_EventId_SortOrder",
                table: "Songs",
                columns: new[] { "EventId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongParts");

            migrationBuilder.DropTable(
                name: "Songs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SessionEvents_DefaultPrep",
                table: "SessionEvents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Phases_EventId_Id",
                table: "Phases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_EventPartDefinitions_EventId_Id",
                table: "EventPartDefinitions");

            migrationBuilder.DropColumn(
                name: "DefaultPrepSeconds",
                table: "SessionEvents");
        }
    }
}
