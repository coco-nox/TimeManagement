using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedUtc",
                table: "ChatMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "ChatMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Chat history written before conversations existed has no real
            // conversation boundary. Rather than leave it looking like an
            // active (unarchived) conversation - which would make every
            // existing course's Tutor page open with old history still
            // showing, contradicting the fresh start this feature is meant
            // to enable - mark it archived as of this migration. All of it
            // already shares the default all-zero ConversationId set above,
            // so it reads as one archived legacy thread per course.
            migrationBuilder.Sql(
                "UPDATE ChatMessages SET ArchivedUtc = CURRENT_TIMESTAMP WHERE ArchivedUtc IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_CourseId_UserId_ArchivedUtc",
                table: "ChatMessages",
                columns: new[] { "CourseId", "UserId", "ArchivedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_CourseId_UserId_ArchivedUtc",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ArchivedUtc",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "ChatMessages");
        }
    }
}
