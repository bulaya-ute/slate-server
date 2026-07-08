using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slate.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixInviteCreatedByCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invites_users_created_by",
                table: "invites");

            migrationBuilder.AddForeignKey(
                name: "fk_invites_users_created_by",
                table: "invites",
                column: "created_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invites_users_created_by",
                table: "invites");

            migrationBuilder.AddForeignKey(
                name: "fk_invites_users_created_by",
                table: "invites",
                column: "created_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
