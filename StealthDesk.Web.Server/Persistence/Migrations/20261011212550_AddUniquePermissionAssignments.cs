using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniquePermissionAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_permission_assignments_PrincipalKind_PrincipalId_Permission~",
                table: "permission_assignments",
                columns: new[] { "PrincipalKind", "PrincipalId", "Permission", "Effect", "ScopeKind", "ScopeId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_permission_assignments_PrincipalKind_PrincipalId_Permission~",
                table: "permission_assignments");
        }
    }
}
