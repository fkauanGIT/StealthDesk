using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTerminalPermission : Migration
    {
        // Spelled out: this migration must keep doing what it did when it first ran, whatever the names become.
        private const string DeviceRead = "device.read";
        private const string DeviceTerminalUse = "device.terminal.use";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Device Superuser now includes the terminal. A preset isn't stored, only the assignments it made, so
            // those who received it are told by the device.read the system gave them in their tenant (on creating
            // it, or as its first administrator). A device.read granted by someone may have been meant to let them
            // look only, so it doesn't bring the terminal: running commands is granted again on purpose.
            migrationBuilder.Sql($"""
                INSERT INTO permission_assignments
                    ("Id", "PrincipalKind", "PrincipalId", "Permission", "Effect", "ScopeKind", "ScopeId",
                     "OwningTenantId", "IsEnabled", "CreatedByKind", "CreatedAt")
                SELECT gen_random_uuid(), r."PrincipalKind", r."PrincipalId", '{DeviceTerminalUse}', 'Allow', 'Tenant',
                       r."ScopeId", r."OwningTenantId", TRUE, 'System', now()
                FROM permission_assignments r
                WHERE r."Permission" = '{DeviceRead}' AND r."Effect" = 'Allow' AND r."ScopeKind" = 'Tenant'
                  AND r."CreatedByKind" = 'System' AND r."IsEnabled"
                  AND NOT EXISTS (SELECT 1 FROM permission_assignments t
                      WHERE t."PrincipalKind" = r."PrincipalKind" AND t."PrincipalId" = r."PrincipalId"
                        AND t."Permission" = '{DeviceTerminalUse}' AND t."Effect" = 'Allow'
                        AND t."ScopeKind" = 'Tenant' AND t."ScopeId" = r."ScopeId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The version before doesn't know the permission: every grant and deny of it goes.
            migrationBuilder.Sql($"""DELETE FROM permission_assignments WHERE "Permission" = '{DeviceTerminalUse}';""");
        }
    }
}
