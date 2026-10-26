using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ManualPhaseOverride",
                table: "SessionEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Phases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MaxPicks = table.Column<int>(type: "INTEGER", nullable: false),
                    ActivateAt = table.Column<string>(type: "TEXT", nullable: true),
                    DeactivateAt = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Phases", x => x.Id);
                    table.CheckConstraint("CK_Phases_Active", "\"IsActive\" IN (0,1) AND (\"IsActive\"=0 OR \"DeletedAt\" IS NULL)");
                    table.CheckConstraint("CK_Phases_Default", "\"Kind\"='Custom' OR (\"MaxPicks\"=0 AND \"ActivateAt\" IS NULL AND \"DeactivateAt\" IS NULL AND \"DeletedAt\" IS NULL)");
                    table.CheckConstraint("CK_Phases_Kind", "\"Kind\" IN ('Hidden','View','Custom')");
                    table.CheckConstraint("CK_Phases_MaxPicks", "\"MaxPicks\" >= -1");
                    table.ForeignKey(
                        name: "FK_Phases_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Phases_EventId",
                table: "Phases",
                column: "EventId",
                unique: true,
                filter: "\"IsActive\"=1");

            migrationBuilder.CreateIndex(
                name: "IX_Phases_EventId_Kind",
                table: "Phases",
                columns: new[] { "EventId", "Kind" },
                unique: true,
                filter: "\"Kind\" IN ('Hidden','View')");
            // Preserve the explicitly selected pre-phase visibility when adopting existing formal events.
            migrationBuilder.Sql("UPDATE SessionEvents SET ManualPhaseOverride=1;");
            migrationBuilder.Sql("INSERT INTO Phases(Id,EventId,Kind,Name,MaxPicks,IsActive,SortOrder) SELECT Id || ':hidden',Id,'Hidden','非表示',0,CASE WHEN PhaseKind='Hidden' THEN 1 ELSE 0 END,0 FROM SessionEvents;");
            migrationBuilder.Sql("INSERT INTO Phases(Id,EventId,Kind,Name,MaxPicks,IsActive,SortOrder) SELECT Id || ':view',Id,'View','閲覧',0,CASE WHEN PhaseKind='View' THEN 1 ELSE 0 END,1 FROM SessionEvents;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Phases");

            migrationBuilder.DropColumn(
                name: "ManualPhaseOverride",
                table: "SessionEvents");
        }
    }
}
