using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventParticipantQr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventParticipantQrs",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedToken = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventParticipantQrs", x => new { x.EventId, x.UserId });
                    table.CheckConstraint("CK_ParticipantQr_Hash", "length(\"TokenHash\")=64");
                    table.CheckConstraint("CK_ParticipantQr_Version", "\"RowVersion\">0");
                    table.ForeignKey(
                        name: "FK_EventParticipantQrs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventParticipantQrs_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventQrOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventQrOperations", x => x.Id);
                    table.CheckConstraint("CK_QrOperation_Hash", "length(\"RequestHash\")=64");
                    table.CheckConstraint("CK_QrOperation_Kind", "\"Kind\" IN ('Issue','Read')");
                    table.ForeignKey(
                        name: "FK_EventQrOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventQrOperations_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventQrReads",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    TokenVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventQrReads", x => x.Id);
                    table.CheckConstraint("CK_QrRead_Version", "\"TokenVersion\">0");
                    table.ForeignKey(
                        name: "FK_EventQrReads_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventQrReads_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventQrReads_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventParticipantQrs_TokenHash",
                table: "EventParticipantQrs",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventParticipantQrs_UserId",
                table: "EventParticipantQrs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EventQrOperations_ActorId",
                table: "EventQrOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_EventQrOperations_EventId",
                table: "EventQrOperations",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventQrReads_ActorId",
                table: "EventQrReads",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_EventQrReads_EventId_At_Id",
                table: "EventQrReads",
                columns: new[] { "EventId", "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EventQrReads_UserId",
                table: "EventQrReads",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventParticipantQrs");

            migrationBuilder.DropTable(
                name: "EventQrOperations");

            migrationBuilder.DropTable(
                name: "EventQrReads");
        }
    }
}
