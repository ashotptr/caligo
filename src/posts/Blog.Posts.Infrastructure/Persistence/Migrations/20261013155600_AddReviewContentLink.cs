using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blog.Posts.Infrastructure.Persistence.Migrations
{
    public partial class AddReviewContentLink : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContentId",
                table: "BlogPosts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlogPosts_Content_PublishedDate_Id",
                table: "BlogPosts",
                columns: new[] { "ContentId", "PublishedDate", "Id" },
                filter: "\"Status\" = 0 AND \"ContentId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BlogPosts_Content_PublishedDate_Id",
                table: "BlogPosts");

            migrationBuilder.DropColumn(
                name: "ContentId",
                table: "BlogPosts");
        }
    }
}
