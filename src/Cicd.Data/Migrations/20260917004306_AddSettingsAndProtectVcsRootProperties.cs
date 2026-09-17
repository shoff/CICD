using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cicd.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsAndProtectVcsRootProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE vcs_roots ALTER COLUMN properties TYPE text USING properties::text;");

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    is_secret = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.Sql("ALTER TABLE vcs_roots ALTER COLUMN properties TYPE jsonb USING properties::jsonb;");
        }
    }
}
