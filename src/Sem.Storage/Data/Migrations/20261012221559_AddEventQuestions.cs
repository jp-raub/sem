using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventQuestions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Public = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicText = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventQuestions", x => x.Id);
                    table.CheckConstraint("CK_EventQuestions_PublicText", "\"PublicText\"=0 OR (\"Type\"='Text' AND \"Public\"=1)");
                    table.CheckConstraint("CK_EventQuestions_Text", "length(\"Text\") BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_EventQuestions_Type", "\"Type\" IN ('YesNo','Text')");
                    table.ForeignKey(
                        name: "FK_EventQuestions_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventQuestionAnswers",
                columns: table => new
                {
                    QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventQuestionAnswers", x => new { x.QuestionId, x.UserId });
                    table.CheckConstraint("CK_EventQuestionAnswers_Value", "\"Value\" IS NULL OR length(\"Value\") <= 2000");
                    table.ForeignKey(
                        name: "FK_EventQuestionAnswers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventQuestionAnswers_EventQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "EventQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventQuestionAnswers_UserId",
                table: "EventQuestionAnswers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EventQuestions_EventId_SortOrder",
                table: "EventQuestions",
                columns: new[] { "EventId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventQuestionAnswers");

            migrationBuilder.DropTable(
                name: "EventQuestions");
        }
    }
}
