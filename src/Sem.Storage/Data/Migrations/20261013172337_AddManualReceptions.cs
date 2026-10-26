using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualReceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventReceptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<string>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventReceptions", x => x.Id);
                    table.CheckConstraint("CK_Reception_Cancel", "(\"CancelledAt\" IS NULL AND \"CancelledBy\" IS NULL) OR (\"CancelledAt\" IS NOT NULL AND \"CancelledBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_Reception_Method", "\"Method\"='Manual'");
                    table.CheckConstraint("CK_Reception_Note", "length(\"Note\")<=500");
                    table.ForeignKey(
                        name: "FK_EventReceptions_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventReceptions_AspNetUsers_CancelledBy",
                        column: x => x.CancelledBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventReceptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventReceptions_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventReceptionStates",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventReceptionStates", x => new { x.EventId, x.UserId });
                    table.CheckConstraint("CK_ReceptionState_Version", "\"RowVersion\">=0");
                    table.ForeignKey(
                        name: "FK_EventReceptionStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventReceptionStates_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ReceptionId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionOperations", x => x.Id);
                    table.CheckConstraint("CK_ReceptionOperation_Hash", "length(\"RequestHash\")=64");
                    table.CheckConstraint("CK_ReceptionOperation_Kind", "\"Kind\" IN ('Complete','Cancel')");
                    table.ForeignKey(
                        name: "FK_ReceptionOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionOperations_EventReceptions_ReceptionId",
                        column: x => x.ReceptionId,
                        principalTable: "EventReceptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionOperations_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptions_ActorId",
                table: "EventReceptions",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptions_CancelledBy",
                table: "EventReceptions",
                column: "CancelledBy");

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptions_EventId_UserId",
                table: "EventReceptions",
                columns: new[] { "EventId", "UserId" },
                unique: true,
                filter: "\"CancelledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptions_EventId_UserId_At_Id",
                table: "EventReceptions",
                columns: new[] { "EventId", "UserId", "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptions_UserId",
                table: "EventReceptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptionStates_UserId",
                table: "EventReceptionStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionOperations_ActorId",
                table: "ReceptionOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionOperations_EventId",
                table: "ReceptionOperations",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionOperations_ReceptionId",
                table: "ReceptionOperations",
                column: "ReceptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventReceptionStates");

            migrationBuilder.DropTable(
                name: "ReceptionOperations");

            migrationBuilder.DropTable(
                name: "EventReceptions");
        }
    }
}
