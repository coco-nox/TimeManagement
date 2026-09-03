using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations;

/// <inheritdoc />
public partial class AddAssessmentsAndAssessmentDocumentHierarchy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Documents_Courses_CourseId",
            table: "Documents");

        migrationBuilder.RenameColumn(
            name: "CourseId",
            table: "Documents",
            newName: "AssessmentId");

        migrationBuilder.RenameIndex(
            name: "IX_Documents_CourseId",
            table: "Documents",
            newName: "IX_Documents_AssessmentId");

        migrationBuilder.CreateTable(
            name: "Assessments",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                CourseId = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Category = table.Column<int>(type: "INTEGER", nullable: false),
                DueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                DueDateConfirmed = table.Column<bool>(type: "INTEGER", defaultValue: false, nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Assessments", x => x.Id);
                table.ForeignKey(
                    name: "FK_Assessments_Courses_CourseId",
                    column: x => x.CourseId,
                    principalTable: "Courses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Assessments_CourseId",
            table: "Assessments",
            column: "CourseId");

        // Existing SQLite data may still reference CourseId values, because the
        // original document-to-course schema was not migrated to assessments yet.
        // Create one default assessment per course so the data copy can satisfy the
        // new foreign key in a legacy database.
        migrationBuilder.Sql(@"
            INSERT INTO ""Assessments"" (""Id"", ""CourseId"", ""Title"", ""Category"", ""DueDate"", ""DueDateConfirmed"", ""CreatedUtc"")
            SELECT c.""Id"", c.""Id"", c.""Title"", 0, NULL, 0, c.""CreatedUtc""
            FROM ""Courses"" AS c
            WHERE NOT EXISTS (
                SELECT 1
                FROM ""Assessments"" AS a
                WHERE a.""Id"" = c.""Id""
            );
        ");

        migrationBuilder.AddForeignKey(
            name: "FK_Documents_Assessments_AssessmentId",
            table: "Documents",
            column: "AssessmentId",
            principalTable: "Assessments",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Documents_Assessments_AssessmentId",
            table: "Documents");

        migrationBuilder.DropTable(
            name: "Assessments");

        migrationBuilder.RenameColumn(
            name: "AssessmentId",
            table: "Documents",
            newName: "CourseId");

        migrationBuilder.RenameIndex(
            name: "IX_Documents_AssessmentId",
            table: "Documents",
            newName: "IX_Documents_CourseId");

        migrationBuilder.AddForeignKey(
            name: "FK_Documents_Courses_CourseId",
            table: "Documents",
            column: "CourseId",
            principalTable: "Courses",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
