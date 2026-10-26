using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartStartDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EventPushDelivery_Kind",
                table: "EventPushDeliveries");

            migrationBuilder.AddColumn<long>(
                name: "ArmedAt",
                table: "PartStartPreferences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ArmedProgressVersion",
                table: "PartStartPreferences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_EventPushDelivery_Kind",
                table: "EventPushDeliveries",
                sql: "\"Kind\" IN ('OpeningReminder','SongPreparation','PartStart')");

            // Preserve existing opt-ins, but never replay Starts already recorded before adoption.
            migrationBuilder.Sql("""
                UPDATE PartStartPreferences
                SET ArmedAt=CAST((julianday('now')-2440587.5)*86400000 AS INTEGER),
                    ArmedProgressVersion=COALESCE((SELECT RowVersion FROM EventProgressStates WHERE EventId=PartStartPreferences.EventId),0)
                WHERE Enabled=1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EventPushDelivery_Kind",
                table: "EventPushDeliveries");

            migrationBuilder.DropColumn(
                name: "ArmedAt",
                table: "PartStartPreferences");

            migrationBuilder.DropColumn(
                name: "ArmedProgressVersion",
                table: "PartStartPreferences");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EventPushDelivery_Kind",
                table: "EventPushDeliveries",
                sql: "\"Kind\" IN ('OpeningReminder','SongPreparation')");
        }
    }
}
