using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderDismissedUtc",
                table: "CalendarTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderEmailSentUtc",
                table: "CalendarTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailRemindersEnabled",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderLeadMinutes",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarTasks_ScheduledStart",
                table: "CalendarTasks",
                column: "ScheduledStart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalendarTasks_ScheduledStart",
                table: "CalendarTasks");

            migrationBuilder.DropColumn(
                name: "ReminderDismissedUtc",
                table: "CalendarTasks");

            migrationBuilder.DropColumn(
                name: "ReminderEmailSentUtc",
                table: "CalendarTasks");

            migrationBuilder.DropColumn(
                name: "EmailRemindersEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReminderLeadMinutes",
                table: "AspNetUsers");
        }
    }
}
