using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeviceGroupId",
                table: "installer_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_groups_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_group_members",
                columns: table => new
                {
                    DeviceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_group_members", x => new { x.DeviceGroupId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_device_group_members_device_groups_DeviceGroupId",
                        column: x => x.DeviceGroupId,
                        principalTable: "device_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_device_group_members_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_installer_keys_DeviceGroupId",
                table: "installer_keys",
                column: "DeviceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_device_group_members_DeviceId",
                table: "device_group_members",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_device_groups_TenantId_Name",
                table: "device_groups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_installer_keys_device_groups_DeviceGroupId",
                table: "installer_keys",
                column: "DeviceGroupId",
                principalTable: "device_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Device group permissions are new: whoever already manages a tenant's user groups gets them there, as a
            // tenant administrator now does on registration.
            migrationBuilder.Sql("""
                INSERT INTO permission_assignments
                    ("Id", "PrincipalKind", "PrincipalId", "Permission", "Effect", "ScopeKind", "ScopeId",
                     "OwningTenantId", "IsEnabled", "CreatedByKind", "CreatedAt")
                SELECT gen_random_uuid(), holder."PrincipalKind", holder."PrincipalId", p.permission, 'Allow', 'Tenant',
                       holder."ScopeId", holder."ScopeId", TRUE, 'System', now()
                FROM (
                    SELECT DISTINCT "PrincipalKind", "PrincipalId", "ScopeId" FROM permission_assignments
                    WHERE "Permission" = 'tenant.user-groups.write' AND "Effect" = 'Allow' AND "ScopeKind" = 'Tenant' AND "IsEnabled"
                ) AS holder
                CROSS JOIN (VALUES ('tenant.device-groups.read'), ('tenant.device-groups.write'), ('device-group.assign-devices')) AS p(permission)
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Earlier versions know neither the permissions nor the scope.
            migrationBuilder.Sql("""
                DELETE FROM permission_assignments
                WHERE "ScopeKind" = 'DeviceGroup'
                   OR "Permission" IN ('tenant.device-groups.read', 'tenant.device-groups.write', 'device-group.assign-devices');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_installer_keys_device_groups_DeviceGroupId",
                table: "installer_keys");

            migrationBuilder.DropTable(
                name: "device_group_members");

            migrationBuilder.DropTable(
                name: "device_groups");

            migrationBuilder.DropIndex(
                name: "IX_installer_keys_DeviceGroupId",
                table: "installer_keys");

            migrationBuilder.DropColumn(
                name: "DeviceGroupId",
                table: "installer_keys");
        }
    }
}
