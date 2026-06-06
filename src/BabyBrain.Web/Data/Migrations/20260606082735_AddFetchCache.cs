using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabyBrain.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFetchCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResolvedAgeCache");

            migrationBuilder.CreateTable(
                name: "FetchCache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RenderJs = table.Column<bool>(type: "INTEGER", nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    Backend = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FetchCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FetchCache_Source",
                table: "FetchCache",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_FetchCache_Url_RenderJs",
                table: "FetchCache",
                columns: new[] { "Url", "RenderJs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FetchCache");

            migrationBuilder.CreateTable(
                name: "ResolvedAgeCache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CheckedAt = table.Column<string>(type: "TEXT", nullable: false),
                    MaxAgeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    MinAgeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
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
    }
}
