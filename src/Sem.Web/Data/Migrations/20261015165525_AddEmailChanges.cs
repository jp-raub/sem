using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailChangeMails",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Due = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Claim = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailChangeMails", x => x.Id);
                    table.CheckConstraint("CK_EmailChangeMail_Claim", "(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
                    table.CheckConstraint("CK_EmailChangeMail_Kind", "\"Kind\" IN ('Verification','OldAddressNotice')");
                    table.CheckConstraint("CK_EmailChangeMail_State", "\"Status\" IN ('Pending','Sending','Sent','Failed','Cancelled') AND \"Attempts\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_EmailChangeMails_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailChangeOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalEmailHash = table.Column<string>(type: "TEXT", nullable: false),
                    TargetEmailHash = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedTargetEmail = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    SessionHash = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengeHash = table.Column<string>(type: "TEXT", nullable: true),
                    ChallengeExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReauthenticatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailChangeOperations", x => x.Id);
                    table.CheckConstraint("CK_EmailChangeOperation_Hashes", "length(\"OriginalEmailHash\")=64 AND length(\"SessionHash\")=64 AND (\"TargetEmailHash\" IS NULL OR length(\"TargetEmailHash\")=64) AND (\"TokenHash\" IS NULL OR length(\"TokenHash\")=64)");
                    table.CheckConstraint("CK_EmailChangeOperation_State", "\"Status\" IN ('Pending','Completed','Cancelled','Invalidated') AND \"RowVersion\">0");
                    table.ForeignKey(
                        name: "FK_EmailChangeOperations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailChangeRateStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    SendTimes = table.Column<string>(type: "TEXT", nullable: false),
                    PasskeyFailures = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationFailures = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailChangeRateStates", x => x.UserId);
                    table.CheckConstraint("CK_EmailChangeRateState_Version", "\"RowVersion\">0");
                    table.ForeignKey(
                        name: "FK_EmailChangeRateStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailChangeMails_Status_Due",
                table: "EmailChangeMails",
                columns: new[] { "Status", "Due" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailChangeMails_UserId",
                table: "EmailChangeMails",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailChangeOperations_TargetEmailHash",
                table: "EmailChangeOperations",
                column: "TargetEmailHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailChangeOperations_UserId",
                table: "EmailChangeOperations",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailChangeMails");

            migrationBuilder.DropTable(
                name: "EmailChangeOperations");

            migrationBuilder.DropTable(
                name: "EmailChangeRateStates");
        }
    }
}
