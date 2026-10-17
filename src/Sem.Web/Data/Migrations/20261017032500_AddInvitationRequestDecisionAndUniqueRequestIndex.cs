using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddInvitationRequestDecisionAndUniqueRequestIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DecisionComment",
            table: "InvitationRequests",
            type: "TEXT",
            maxLength: 500,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Invitations_RequestId",
            table: "Invitations",
            column: "RequestId",
            unique: true,
            filter: "\"RequestId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Invitations_RequestId",
            table: "Invitations");

        migrationBuilder.DropColumn(
            name: "DecisionComment",
            table: "InvitationRequests");
    }
}