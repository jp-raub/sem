using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationScheduleStates",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    EventVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ProgressVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationScheduleStates", x => x.EventId);
                    table.CheckConstraint("CK_NotificationSchedule_Versions", "\"Version\">0 AND \"EventVersion\">=0 AND \"ProgressVersion\">=0");
                    table.ForeignKey(
                        name: "FK_NotificationScheduleStates_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReservations",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    PartId = table.Column<string>(type: "TEXT", nullable: true),
                    Basis = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousPartId = table.Column<string>(type: "TEXT", nullable: true),
                    ReferenceAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReservations", x => new { x.EventId, x.Id });
                    table.CheckConstraint("CK_Reservation_Kind", "\"Kind\" IN ('OpeningReminder','SongPreparation')");
                    table.CheckConstraint("CK_Reservation_Reason", "(\"State\"='Cancelled' AND \"CancelReason\" IS NOT NULL AND \"CancelReason\" IN ('PartRemoved','TargetStarted')) OR (\"State\"<>'Cancelled' AND \"CancelReason\" IS NULL)");
                    table.CheckConstraint("CK_Reservation_State", "\"State\" IN ('Planned','Due','WaitingActual','Cancelled')");
                    table.CheckConstraint("CK_Reservation_Time", "(\"State\"='WaitingActual' AND \"DueAt\" IS NULL AND \"ReferenceAt\" IS NULL) OR (\"State\" IN ('Planned','Due') AND \"DueAt\" IS NOT NULL AND \"ReferenceAt\" IS NOT NULL) OR \"State\"='Cancelled'");
                    table.CheckConstraint("CK_Reservation_Version", "\"Version\">0");
                    table.ForeignKey(
                        name: "FK_NotificationReservations_NotificationScheduleStates_EventId",
                        column: x => x.EventId,
                        principalTable: "NotificationScheduleStates",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReservations_State_DueAt",
                table: "NotificationReservations",
                columns: new[] { "State", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationReservations");

            migrationBuilder.DropTable(
                name: "NotificationScheduleStates");
        }
    }
}
