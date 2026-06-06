using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResolvedAgeCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResolvedAgeCache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MinAgeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxAgeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolvedAgeCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResolvedAgeCache_Source_SourceUrl",
                table: "ResolvedAgeCache",
                columns: new[] { "Source", "SourceUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResolvedAgeCache");
        }
    }
}
