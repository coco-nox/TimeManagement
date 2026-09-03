using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddColourPalette : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ColourPalette",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: "default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColourPalette",
                table: "AspNetUsers");
        }
    }
}
