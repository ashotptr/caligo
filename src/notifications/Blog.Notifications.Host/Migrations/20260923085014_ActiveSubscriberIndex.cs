using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blog.Notifications.Host.Migrations
{
    /// <inheritdoc />
    public partial class ActiveSubscriberIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscribers_IsActive",
                table: "subscribers");

            migrationBuilder.CreateIndex(
                name: "IX_subscribers_active_id",
                table: "subscribers",
                column: "Id",
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscribers_active_id",
                table: "subscribers");

            migrationBuilder.CreateIndex(
                name: "IX_subscribers_IsActive",
                table: "subscribers",
                column: "IsActive");
        }
    }
}
