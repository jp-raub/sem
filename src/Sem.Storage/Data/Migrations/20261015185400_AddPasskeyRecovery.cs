using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasskeyRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasskeyRecoveryAdminChallenges",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AdminId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUserId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionHash = table.Column<string>(type: "TEXT", nullable: false),
                    ContextHash = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengeHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyRecoveryAdminChallenges", x => x.Id);
                    table.CheckConstraint("CK_PasskeyRecoveryAdminChallenge_Hashes", "length(\"SessionHash\")=64 AND length(\"ContextHash\")=64 AND length(\"ChallengeHash\")=64");
                    table.CheckConstraint("CK_PasskeyRecoveryAdminChallenge_Time", "\"ExpiresAt\">\"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryAdminChallenges_AspNetUsers_AdminId",
                        column: x => x.AdminId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryAdminChallenges_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PasskeyRecoveryOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AdminId = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    EmailHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    GrantHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreationChallengeHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EmailVerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    GrantExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DisabledForRecovery = table.Column<bool>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyRecoveryOperations", x => x.Id);
                    table.CheckConstraint("CK_PasskeyRecoveryOperation_Hashes", "length(\"EmailHash\")=64 AND length(\"SecurityStamp\")>0 AND (\"TokenHash\" IS NULL OR length(\"TokenHash\")=64) AND (\"GrantHash\" IS NULL OR length(\"GrantHash\")=64) AND (\"CreationChallengeHash\" IS NULL OR length(\"CreationChallengeHash\")=64)");
                    table.CheckConstraint("CK_PasskeyRecoveryOperation_Reason", "length(trim(\"Reason\")) BETWEEN 3 AND 500");
                    table.CheckConstraint("CK_PasskeyRecoveryOperation_State", "\"Status\" IN ('Pending','EmailConfirmed','Completed','Expired','Cancelled','Invalidated') AND \"RowVersion\">0");
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryOperations_AspNetUsers_AdminId",
                        column: x => x.AdminId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryOperations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PasskeyRecoveryRateStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    SendTimes = table.Column<string>(type: "TEXT", nullable: false),
                    ProofFailures = table.Column<string>(type: "TEXT", nullable: false),
                    EmailFailures = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyRecoveryRateStates", x => new { x.UserId, x.Purpose });
                    table.CheckConstraint("CK_PasskeyRecoveryRateState_Version", "\"RowVersion\">0");
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryRateStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PasskeyRecoveryAudits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    AdminId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyRecoveryAudits", x => x.Id);
                    table.CheckConstraint("CK_PasskeyRecoveryAudit_Action", "\"Action\" IN ('RecoveryStarted','RecoveryMailResent','RecoveryEmailConfirmed','RecoveryCompleted')");
                    table.CheckConstraint("CK_PasskeyRecoveryAudit_Reason", "length(trim(\"Reason\")) BETWEEN 3 AND 500");
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryAudits_AspNetUsers_AdminId",
                        column: x => x.AdminId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryAudits_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryAudits_PasskeyRecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "PasskeyRecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PasskeyRecoveryMails",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ProofHash = table.Column<string>(type: "TEXT", nullable: true),
                    Due = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Claim = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyRecoveryMails", x => x.Id);
                    table.CheckConstraint("CK_PasskeyRecoveryMail_Claim", "(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
                    table.CheckConstraint("CK_PasskeyRecoveryMail_Kind", "\"Kind\" IN ('RecoveryLink','RecoveryComplete')");
                    table.CheckConstraint("CK_PasskeyRecoveryMail_ProofHash", "\"ProofHash\" IS NULL OR length(\"ProofHash\")=64");
                    table.CheckConstraint("CK_PasskeyRecoveryMail_State", "\"Status\" IN ('Pending','Sending','Sent','Failed','Cancelled') AND \"Attempts\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryMails_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasskeyRecoveryMails_PasskeyRecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "PasskeyRecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryAdminChallenges_AdminId_TargetUserId_SessionHash_ExpiresAt",
                table: "PasskeyRecoveryAdminChallenges",
                columns: new[] { "AdminId", "TargetUserId", "SessionHash", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryAdminChallenges_TargetUserId",
                table: "PasskeyRecoveryAdminChallenges",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryAudits_AdminId",
                table: "PasskeyRecoveryAudits",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryAudits_OperationId_At",
                table: "PasskeyRecoveryAudits",
                columns: new[] { "OperationId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryAudits_TargetUserId",
                table: "PasskeyRecoveryAudits",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryMails_OperationId_Kind",
                table: "PasskeyRecoveryMails",
                columns: new[] { "OperationId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryMails_Status_Due",
                table: "PasskeyRecoveryMails",
                columns: new[] { "Status", "Due" });

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryMails_UserId",
                table: "PasskeyRecoveryMails",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryOperations_AdminId",
                table: "PasskeyRecoveryOperations",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyRecoveryOperations_UserId",
                table: "PasskeyRecoveryOperations",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasskeyRecoveryAdminChallenges");

            migrationBuilder.DropTable(
                name: "PasskeyRecoveryAudits");

            migrationBuilder.DropTable(
                name: "PasskeyRecoveryMails");

            migrationBuilder.DropTable(
                name: "PasskeyRecoveryRateStates");

            migrationBuilder.DropTable(
                name: "PasskeyRecoveryOperations");
        }
    }
}
