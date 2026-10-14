using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualNotificationPushQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualNotificationPushPreparations",
                columns: table => new
                {
                    RecipientId = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    PreparedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeviceCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationPushPreparations", x => x.RecipientId);
                    table.CheckConstraint("CK_ManualPushPreparation_Count", "\"DeviceCount\">=0 AND ((\"Outcome\"='Prepared' AND \"DeviceCount\">0) OR (\"Outcome\"<>'Prepared' AND \"DeviceCount\"=0))");
                    table.ForeignKey(
                        name: "FK_ManualNotificationPushPreparations_ManualNotificationRecipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "ManualNotificationRecipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManualNotificationPushDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    BindingFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Claim = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderAcceptedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationPushDeliveries", x => x.Id);
                    table.CheckConstraint("CK_ManualPushDelivery_Accepted", "(\"State\"='Accepted' AND \"ProviderAcceptedAt\" IS NOT NULL) OR (\"State\"<>'Accepted' AND \"ProviderAcceptedAt\" IS NULL)");
                    table.CheckConstraint("CK_ManualPushDelivery_Attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_ManualPushDelivery_Claim", "(\"State\"='Claimed' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL AND \"Attempts\">0 AND \"LeaseUntil\"<=\"ExpiresAt\") OR (\"State\"<>'Claimed' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
                    table.CheckConstraint("CK_ManualPushDelivery_Digests", "length(\"Id\")=64 AND length(\"BindingFingerprint\")=64");
                    table.CheckConstraint("CK_ManualPushDelivery_State", "\"State\" IN ('Pending','Claimed','Unknown','Accepted','Expired','Suppressed','Failed')");
                    table.ForeignKey(
                        name: "FK_ManualNotificationPushDeliveries_ManualNotificationPushPreparations_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "ManualNotificationPushPreparations",
                        principalColumn: "RecipientId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationPushDeliveries_RecipientId_DeviceId",
                table: "ManualNotificationPushDeliveries",
                columns: new[] { "RecipientId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationPushDeliveries_State_DueAt",
                table: "ManualNotificationPushDeliveries",
                columns: new[] { "State", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualNotificationPushDeliveries");

            migrationBuilder.DropTable(
                name: "ManualNotificationPushPreparations");
        }
    }
}
