using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualNotificationAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualNotificationOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    RecipientCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationOperations", x => x.Id);
                    table.UniqueConstraint("AK_ManualNotificationOperations_EventId_Id", x => new { x.EventId, x.Id });
                    table.CheckConstraint("CK_ManualNotificationOperation_Count", "\"RecipientCount\">0");
                    table.CheckConstraint("CK_ManualNotificationOperation_Hashes", "length(\"RequestHash\")=64 AND length(\"PreviewFingerprint\")=64");
                    table.CheckConstraint("CK_ManualNotificationOperation_Text", "length(trim(\"Subject\")) BETWEEN 1 AND 80 AND length(trim(\"Body\")) BETWEEN 1 AND 2000");
                    table.ForeignKey(
                        name: "FK_ManualNotificationOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualNotificationOperations_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManualNotificationRecipients",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    PushState = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationRecipients", x => x.Id);
                    table.CheckConstraint("CK_ManualNotificationRecipient_Id", "length(\"Id\")=64");
                    table.CheckConstraint("CK_ManualNotificationRecipient_Pending", "\"PushState\"='Pending'");
                    table.ForeignKey(
                        name: "FK_ManualNotificationRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualNotificationRecipients_ManualNotificationOperations_EventId_OperationId",
                        columns: x => new { x.EventId, x.OperationId },
                        principalTable: "ManualNotificationOperations",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualNotificationRecipients_Messages_Id",
                        column: x => x.Id,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationOperations_ActorId",
                table: "ManualNotificationOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationRecipients_EventId_OperationId",
                table: "ManualNotificationRecipients",
                columns: new[] { "EventId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationRecipients_OperationId_UserId",
                table: "ManualNotificationRecipients",
                columns: new[] { "OperationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationRecipients_UserId",
                table: "ManualNotificationRecipients",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualNotificationRecipients");

            migrationBuilder.DropTable(
                name: "ManualNotificationOperations");
        }
    }
}
