using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountsAndAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "installation_id",
                table: "repositories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "investigations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "github_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    account_login = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_installations", x => x.id);
                    table.CheckConstraint("ck_github_installations_status", "status IN ('active', 'suspended')");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_user_id = table.Column<long>(type: "bigint", nullable: false),
                    login = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repository_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repository_access", x => x.id);
                    table.ForeignKey(
                        name: "fk_repository_access_repositories_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_repository_access_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_repositories_installation_id",
                table: "repositories",
                column: "installation_id");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_user_id",
                table: "investigations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_installations_installation_id",
                table: "github_installations",
                column: "installation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_repository_access_repository_id",
                table: "repository_access",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_repository_access_user_id_repository_id",
                table: "repository_access",
                columns: new[] { "user_id", "repository_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_github_user_id",
                table: "users",
                column: "github_user_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_investigations_users_user_id",
                table: "investigations",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_repositories_github_installations_installation_id",
                table: "repositories",
                column: "installation_id",
                principalTable: "github_installations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_investigations_users_user_id",
                table: "investigations");

            migrationBuilder.DropForeignKey(
                name: "fk_repositories_github_installations_installation_id",
                table: "repositories");

            migrationBuilder.DropTable(
                name: "github_installations");

            migrationBuilder.DropTable(
                name: "repository_access");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropIndex(
                name: "ix_repositories_installation_id",
                table: "repositories");

            migrationBuilder.DropIndex(
                name: "ix_investigations_user_id",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "repositories");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "investigations");
        }
    }
}
