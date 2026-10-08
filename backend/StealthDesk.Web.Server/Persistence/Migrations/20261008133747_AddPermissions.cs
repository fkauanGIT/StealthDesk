using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissions : Migration
    {
        // The v0.3 administrator markers, which this migration turns into permission assignments.
        private const string ServerAdministratorMarker = "stealthdesk:server_admin";
        private const string TenantAdministratorMarker = "stealthdesk:tenant_admin";

        // The presets as they are today. A migration keeps its own copy: presets may change later, but this
        // migration must keep doing what it did when it first ran.
        private static readonly (string Preset, string Permission, string Scope)[] Grants =
        [
            .. Server("server.authorization-logs.read", "server.permissions.read", "server.permissions.write",
                "server.service-accounts.read", "server.service-accounts.rotate-credentials", "server.service-accounts.write",
                "server.settings.write", "server.tenants.delete", "server.tenants.read", "server.tenants.write"),
            .. Tenant("server", "tenant.authorization-logs.read", "tenant.permissions.read"),
            .. Tenant("tenant", "tenant.read", "tenant.settings.read", "tenant.settings.write", "tenant.users.read",
                "tenant.users.write", "tenant.users.delete", "tenant.user-groups.read", "tenant.user-groups.write",
                "user-group.assign-users", "tenant.permissions.read", "tenant.permissions.write", "tenant.permissions.deny",
                "tenant.authorization-logs.read", "personal-access-token.self.read", "personal-access-token.self.write",
                "personal-access-token.others.read", "personal-access-token.others.write", "service-account.read",
                "service-account.write", "service-account.rotate-credentials", "installer-key.read", "installer-key.write",
                "installer-key.manage-all", "agent.install", "device.read"),
            .. Tenant("everyone", "personal-access-token.self.read", "personal-access-token.self.write"),
        ];

        private static IEnumerable<(string, string, string)> Server(params string[] permissions) =>
            permissions.Select(x => ("server", x, "Server"));

        private static IEnumerable<(string, string, string)> Tenant(string preset, params string[] permissions) =>
            permissions.Select(x => (preset, x, "Tenant"));
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Effect = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ScopeKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwningTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_groups_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_group_members",
                columns: table => new
                {
                    UserGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_group_members", x => new { x.UserGroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_user_group_members_user_groups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalTable: "user_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_group_members_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_permission_assignments_OwningTenantId",
                table: "permission_assignments",
                column: "OwningTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_permission_assignments_PrincipalKind_PrincipalId",
                table: "permission_assignments",
                columns: new[] { "PrincipalKind", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_user_group_members_UserId",
                table: "user_group_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_groups_TenantId_Name",
                table: "user_groups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // Every user gets the baseline, marked tenant administrators their tenant's presets, and the marked
            // server administrator the server's; each permission once per user and scope. Then the markers go.
            var values = string.Join(", ", Grants.Select(x => $"('{x.Preset}', '{x.Permission}', '{x.Scope}')"));
            migrationBuilder.Sql($"""
                INSERT INTO permission_assignments
                    ("Id", "PrincipalKind", "PrincipalId", "Permission", "Effect", "ScopeKind", "ScopeId",
                     "OwningTenantId", "IsEnabled", "CreatedByKind", "CreatedAt")
                SELECT gen_random_uuid(), 'User', grant_row.user_id, grant_row.permission, 'Allow', grant_row.scope,
                       grant_row.scope_id, grant_row.scope_id, TRUE, 'System', now()
                FROM (
                    SELECT DISTINCT u."Id" AS user_id, p.permission, p.scope,
                           CASE WHEN p.scope = 'Server' THEN NULL ELSE u."TenantId" END AS scope_id
                    FROM users u
                    JOIN (VALUES {values}) AS p(preset, permission, scope)
                      ON p.preset = 'everyone'
                      OR (p.preset = 'tenant' AND EXISTS (SELECT 1 FROM user_claims c
                            WHERE c."UserId" = u."Id" AND c."ClaimType" = '{TenantAdministratorMarker}'))
                      OR (p.preset = 'server' AND EXISTS (SELECT 1 FROM user_claims c
                            WHERE c."UserId" = u."Id" AND c."ClaimType" = '{ServerAdministratorMarker}'))
                ) AS grant_row;

                DELETE FROM user_claims WHERE "ClaimType" IN ('{ServerAdministratorMarker}', '{TenantAdministratorMarker}');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to markers: whoever may manage permissions at the server or in their tenant is marked again.
            migrationBuilder.Sql($"""
                INSERT INTO user_claims ("UserId", "ClaimType", "ClaimValue")
                SELECT DISTINCT "PrincipalId", '{ServerAdministratorMarker}', 'true' FROM permission_assignments
                WHERE "PrincipalKind" = 'User' AND "Permission" = 'server.permissions.write' AND "Effect" = 'Allow' AND "IsEnabled";

                INSERT INTO user_claims ("UserId", "ClaimType", "ClaimValue")
                SELECT DISTINCT "PrincipalId", '{TenantAdministratorMarker}', 'true' FROM permission_assignments
                WHERE "PrincipalKind" = 'User' AND "Permission" = 'tenant.permissions.write' AND "Effect" = 'Allow' AND "IsEnabled";
                """);

            migrationBuilder.DropTable(
                name: "permission_assignments");

            migrationBuilder.DropTable(
                name: "user_group_members");

            migrationBuilder.DropTable(
                name: "user_groups");
        }
    }
}
