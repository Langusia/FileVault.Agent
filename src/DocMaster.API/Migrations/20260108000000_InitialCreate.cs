using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocMaster.API.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GrpcAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Datacenter = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Tier = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Hot"),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Unknown"),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    CpuUsagePercent = table.Column<double>(type: "double precision", nullable: true),
                    WorkingSetBytes = table.Column<long>(type: "bigint", nullable: true),
                    UptimeSeconds = table.Column<long>(type: "bigint", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.NodeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Status",
                table: "Nodes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Status_Tier",
                table: "Nodes",
                columns: new[] { "Status", "Tier" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_UpdatedUtc",
                table: "Nodes",
                column: "UpdatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}
