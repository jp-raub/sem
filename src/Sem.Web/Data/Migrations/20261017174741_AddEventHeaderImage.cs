using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventHeaderImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventHeaderImages",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventHeaderImages", x => x.EventId);
                    table.CheckConstraint("CK_EventHeaderImages_Data", "length(\"Data\") BETWEEN 8 AND 1048576");
                    table.CheckConstraint("CK_EventHeaderImages_Size", "\"Width\" BETWEEN 1 AND 1600 AND \"Height\" BETWEEN 1 AND 1600");
                    table.ForeignKey(
                        name: "FK_EventHeaderImages_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventHeaderImages");
        }
    }
}
