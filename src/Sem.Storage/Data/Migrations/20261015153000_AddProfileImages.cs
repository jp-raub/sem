using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddProfileImages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProfileImages",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                Width = table.Column<int>(type: "INTEGER", nullable: false),
                Height = table.Column<int>(type: "INTEGER", nullable: false),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProfileImages", x => x.UserId);
                table.CheckConstraint("CK_ProfileImages_Data", "(length(\"Data\")=0 AND \"Width\"=0 AND \"Height\"=0) OR (length(\"Data\") BETWEEN 8 AND 1048576 AND \"Width\" BETWEEN 1 AND 512 AND \"Height\" BETWEEN 1 AND 512)");
                table.CheckConstraint("CK_ProfileImages_RowVersion", "\"RowVersion\">0");
                table.ForeignKey("FK_ProfileImages_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "ProfileImages");
}
