using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobFireMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_fire_marks",
                columns: table => new
                {
                    job = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_fire_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_fire_marks", x => x.job);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_fire_marks");
        }
    }
}
