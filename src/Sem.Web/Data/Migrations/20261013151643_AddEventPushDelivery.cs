using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPushDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "PushDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionHash",
                table: "PushDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventPushDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RuleVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Due = table.Column<long>(type: "INTEGER", nullable: false),
                    Expires = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Claim = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPushDeliveries", x => x.Id);
                    table.CheckConstraint("CK_EventPushDelivery_Accepted", "(\"Status\"='Accepted' AND \"AcceptedAt\" IS NOT NULL) OR (\"Status\"<>'Accepted' AND \"AcceptedAt\" IS NULL)");
                    table.CheckConstraint("CK_EventPushDelivery_Claim", "(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
                    table.CheckConstraint("CK_EventPushDelivery_Kind", "\"Kind\" IN ('OpeningReminder','SongPreparation')");
                    table.CheckConstraint("CK_EventPushDelivery_Status", "\"Status\" IN ('Pending','Sending','Accepted','Cancelled','Failed')");
                    table.CheckConstraint("CK_EventPushDelivery_Version", "\"RuleVersion\">0 AND \"Attempts\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_EventPushDeliveries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventPushDeliveries_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventPushDeliveries_EventId_EntryId_UserId_DeviceId",
                table: "EventPushDeliveries",
                columns: new[] { "EventId", "EntryId", "UserId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventPushDeliveries_Status_Due",
                table: "EventPushDeliveries",
                columns: new[] { "Status", "Due" });

            migrationBuilder.CreateIndex(
                name: "IX_EventPushDeliveries_UserId",
                table: "EventPushDeliveries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventPushDeliveries");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "PushDevices");

            migrationBuilder.DropColumn(
                name: "SessionHash",
                table: "PushDevices");
        }
    }
}
