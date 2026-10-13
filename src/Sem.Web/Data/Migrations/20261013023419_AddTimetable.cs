using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimetable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClockFormat",
                table: "SessionEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "seconds");

            migrationBuilder.AddColumn<string>(
                name: "DurationFormat",
                table: "SessionEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "units");

            migrationBuilder.AddColumn<string>(
                name: "PlannedEndAt",
                table: "SessionEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RoundStarts",
                table: "SessionEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SongIcon",
                table: "SessionEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "🎵");

            migrationBuilder.CreateTable(
                name: "TimeParts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SongId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeParts", x => x.Id);
                    table.CheckConstraint("CK_TimeParts_Icon", "\"Icon\" IS NULL OR \"Icon\" IN ('🎵','💿','🎤')");
                    table.CheckConstraint("CK_TimeParts_Kind", "(\"Kind\"='Song' AND \"SongId\" IS NOT NULL AND \"Name\"='' AND \"DurationSeconds\"=0) OR (\"Kind\"='Transition' AND \"SongId\" IS NULL AND length(\"Name\") BETWEEN 1 AND 200 AND \"DurationSeconds\">=0 AND \"Icon\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_TimeParts_SessionEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "SessionEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeParts_Songs_EventId_SongId",
                        columns: x => new { x.EventId, x.SongId },
                        principalTable: "Songs",
                        principalColumns: new[] { "EventId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SessionEvents_TimeDisplay",
                table: "SessionEvents",
                sql: "\"RoundStarts\" IN (0,1) AND \"ClockFormat\" IN ('seconds','minutes') AND \"DurationFormat\" IN ('units','colon') AND \"SongIcon\" IN ('🎵','💿','🎤')");

            migrationBuilder.CreateIndex(
                name: "IX_TimeParts_EventId_SongId",
                table: "TimeParts",
                columns: new[] { "EventId", "SongId" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeParts_EventId_SortOrder",
                table: "TimeParts",
                columns: new[] { "EventId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeParts_SongId",
                table: "TimeParts",
                column: "SongId",
                unique: true,
                filter: "\"SongId\" IS NOT NULL");
            // Retain song IDs, current order, and tombstones. Assignments and answers are untouched.
            migrationBuilder.Sql("INSERT INTO TimeParts (Id,EventId,Kind,SongId,Name,DurationSeconds,Icon,SortOrder,DeletedAt) SELECT Id,EventId,'Song',Id,'',0,NULL,SortOrder,DeletedAt FROM Songs;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeParts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SessionEvents_TimeDisplay",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "ClockFormat",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "DurationFormat",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "PlannedEndAt",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "RoundStarts",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "SongIcon",
                table: "SessionEvents");
        }
    }
}
