using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkQrReceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite rebuilds EventReceptions before EventQrReads. Make the composite
            // parent key available before the child rebuild; this temporary index is
            // dropped with the original read table when its alternate key is installed.
            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_LinkQrReceptions_ReadReference\" ON \"EventQrReads\" (\"EventId\",\"UserId\",\"Id\");");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reception_Method",
                table: "EventReceptions");

            migrationBuilder.AddColumn<string>(
                name: "ReadId",
                table: "EventReceptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_EventQrReads_EventId_UserId_Id",
                table: "EventQrReads",
                columns: new[] { "EventId", "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EventReceptions_EventId_UserId_ReadId",
                table: "EventReceptions",
                columns: new[] { "EventId", "UserId", "ReadId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reception_Method",
                table: "EventReceptions",
                sql: "(\"Method\"='Manual' AND \"ReadId\" IS NULL) OR (\"Method\"='QR' AND \"ReadId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_EventReceptions_EventQrReads_EventId_UserId_ReadId",
                table: "EventReceptions",
                columns: new[] { "EventId", "UserId", "ReadId" },
                principalTable: "EventQrReads",
                principalColumns: new[] { "EventId", "UserId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventReceptions_EventQrReads_EventId_UserId_ReadId",
                table: "EventReceptions");

            migrationBuilder.DropIndex(
                name: "IX_EventReceptions_EventId_UserId_ReadId",
                table: "EventReceptions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reception_Method",
                table: "EventReceptions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_EventQrReads_EventId_UserId_Id",
                table: "EventQrReads");

            migrationBuilder.DropColumn(
                name: "ReadId",
                table: "EventReceptions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reception_Method",
                table: "EventReceptions",
                sql: "\"Method\"='Manual'");
        }
    }
}
