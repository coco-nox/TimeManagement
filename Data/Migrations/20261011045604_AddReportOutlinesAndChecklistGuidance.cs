using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportOutlinesAndChecklistGuidance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Guidance",
                table: "AssessmentChecklistItems",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "AssessmentChecklistItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportOutlines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssessmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SuggestedTotalWords = table.Column<int>(type: "INTEGER", nullable: true),
                    SectionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportOutlines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportOutlines_Assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportOutlines_AssessmentId_SessionId",
                table: "ReportOutlines",
                columns: new[] { "AssessmentId", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportOutlines");

            migrationBuilder.DropColumn(
                name: "Guidance",
                table: "AssessmentChecklistItems");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AssessmentChecklistItems");
        }
    }
}
