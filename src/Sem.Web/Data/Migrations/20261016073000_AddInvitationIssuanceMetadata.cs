using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddInvitationIssuanceMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Purpose",
            table: "Invitations",
            type: "TEXT",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IssuanceSource",
            table: "Invitations",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Purpose", table: "Invitations");
        migrationBuilder.DropColumn(name: "IssuanceSource", table: "Invitations");
    }
}