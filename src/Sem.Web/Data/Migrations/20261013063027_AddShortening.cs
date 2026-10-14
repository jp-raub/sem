using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShortening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_TimeParts_EventId_Id",
                table: "TimeParts",
                columns: new[] { "EventId", "Id" });

            migrationBuilder.CreateTable(
                name: "EventShorteningPolicies",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    PartId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    AllowPrep = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinPrepSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowDuration = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventShorteningPolicies", x => new { x.EventId, x.PartId, x.Scope });
                    table.CheckConstraint("CK_ShorteningPolicy", "\"Scope\" IN ('Plan','Live') AND \"MinPrepSeconds\">=0 AND \"MinDurationSeconds\">=0 AND \"AllowPrep\" IN (0,1) AND \"AllowDuration\" IN (0,1)");
                    table.ForeignKey(
                        name: "FK_EventShorteningPolicies_TimeParts_EventId_PartId",
                        columns: x => new { x.EventId, x.PartId },
                        principalTable: "TimeParts",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShorteningOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShorteningOperations", x => x.Id);
                    table.CheckConstraint("CK_ShorteningOperation", "\"Kind\" IN ('Policy','Approve') AND json_valid(\"ResultJson\")");
                    table.ForeignKey(
                        name: "FK_ShorteningOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShorteningOperations_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShorteningOperations_ActorId",
                table: "ShorteningOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ShorteningOperations_EventId",
                table: "ShorteningOperations",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventShorteningPolicies");

            migrationBuilder.DropTable(
                name: "ShorteningOperations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TimeParts_EventId_Id",
                table: "TimeParts");
        }
    }
}
