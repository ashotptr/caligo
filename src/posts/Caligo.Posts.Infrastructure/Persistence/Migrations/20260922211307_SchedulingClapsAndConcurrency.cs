using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Caligo.Posts.Infrastructure.Persistence.Migrations
{
    public partial class SchedulingClapsAndConcurrency : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BlogPosts_AuthorId",
                table: "BlogPosts");

            migrationBuilder.DropIndex(
                name: "IX_BlogPosts_PublishedDate_Id",
                table: "BlogPosts");

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledFor",
                table: "BlogPosts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "BlogPosts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "BlogPosts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "clap_deltas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PostId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    FirstClap = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clap_deltas", x => x.Id);
                })
                .Annotation("Npgsql:StorageParameter:autovacuum_vacuum_scale_factor", 0.0)
                .Annotation("Npgsql:StorageParameter:autovacuum_vacuum_threshold", 5000);

            migrationBuilder.CreateTable(
                name: "post_clap_totals",
                columns: table => new
                {
                    PostId = table.Column<int>(type: "integer", nullable: false),
                    Claps = table.Column<long>(type: "bigint", nullable: false),
                    Clappers = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_clap_totals", x => x.PostId);
                    table.ForeignKey(
                        name: "FK_post_clap_totals_BlogPosts_PostId",
                        column: x => x.PostId,
                        principalTable: "BlogPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 70);

            migrationBuilder.CreateTable(
                name: "post_claps",
                columns: table => new
                {
                    PostId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Claps = table.Column<int>(type: "integer", nullable: false),
                    LastApplied = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_claps", x => new { x.PostId, x.UserId });
                    table.ForeignKey(
                        name: "FK_post_claps_BlogPosts_PostId",
                        column: x => x.PostId,
                        principalTable: "BlogPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlogPosts_Author_PublishedDate_Id",
                table: "BlogPosts",
                columns: new[] { "AuthorId", "PublishedDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BlogPosts_Published_PublishedDate_Id",
                table: "BlogPosts",
                columns: new[] { "PublishedDate", "Id" },
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_BlogPosts_Scheduled_Due",
                table: "BlogPosts",
                columns: new[] { "ScheduledFor", "Id" },
                filter: "\"Status\" = 1");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clap_deltas");

            migrationBuilder.DropTable(
                name: "post_clap_totals");

            migrationBuilder.DropTable(
                name: "post_claps");

            migrationBuilder.DropIndex(
                name: "IX_BlogPosts_Author_PublishedDate_Id",
                table: "BlogPosts");

            migrationBuilder.DropIndex(
                name: "IX_BlogPosts_Published_PublishedDate_Id",
                table: "BlogPosts");

            migrationBuilder.DropIndex(
                name: "IX_BlogPosts_Scheduled_Due",
                table: "BlogPosts");

            migrationBuilder.DropColumn(
                name: "ScheduledFor",
                table: "BlogPosts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BlogPosts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "BlogPosts");

            migrationBuilder.CreateIndex(
                name: "IX_BlogPosts_AuthorId",
                table: "BlogPosts",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_BlogPosts_PublishedDate_Id",
                table: "BlogPosts",
                columns: new[] { "PublishedDate", "Id" });
        }
    }
}
