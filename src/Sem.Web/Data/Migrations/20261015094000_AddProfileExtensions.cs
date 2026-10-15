using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddProfileExtensions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Icon", table: "UserProfiles", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddCheckConstraint(name: "CK_UserProfiles_Icon", table: "UserProfiles", sql: "length(\"Icon\")<=64");

        migrationBuilder.CreateTable(
            name: "ProfileQuestions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Text = table.Column<string>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProfileQuestions", x => x.Id);
                table.CheckConstraint("CK_ProfileQuestions_Type", "\"Type\" IN ('Text','YesNo')");
                table.CheckConstraint("CK_ProfileQuestions_Text", "length(trim(\"Text\")) BETWEEN 1 AND 200");
                table.CheckConstraint("CK_ProfileQuestions_Enabled", "\"Enabled\" IN (0,1)");
                table.CheckConstraint("CK_ProfileQuestions_Order", "\"SortOrder\">=0 AND \"RowVersion\">=0");
            });

        migrationBuilder.CreateTable(
            name: "ProfileQrCredentials",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                IssuedAt = table.Column<string>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProfileQrCredentials", x => x.UserId);
                table.CheckConstraint("CK_ProfileQrCredentials_Hash", "length(\"TokenHash\")=64");
                table.CheckConstraint("CK_ProfileQrCredentials_Expiry", "\"ExpiresAt\">\"IssuedAt\" AND \"RowVersion\">0");
                table.ForeignKey("FK_ProfileQrCredentials_AspNetUsers_UserId", x => x.UserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ProfileAnswers",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProfileAnswers", x => new { x.UserId, x.QuestionId });
                table.CheckConstraint("CK_ProfileAnswers_Value", "length(\"Value\")<=1000");
                table.ForeignKey("FK_ProfileAnswers_AspNetUsers_UserId", x => x.UserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ProfileAnswers_ProfileQuestions_QuestionId", x => x.QuestionId,
                    "ProfileQuestions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_ProfileQuestions_SortOrder_Id", "ProfileQuestions", new[] { "SortOrder", "Id" });
        migrationBuilder.CreateIndex("IX_ProfileQrCredentials_TokenHash", "ProfileQrCredentials", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_ProfileAnswers_QuestionId", "ProfileAnswers", "QuestionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_UserProfiles_Icon", table: "UserProfiles");
        migrationBuilder.DropTable(name: "ProfileAnswers");
        migrationBuilder.DropTable(name: "ProfileQrCredentials");
        migrationBuilder.DropTable(name: "ProfileQuestions");
        migrationBuilder.DropColumn(name: "Icon", table: "UserProfiles");
    }
}
