using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasskeyAdditionProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasskeyAdditions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionHash = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    ReauthenticationChallengeHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreationChallengeHash = table.Column<string>(type: "TEXT", nullable: true),
                    EmailTokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<long>(type: "INTEGER", nullable: false),
                    Expires = table.Column<long>(type: "INTEGER", nullable: false),
                    ReauthenticatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EmailVerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RegistrationUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Cancelled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyAdditions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyAdditions_EmailTokenHash",
                table: "PasskeyAdditions",
                column: "EmailTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PasskeyAdditions_UserId_SessionHash",
                table: "PasskeyAdditions",
                columns: new[] { "UserId", "SessionHash" },
                unique: true,
                filter: "\"Cancelled\" = 0 AND \"CompletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasskeyAdditions");
        }
    }
}
