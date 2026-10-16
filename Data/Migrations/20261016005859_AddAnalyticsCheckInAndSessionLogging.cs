using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsCheckInAndSessionLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComparedToLastWeek",
                table: "WeeklyCheckIns",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Preparedness",
                table: "WeeklyCheckIns",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsMissed",
                table: "CalendarTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WeeklySummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    WeekStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InputHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentJson = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklySummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklySummaries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyCheckIns_UserId_WeekEnding",
                table: "WeeklyCheckIns",
                columns: new[] { "UserId", "WeekEnding" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklySummaries_UserId_WeekStart",
                table: "WeeklySummaries",
                columns: new[] { "UserId", "WeekStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklySummaries");

            migrationBuilder.DropIndex(
                name: "IX_WeeklyCheckIns_UserId_WeekEnding",
                table: "WeeklyCheckIns");

            migrationBuilder.DropColumn(
                name: "ComparedToLastWeek",
                table: "WeeklyCheckIns");

            migrationBuilder.DropColumn(
                name: "Preparedness",
                table: "WeeklyCheckIns");

            migrationBuilder.DropColumn(
                name: "IsMissed",
                table: "CalendarTasks");
        }
    }
}
