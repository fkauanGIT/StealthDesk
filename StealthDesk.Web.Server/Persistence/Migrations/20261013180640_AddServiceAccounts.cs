using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_accounts", x => x.Id);
                    table.CheckConstraint("CK_service_accounts_kind_tenant", "(\"Kind\" = 'Server' AND \"TenantId\" IS NULL) OR (\"Kind\" = 'Tenant' AND \"TenantId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_service_accounts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_account_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HashedSecret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_account_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_account_credentials_service_accounts_ServiceAccount~",
                        column: x => x.ServiceAccountId,
                        principalTable: "service_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_account_credentials_ServiceAccountId",
                table: "service_account_credentials",
                column: "ServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_service_accounts_Name",
                table: "service_accounts",
                column: "Name",
                unique: true,
                filter: "\"Kind\" = 'Server'");

            migrationBuilder.CreateIndex(
                name: "IX_service_accounts_TenantId_Name",
                table: "service_accounts",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"Kind\" = 'Tenant'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_account_credentials");

            migrationBuilder.DropTable(
                name: "service_accounts");
        }
    }
}
