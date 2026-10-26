using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessNotifications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    Due = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Claim = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessNotifications", x => x.Id);
                    table.CheckConstraint("CK_BusinessNotifications_Attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_BusinessNotifications_Claim", "(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
                    table.CheckConstraint("CK_BusinessNotifications_Kind", "\"Kind\" IN ('Register','Release')");
                    table.CheckConstraint("CK_BusinessNotifications_Sent", "(\"Status\"='Sent' AND \"SentAt\" IS NOT NULL) OR (\"Status\"<>'Sent' AND \"SentAt\" IS NULL)");
                    table.CheckConstraint("CK_BusinessNotifications_Status", "\"Status\" IN ('Pending','Sending','Sent','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_BusinessNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessNotifications_RegistrationOperations_Id",
                        column: x => x.Id,
                        principalTable: "RegistrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessNotifications_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNotifications_EventId",
                table: "BusinessNotifications",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNotifications_Status_Due",
                table: "BusinessNotifications",
                columns: new[] { "Status", "Due" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNotifications_UserId",
                table: "BusinessNotifications",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessNotifications");
        }
    }
}
