using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventProgressStates",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventProgressStates", x => x.EventId);
                    table.CheckConstraint("CK_ProgressState", "\"RowVersion\">=0 AND \"SourceVersion\">=0 AND json_valid(\"PlanJson\")");
                    table.ForeignKey(
                        name: "FK_EventProgressStates_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProgressEstimates",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    PartId = table.Column<string>(type: "TEXT", nullable: false),
                    PrepSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressEstimates", x => new { x.EventId, x.PartId });
                    table.CheckConstraint("CK_ProgressEstimate", "(\"PrepSeconds\" IS NULL OR \"PrepSeconds\">=0) AND (\"DurationSeconds\" IS NULL OR \"DurationSeconds\">=0)");
                    table.ForeignKey(
                        name: "FK_ProgressEstimates_EventProgressStates_EventId",
                        column: x => x.EventId,
                        principalTable: "EventProgressStates",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProgressOperations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressOperations", x => x.Id);
                    table.UniqueConstraint("AK_ProgressOperations_EventId_Id", x => new { x.EventId, x.Id });
                    table.CheckConstraint("CK_ProgressOperation", "\"Kind\" IN ('Record','Estimate') AND json_valid(\"ResultJson\")");
                    table.ForeignKey(
                        name: "FK_ProgressOperations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProgressOperations_EventProgressStates_EventId",
                        column: x => x.EventId,
                        principalTable: "EventProgressStates",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProgressMarks",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    PartId = table.Column<string>(type: "TEXT", nullable: false),
                    Point = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressMarks", x => new { x.EventId, x.PartId, x.Point });
                    table.CheckConstraint("CK_ProgressMark_Point", "\"Point\" IN ('PrepStart','Start','End')");
                    table.ForeignKey(
                        name: "FK_ProgressMarks_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProgressMarks_EventProgressStates_EventId",
                        column: x => x.EventId,
                        principalTable: "EventProgressStates",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProgressMarks_ProgressOperations_EventId_OperationId",
                        columns: x => new { x.EventId, x.OperationId },
                        principalTable: "ProgressOperations",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProgressMarks_ActorId",
                table: "ProgressMarks",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ProgressMarks_EventId_OperationId",
                table: "ProgressMarks",
                columns: new[] { "EventId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProgressOperations_ActorId",
                table: "ProgressOperations",
                column: "ActorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProgressEstimates");

            migrationBuilder.DropTable(
                name: "ProgressMarks");

            migrationBuilder.DropTable(
                name: "ProgressOperations");

            migrationBuilder.DropTable(
                name: "EventProgressStates");
        }
    }
}
