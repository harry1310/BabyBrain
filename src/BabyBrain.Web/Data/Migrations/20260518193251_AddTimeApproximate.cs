using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeApproximate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TimeApproximate",
                table: "EventOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeApproximate",
                table: "EventOccurrences");
        }
    }
}
