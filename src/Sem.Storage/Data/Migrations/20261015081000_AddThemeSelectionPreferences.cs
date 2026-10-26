using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddThemeSelectionPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserThemePreferences",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                ThemeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Mode = table.Column<int>(type: "INTEGER", nullable: true),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserThemePreferences", x => x.UserId);
                table.CheckConstraint("CK_UserThemePreferences_Mode", "\"Mode\" IS NULL OR \"Mode\" IN (0,1,2)");
                table.CheckConstraint("CK_UserThemePreferences_RowVersion", "\"RowVersion\">=0");
                table.ForeignKey("FK_UserThemePreferences_AspNetUsers_UserId", x => x.UserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_UserThemePreferences_ThemeCatalogEntries_ThemeId", x => x.ThemeId,
                    "ThemeCatalogEntries", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "EventThemePreferences",
            columns: table => new
            {
                EventId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                ThemeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Mode = table.Column<int>(type: "INTEGER", nullable: true),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EventThemePreferences", x => x.EventId);
                table.CheckConstraint("CK_EventThemePreferences_Mode", "\"Mode\" IS NULL OR \"Mode\" IN (0,1,2)");
                table.CheckConstraint("CK_EventThemePreferences_RowVersion", "\"RowVersion\">=0");
                table.ForeignKey("FK_EventThemePreferences_SessionEvents_EventId", x => x.EventId,
                    "SessionEvents", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_EventThemePreferences_ThemeCatalogEntries_ThemeId", x => x.ThemeId,
                    "ThemeCatalogEntries", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "SiteThemePreferences",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ThemeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Mode = table.Column<int>(type: "INTEGER", nullable: true),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SiteThemePreferences", x => x.Id);
                table.CheckConstraint("CK_SiteThemePreferences_Id", "\"Id\"=1");
                table.CheckConstraint("CK_SiteThemePreferences_Mode", "\"Mode\" IS NULL OR \"Mode\" IN (0,1,2)");
                table.CheckConstraint("CK_SiteThemePreferences_RowVersion", "\"RowVersion\">=0");
                table.ForeignKey("FK_SiteThemePreferences_ThemeCatalogEntries_ThemeId", x => x.ThemeId,
                    "ThemeCatalogEntries", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserThemePreferences_ThemeId",
            table: "UserThemePreferences",
            column: "ThemeId");
        migrationBuilder.CreateIndex(
            name: "IX_EventThemePreferences_ThemeId",
            table: "EventThemePreferences",
            column: "ThemeId");
        migrationBuilder.CreateIndex(
            name: "IX_SiteThemePreferences_ThemeId",
            table: "SiteThemePreferences",
            column: "ThemeId");

        migrationBuilder.Sql("CREATE TRIGGER TR_ThemeCatalog_BuiltInDelete BEFORE DELETE ON ThemeCatalogEntries WHEN OLD.BuiltIn = 1 BEGIN SELECT RAISE(ABORT, 'BuiltIn themes cannot be deleted'); END;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ThemeCatalog_BuiltInDelete;");
        migrationBuilder.DropTable(name: "EventThemePreferences");
        migrationBuilder.DropTable(name: "SiteThemePreferences");
        migrationBuilder.DropTable(name: "UserThemePreferences");
    }
}