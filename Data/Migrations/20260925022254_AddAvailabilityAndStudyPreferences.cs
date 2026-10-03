using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations;

/// <inheritdoc />
public partial class AddAvailabilityAndStudyPreferences : Migration
{
    private static readonly string[] AvailabilityBlocksUserIdDateHourColumns = ["UserId", "Date", "Hour"];
    private static readonly string[] CourseHoursPreferencesUserIdCourseIdColumns = ["UserId", "CourseId"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AvailabilityBlocks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                Hour = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AvailabilityBlocks", x => x.Id);
                table.ForeignKey(
                    name: "FK_AvailabilityBlocks_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CourseHoursPreferences",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                CourseId = table.Column<int>(type: "INTEGER", nullable: false),
                HoursPerWeek = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CourseHoursPreferences", x => x.Id);
                table.ForeignKey(
                    name: "FK_CourseHoursPreferences_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CourseHoursPreferences_Courses_CourseId",
                    column: x => x.CourseId,
                    principalTable: "Courses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "StudyPreferences",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                TotalWeeklyHours = table.Column<int>(type: "INTEGER", nullable: false),
                StudyDays = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StudyPreferences", x => x.Id);
                table.ForeignKey(
                    name: "FK_StudyPreferences_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AvailabilityBlocks_UserId_Date_Hour",
            table: "AvailabilityBlocks",
            columns: AvailabilityBlocksUserIdDateHourColumns,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CourseHoursPreferences_CourseId",
            table: "CourseHoursPreferences",
            column: "CourseId");

        migrationBuilder.CreateIndex(
            name: "IX_CourseHoursPreferences_UserId_CourseId",
            table: "CourseHoursPreferences",
            columns: CourseHoursPreferencesUserIdCourseIdColumns,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_StudyPreferences_UserId",
            table: "StudyPreferences",
            column: "UserId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AvailabilityBlocks");

        migrationBuilder.DropTable(
            name: "CourseHoursPreferences");

        migrationBuilder.DropTable(
            name: "StudyPreferences");
    }
}
