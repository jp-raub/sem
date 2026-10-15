using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddThemeCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ThemeCatalogEntries",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                Accent = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                Background = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                Surface = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                Text = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                BuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ThemeCatalogEntries", x => x.Id);
                table.CheckConstraint("CK_ThemeCatalog_Id", "length(\"Id\") BETWEEN 1 AND 64 AND \"Id\" NOT GLOB '*[^a-z0-9-]*' AND \"Id\" NOT GLOB '-*' AND \"Id\" NOT GLOB '*-' AND \"Id\" NOT GLOB '*--*'");
                table.CheckConstraint("CK_ThemeCatalog_Name", "length(\"Name\") BETWEEN 1 AND 30 AND length(trim(\"Name\")) >= 1");
                table.CheckConstraint("CK_ThemeCatalog_Colors", "length(\"Accent\")=7 AND \"Accent\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]' AND length(\"Background\")=7 AND \"Background\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]' AND length(\"Surface\")=7 AND \"Surface\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]' AND length(\"Text\")=7 AND \"Text\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'");
                table.CheckConstraint("CK_ThemeCatalog_FlagsAndVersion", "\"BuiltIn\" IN (0,1) AND \"RowVersion\">=0");
            });

        migrationBuilder.Sql("CREATE TRIGGER TR_ThemeCatalog_BuiltInImmutable BEFORE UPDATE OF BuiltIn ON ThemeCatalogEntries WHEN NEW.BuiltIn <> OLD.BuiltIn BEGIN SELECT RAISE(ABORT, 'BuiltIn is immutable'); END;");
        var rows = new object[,]
        {
            { "green", "Forest", "#1E6557", "#F5F7F3", "#FFFEFA", "#18312E", true, 0L },
            { "blue", "Ocean", "#285F91", "#F5F7F3", "#FFFEFA", "#18312E", true, 0L },
            { "purple", "Plum", "#775087", "#F5F7F3", "#FFFEFA", "#18312E", true, 0L },
            { "amber", "Amber", "#906322", "#F5F7F3", "#FFFEFA", "#18312E", true, 0L },
            { "rose", "Rose", "#A14365", "#FBF3F6", "#FFFAFB", "#36212A", true, 0L },
            { "teal", "Teal", "#14766E", "#EFF8F7", "#FAFFFF", "#183A37", true, 0L },
            { "slate", "Slate", "#526577", "#F1F4F7", "#FFFFFF", "#263440", true, 0L },
            { "navy", "Midnight", "#31547B", "#EDF2F8", "#F8FBFF", "#1E3047", true, 0L },
            { "coral", "Coral", "#AA513F", "#FFF3ED", "#FFFAF6", "#482D25", true, 0L },
            { "mono", "Monochrome", "#555555", "#F5F5F5", "#FFFFFF", "#252525", true, 0L }
        };
        migrationBuilder.InsertData("ThemeCatalogEntries", new[] { "Id", "Name", "Accent", "Background", "Surface", "Text", "BuiltIn", "RowVersion" },
            new[] { "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "INTEGER", "INTEGER" }, rows);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ThemeCatalog_BuiltInImmutable;");
        migrationBuilder.DropTable(name: "ThemeCatalogEntries");
    }
}
