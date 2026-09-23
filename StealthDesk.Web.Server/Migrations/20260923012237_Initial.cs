using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StealthDesk.Web.Server.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConnectionId = table.Column<string>(type: "text", nullable: false),
                    CpuUtilization = table.Column<double>(type: "double precision", nullable: false),
                    CurrentUsers = table.Column<string[]>(type: "text[]", nullable: false),
                    DnsHostName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Is64Bit = table.Column<bool>(type: "boolean", nullable: false),
                    IsOnline = table.Column<bool>(type: "boolean", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LocalIpV4 = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    LocalIpV6 = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    MacAddresses = table.Column<string[]>(type: "text[]", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OsArchitecture = table.Column<int>(type: "integer", nullable: false),
                    OsDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    ProcessorCount = table.Column<int>(type: "integer", nullable: false),
                    PublicIpV4 = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    PublicIpV6 = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TotalMemory = table.Column<double>(type: "double precision", nullable: false),
                    TotalStorage = table.Column<double>(type: "double precision", nullable: false),
                    UsedMemory = table.Column<double>(type: "double precision", nullable: false),
                    UsedStorage = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Drives = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId",
                table: "Devices",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
