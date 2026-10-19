using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caligo.Notifications.Host.Migrations
{
    public partial class ActiveSubscriberIndex : Migration
    {
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
