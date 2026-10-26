using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualNotificationHistoryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationOperations_EventId_ActorId_AcceptedAt_Id",
                table: "ManualNotificationOperations",
                columns: new[] { "EventId", "ActorId", "AcceptedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualNotificationOperations_EventId_ActorId_AcceptedAt_Id",
                table: "ManualNotificationOperations");
        }
    }
}
