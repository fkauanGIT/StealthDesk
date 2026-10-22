using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DnsName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OsDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OsArchitecture = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Is64BitOs = table.Column<bool>(type: "boolean", nullable: false),
                    CpuCores = table.Column<int>(type: "integer", nullable: false),
                    CpuLoad = table.Column<double>(type: "double precision", nullable: false),
                    MemoryTotalGb = table.Column<double>(type: "double precision", nullable: false),
                    MemoryUsedGb = table.Column<double>(type: "double precision", nullable: false),
                    StorageTotalGb = table.Column<double>(type: "double precision", nullable: false),
                    StorageUsedGb = table.Column<double>(type: "double precision", nullable: false),
                    LoggedOnUsers = table.Column<string[]>(type: "text[]", nullable: false),
                    MacAddresses = table.Column<string[]>(type: "text[]", nullable: false),
                    LocalIpV4 = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    LocalIpV6 = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PublicIpV4 = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    PublicIpV6 = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConnectionId = table.Column<string>(type: "text", nullable: false),
                    IsOnline = table.Column<bool>(type: "boolean", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Disks = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_devices_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_devices_TenantId",
                table: "devices",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
