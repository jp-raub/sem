using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualNotificationPushAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualNotificationPushActivations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Epoch = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationPushActivations", x => x.Id);
                    table.CheckConstraint("CK_ManualPushActivation_State", "\"Id\"=1 AND \"Epoch\">0 AND \"Enabled\" IN (0,1)");
                });

            migrationBuilder.CreateTable(
                name: "ManualNotificationPushAdmissions",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    Epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualNotificationPushAdmissions", x => x.OperationId);
                    table.CheckConstraint("CK_ManualPushAdmission_Epoch", "\"Epoch\">0");
                    table.CheckConstraint("CK_ManualPushAdmission_Time", "\"AcceptedAt\">=0 AND \"AcceptedAt\"<=9223372036854775207 AND \"ExpiresAt\"=\"AcceptedAt\"+600");
                    table.ForeignKey(
                        name: "FK_ManualNotificationPushAdmissions_ManualNotificationOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ManualNotificationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualNotificationPushActivations");

            migrationBuilder.DropTable(
                name: "ManualNotificationPushAdmissions");
        }
    }
}
