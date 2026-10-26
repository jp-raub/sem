using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventImportOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventImportOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    NewSongs = table.Column<int>(type: "INTEGER", nullable: false),
                    NewTransitions = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventImportOperations", x => x.Id);
                    table.CheckConstraint("CK_EventImportOperation_Hash", "length(\"Id\")=36 AND length(\"RequestHash\")=64");
                    table.CheckConstraint("CK_EventImportOperation_Result", "\"ResultVersion\">0 AND \"NewSongs\">=0 AND \"NewTransitions\">=0 AND \"NewSongs\"+\"NewTransitions\">0 AND \"SkippedRows\">=0 AND \"AcceptedAt\">=0");
                    table.ForeignKey(
                        name: "FK_EventImportOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventImportOperations_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventImportOperations_ActorId",
                table: "EventImportOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_EventImportOperations_EventId",
                table: "EventImportOperations",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventImportOperations");
        }
    }
}
