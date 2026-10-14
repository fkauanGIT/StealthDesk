using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "users",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                // Existing users follow their device until they choose.
                defaultValue: "Auto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Theme",
                table: "users");
        }
    }
}
