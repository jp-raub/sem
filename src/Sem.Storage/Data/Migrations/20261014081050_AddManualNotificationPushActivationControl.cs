using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualNotificationPushActivationControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualNotificationPushActivationOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpectedEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    Changed = table.Column<bool>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationPushActivationOperations", x => x.Id);
                    table.CheckConstraint("CK_ManualPushActivationOperation_Request", "length(\"Id\")=36 AND \"ExpectedEpoch\">=0 AND \"ResultEpoch\">=0");
                    table.CheckConstraint("CK_ManualPushActivationOperation_Result", "(\"Changed\"=1 AND \"ExpectedEpoch\"<9223372036854775807 AND \"ResultEpoch\"=\"ExpectedEpoch\"+1) OR (\"Changed\"=0 AND \"ResultEpoch\"=\"ExpectedEpoch\")");
                    table.ForeignKey(
                        name: "FK_ManualNotificationPushActivationOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualNotificationPushActivationOperations_ActorId",
                table: "ManualNotificationPushActivationOperations",
                column: "ActorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualNotificationPushActivationOperations");
        }
    }
}
