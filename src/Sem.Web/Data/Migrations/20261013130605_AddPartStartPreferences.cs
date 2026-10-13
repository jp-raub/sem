using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartStartPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartStartPreferences",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    PartId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartStartPreferences", x => new { x.EventId, x.UserId, x.PartId });
                    table.CheckConstraint("CK_PartStartPreference_Enabled", "\"Enabled\" IN (0,1)");
                    table.CheckConstraint("CK_PartStartPreference_Part", "length(\"PartId\") BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_PartStartPreference_Version", "\"Version\">0");
                    table.ForeignKey(
                        name: "FK_PartStartPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartStartPreferences_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartStartPreferences_UserId",
                table: "PartStartPreferences",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartStartPreferences");
        }
    }
}
