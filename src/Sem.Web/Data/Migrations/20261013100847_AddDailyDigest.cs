using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyDigestRuns",
                columns: table => new
                {
                    Day = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyDigestRuns", x => x.Day);
                    table.CheckConstraint("CK_DailyDigestRun_Day", "length(\"Day\")=10 AND date(\"Day\") IS NOT NULL AND date(\"Day\")=\"Day\"");
                });

            migrationBuilder.CreateTable(
                name: "DailyDigestMails",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Day = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Due = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Claim = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyDigestMails", x => x.Id);
                    table.CheckConstraint("CK_DailyDigestMail_Attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_DailyDigestMail_Claim", "(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
                    table.CheckConstraint("CK_DailyDigestMail_Sent", "(\"Status\"='Sent' AND \"SentAt\" IS NOT NULL) OR (\"Status\"<>'Sent' AND \"SentAt\" IS NULL)");
                    table.CheckConstraint("CK_DailyDigestMail_Status", "\"Status\" IN ('Pending','Sending','Sent','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_DailyDigestMails_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DailyDigestMails_DailyDigestRuns_Day",
                        column: x => x.Day,
                        principalTable: "DailyDigestRuns",
                        principalColumn: "Day",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyDigestMails_Day_UserId",
                table: "DailyDigestMails",
                columns: new[] { "Day", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyDigestMails_Status_Due",
                table: "DailyDigestMails",
                columns: new[] { "Status", "Due" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyDigestMails_UserId",
                table: "DailyDigestMails",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyDigestMails");

            migrationBuilder.DropTable(
                name: "DailyDigestRuns");
        }
    }
}
