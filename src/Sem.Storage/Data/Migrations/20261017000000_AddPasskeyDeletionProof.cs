using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations;

public partial class AddPasskeyDeletionProof : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PasskeyDeletionProofs",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                CredentialHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SessionHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                ChallengeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasskeyDeletionProofs", x => x.Id);
                table.CheckConstraint("CK_PasskeyDeletionProof_Hashes", "length(\"CredentialHash\")=64 AND length(\"SessionHash\")=64 AND length(\"SecurityStamp\")>0 AND (\"ChallengeHash\" IS NULL OR length(\"ChallengeHash\")=64)");
                table.CheckConstraint("CK_PasskeyDeletionProof_Time", "\"CreatedAt\">=0 AND \"ExpiresAt\">\"CreatedAt\" AND (\"VerifiedAt\" IS NULL OR (\"VerifiedAt\">=\"CreatedAt\" AND \"VerifiedAt\"<\"ExpiresAt\")) AND (\"ConsumedAt\" IS NULL OR \"ConsumedAt\">=\"CreatedAt\")");
                table.CheckConstraint("CK_PasskeyDeletionProof_State", "\"ConsumedAt\" IS NOT NULL OR (\"VerifiedAt\" IS NULL AND \"ChallengeHash\" IS NOT NULL) OR (\"VerifiedAt\" IS NOT NULL AND \"ChallengeHash\" IS NULL)");
                table.ForeignKey(
                    name: "FK_PasskeyDeletionProofs_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PasskeyDeletionProofs_UserId_SessionHash",
            table: "PasskeyDeletionProofs",
            columns: new[] { "UserId", "SessionHash" },
            filter: "\"ConsumedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PasskeyDeletionProofs");
}
