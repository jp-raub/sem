using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPushRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventPushRules",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ArmedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ArmedProgressVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPushRules", x => new { x.EventId, x.Kind });
                    table.CheckConstraint("CK_EventPushRule_Arming", "(\"Enabled\"=1 AND \"ArmedAt\" IS NOT NULL AND \"ArmedProgressVersion\" IS NOT NULL AND \"ArmedProgressVersion\">=0) OR (\"Enabled\"=0 AND \"ArmedAt\" IS NULL AND \"ArmedProgressVersion\" IS NULL)");
                    table.CheckConstraint("CK_EventPushRule_Kind", "\"Kind\" IN ('OpeningReminder','SongPreparation')");
                    table.CheckConstraint("CK_EventPushRule_Version", "\"Version\">0");
                    table.ForeignKey(
                        name: "FK_EventPushRules_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventPushRules");
        }
    }
}
