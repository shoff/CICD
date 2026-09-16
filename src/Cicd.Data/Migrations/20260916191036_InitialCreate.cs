using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cicd.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    authorized = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    current_build_id = table.Column<Guid>(type: "uuid", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.ForeignKey(
                        name: "fk_projects_projects_parent_id",
                        column: x => x.parent_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "trigger_state",
                columns: table => new
                {
                    build_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_index = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trigger_state", x => new { x.build_configuration_id, x.trigger_index, x.key });
                });

            migrationBuilder.CreateTable(
                name: "vcs_roots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    provider_id = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: false),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vcs_roots", x => x.id);
                    table.ForeignKey(
                        name: "fk_vcs_roots_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "build_configurations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    vcs_root_id = table.Column<Guid>(type: "uuid", nullable: true),
                    build_number_format = table.Column<string>(type: "text", nullable: false),
                    build_counter = table.Column<int>(type: "integer", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    triggers = table.Column<string>(type: "jsonb", nullable: false),
                    agent_requirements = table.Column<string>(type: "jsonb", nullable: false),
                    artifact_paths = table.Column<string>(type: "jsonb", nullable: false),
                    pull_requests = table.Column<string>(type: "jsonb", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_configurations", x => x.id);
                    table.ForeignKey(
                        name: "fk_build_configurations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_build_configurations_vcs_roots_vcs_root_id",
                        column: x => x.vcs_root_id,
                        principalTable: "vcs_roots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pull_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vcs_root_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    source_branch = table.Column<string>(type: "text", nullable: false),
                    target_branch = table.Column<string>(type: "text", nullable: false),
                    head_sha = table.Column<string>(type: "text", nullable: false),
                    checkout_ref = table.Column<string>(type: "text", nullable: true),
                    author = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_built_sha_by_configuration = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pull_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_pull_requests_vcs_roots_vcs_root_id",
                        column: x => x.vcs_root_id,
                        principalTable: "vcs_roots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "builds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    build_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    status_text = table.Column<string>(type: "text", nullable: true),
                    branch = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<string>(type: "text", nullable: true),
                    checkout_ref = table.Column<string>(type: "text", nullable: true),
                    triggered_by = table.Column<string>(type: "text", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pull_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    log_line_count = table.Column<long>(type: "bigint", nullable: false),
                    step_runs = table.Column<string>(type: "jsonb", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_builds", x => x.id);
                    table.ForeignKey(
                        name: "fk_builds_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_builds_build_configurations_build_configuration_id",
                        column: x => x.build_configuration_id,
                        principalTable: "build_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_builds_pull_requests_pull_request_id",
                        column: x => x.pull_request_id,
                        principalTable: "pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "build_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    build_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_build_artifacts_builds_build_id",
                        column: x => x.build_id,
                        principalTable: "builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "build_log_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    build_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_log_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_build_log_lines_builds_build_id",
                        column: x => x.build_id,
                        principalTable: "builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agents_name",
                table: "agents",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_build_artifacts_build_id",
                table: "build_artifacts",
                column: "build_id");

            migrationBuilder.CreateIndex(
                name: "ix_build_configurations_project_id_name",
                table: "build_configurations",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_build_configurations_vcs_root_id",
                table: "build_configurations",
                column: "vcs_root_id");

            migrationBuilder.CreateIndex(
                name: "ix_build_log_lines_build_id_sequence",
                table: "build_log_lines",
                columns: new[] { "build_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_builds_agent_id",
                table: "builds",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_builds_build_configuration_id_queued_at",
                table: "builds",
                columns: new[] { "build_configuration_id", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_builds_pull_request_id",
                table: "builds",
                column: "pull_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_builds_status",
                table: "builds",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                table: "projects",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_parent_id",
                table: "projects",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_pull_requests_vcs_root_id_number",
                table: "pull_requests",
                columns: new[] { "vcs_root_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vcs_roots_project_id",
                table: "vcs_roots",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_artifacts");

            migrationBuilder.DropTable(
                name: "build_log_lines");

            migrationBuilder.DropTable(
                name: "trigger_state");

            migrationBuilder.DropTable(
                name: "builds");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "build_configurations");

            migrationBuilder.DropTable(
                name: "pull_requests");

            migrationBuilder.DropTable(
                name: "vcs_roots");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
