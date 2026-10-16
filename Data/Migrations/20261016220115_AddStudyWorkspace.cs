using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStudyWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudyDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CourseId = table.Column<int>(type: "INTEGER", nullable: false),
                    AssessmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentHtml = table.Column<string>(type: "TEXT", nullable: false),
                    WordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudyDocuments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StudyDocuments_Assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StudyDocuments_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudyGuides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AssessmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceNames = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyGuides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudyGuides_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StudyGuides_Assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudySessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CalendarTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    CourseId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlannedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActiveSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    OfflineSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    InactiveSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingIdleSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    OfflineGraceUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CheckShownUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsOnBreak = table.Column<bool>(type: "INTEGER", nullable: false),
                    WordsWritten = table.Column<int>(type: "INTEGER", nullable: false),
                    ChecklistTicked = table.Column<int>(type: "INTEGER", nullable: false),
                    QuizAnswered = table.Column<int>(type: "INTEGER", nullable: false),
                    QuizCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    TutorQuestions = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSaved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudySessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudySessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StudySessions_CalendarTasks_CalendarTaskId",
                        column: x => x.CalendarTaskId,
                        principalTable: "CalendarTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StudySessions_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudyDocuments_AssessmentId",
                table: "StudyDocuments",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyDocuments_CourseId",
                table: "StudyDocuments",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyDocuments_UserId_CourseId",
                table: "StudyDocuments",
                columns: new[] { "UserId", "CourseId" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyGuides_AssessmentId",
                table: "StudyGuides",
                column: "AssessmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudyGuides_UserId",
                table: "StudyGuides",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_StudySessions_CalendarTaskId",
                table: "StudySessions",
                column: "CalendarTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StudySessions_CourseId",
                table: "StudySessions",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_StudySessions_UserId_EndedUtc",
                table: "StudySessions",
                columns: new[] { "UserId", "EndedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StudySessions_UserId_StartedUtc",
                table: "StudySessions",
                columns: new[] { "UserId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudyDocuments");

            migrationBuilder.DropTable(
                name: "StudyGuides");

            migrationBuilder.DropTable(
                name: "StudySessions");
        }
    }
}
