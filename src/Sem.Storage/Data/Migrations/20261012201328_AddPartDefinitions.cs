using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventPartDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePresetId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPartDefinitions", x => x.Id);
                    table.CheckConstraint("CK_EventPartDefinitions_Category", "\"Category\" IN ('Sing','Play')");
                    table.CheckConstraint("CK_EventPartDefinitions_Name", "length(\"Name\") BETWEEN 1 AND 40");
                    table.ForeignKey(
                        name: "FK_EventPartDefinitions_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartPresets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartPresets", x => x.Id);
                    table.CheckConstraint("CK_PartPresets_Category", "\"Category\" IN ('Sing','Play')");
                    table.CheckConstraint("CK_PartPresets_Name", "length(\"Name\") BETWEEN 1 AND 40");
                });

            migrationBuilder.CreateTable(
                name: "PartPresetStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartPresetStates", x => x.Id);
                    table.CheckConstraint("CK_PartPresetStates_Id", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventPartDefinitions_EventId_NormalizedName",
                table: "EventPartDefinitions",
                columns: new[] { "EventId", "NormalizedName" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PartPresets_NormalizedName",
                table: "PartPresets",
                column: "NormalizedName",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
            migrationBuilder.Sql("INSERT INTO PartPresetStates(Id,RowVersion) VALUES (1,0);");
            migrationBuilder.Sql("INSERT INTO PartPresets(Id,Name,NormalizedName,Category,SortOrder) VALUES ('preset-vo','Vo','VO','Sing',0);");
            migrationBuilder.Sql("INSERT INTO PartPresets(Id,Name,NormalizedName,Category,SortOrder) VALUES ('preset-cho','Cho','CHO','Sing',1);");
            migrationBuilder.Sql("INSERT INTO PartPresets(Id,Name,NormalizedName,Category,SortOrder) VALUES ('preset-gt','Gt','GT','Play',2);");
            migrationBuilder.Sql("INSERT INTO PartPresets(Id,Name,NormalizedName,Category,SortOrder) VALUES ('preset-ba','Ba','BA','Play',3);");
            migrationBuilder.Sql("INSERT INTO PartPresets(Id,Name,NormalizedName,Category,SortOrder) VALUES ('preset-dr','Dr','DR','Play',4);");
            migrationBuilder.Sql("INSERT INTO PartPresets(Id,Name,NormalizedName,Category,SortOrder) VALUES ('preset-key','Key','KEY','Play',5);");
            // Existing formal events have no Songs/SongParts in the previous schema. Preserve every event/admin/phase row.
            migrationBuilder.Sql("INSERT INTO EventPartDefinitions(Id,EventId,SourcePresetId,Name,NormalizedName,Category,SortOrder) SELECT e.Id || ':part:' || p.Id,e.Id,p.Id,p.Name,p.NormalizedName,p.Category,p.SortOrder FROM SessionEvents e CROSS JOIN PartPresets p;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventPartDefinitions");

            migrationBuilder.DropTable(
                name: "PartPresets");

            migrationBuilder.DropTable(
                name: "PartPresetStates");
        }
    }
}
