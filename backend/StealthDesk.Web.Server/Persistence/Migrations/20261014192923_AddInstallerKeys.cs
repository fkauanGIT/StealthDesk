using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallerKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installer_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatorKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    HashedSecret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AllowedUses = table.Column<int>(type: "integer", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installer_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_installer_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "installer_key_usages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallerKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installer_key_usages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_installer_key_usages_installer_keys_InstallerKeyId",
                        column: x => x.InstallerKeyId,
                        principalTable: "installer_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_installer_key_usages_InstallerKeyId",
                table: "installer_key_usages",
                column: "InstallerKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_installer_key_usages_UsedAt",
                table: "installer_key_usages",
                column: "UsedAt");

            migrationBuilder.CreateIndex(
                name: "IX_installer_keys_TenantId",
                table: "installer_keys",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installer_key_usages");

            migrationBuilder.DropTable(
                name: "installer_keys");
        }
    }
}
