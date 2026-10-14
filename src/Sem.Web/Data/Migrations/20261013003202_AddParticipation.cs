using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParticipation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                table: "SongParts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Capacity",
                table: "SessionEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventParticipantStates",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventParticipantStates", x => new { x.EventId, x.UserId });
                    table.ForeignKey(
                        name: "FK_EventParticipantStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventParticipantStates_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventSpectators",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredAt = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSpectators", x => new { x.EventId, x.UserId });
                    table.ForeignKey(
                        name: "FK_EventSpectators_AspNetUsers_RegisteredBy",
                        column: x => x.RegisteredBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventSpectators_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventSpectators_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    PhaseId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationOperations", x => x.Id);
                    table.CheckConstraint("CK_RegistrationOperations_Kind", "\"Kind\" IN ('Register','Spectate','Answers','Release','RemoveSpectator')");
                    table.CheckConstraint("CK_RegistrationOperations_Reason", "length(\"Reason\") <= 500");
                    table.CheckConstraint("CK_RegistrationOperations_Result", "json_valid(\"ResultJson\")");
                    table.ForeignKey(
                        name: "FK_RegistrationOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegistrationOperations_AspNetUsers_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegistrationOperations_Phases_EventId_PhaseId",
                        columns: x => new { x.EventId, x.PhaseId },
                        principalTable: "Phases",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegistrationOperations_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SongParts_Version",
                table: "SongParts",
                sql: "\"RowVersion\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SessionEvents_Capacity",
                table: "SessionEvents",
                sql: "\"Capacity\" IS NULL OR \"Capacity\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_EventParticipantStates_UserId",
                table: "EventParticipantStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EventSpectators_RegisteredBy",
                table: "EventSpectators",
                column: "RegisteredBy");

            migrationBuilder.CreateIndex(
                name: "IX_EventSpectators_UserId",
                table: "EventSpectators",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationOperations_ActorId",
                table: "RegistrationOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationOperations_EventId_PhaseId",
                table: "RegistrationOperations",
                columns: new[] { "EventId", "PhaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationOperations_SubjectId",
                table: "RegistrationOperations",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventParticipantStates");

            migrationBuilder.DropTable(
                name: "EventSpectators");

            migrationBuilder.DropTable(
                name: "RegistrationOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SongParts_Version",
                table: "SongParts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SessionEvents_Capacity",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "SongParts");

            migrationBuilder.DropColumn(
                name: "Capacity",
                table: "SessionEvents");
        }
    }
}
