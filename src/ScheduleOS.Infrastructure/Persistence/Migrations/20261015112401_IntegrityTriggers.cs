using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleOS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Database-level safety nets. SQLite has no exclusion constraints, so triggers reject overlapping
    /// blocking segments for the same staff member (and for capacity-1 resources) even if a bug or a
    /// concurrent writer bypassed the application-level conflict detection. Payments and audit entries are append-only.
    /// </summary>
    public partial class IntegrityTriggers : Migration
    {
        private const string StaffOverlap = """
            EXISTS (SELECT 1 FROM AppointmentSegments s
                    WHERE s.IsBlocking = 1 AND s.StaffMemberId = NEW.StaffMemberId AND s.AppointmentId <> NEW.AppointmentId
                      AND s.BlockStart < NEW.BlockEnd AND s.BlockEnd > NEW.BlockStart)
            """;

        private const string ResourceOverlap = """
            NEW.ResourceId IS NOT NULL
            AND (SELECT Capacity FROM Resources WHERE Id = NEW.ResourceId) <= 1
            AND EXISTS (SELECT 1 FROM AppointmentSegments s
                        WHERE s.IsBlocking = 1 AND s.ResourceId = NEW.ResourceId AND s.AppointmentId <> NEW.AppointmentId
                          AND s.BlockStart < NEW.BlockEnd AND s.BlockEnd > NEW.BlockStart)
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE TRIGGER trg_segment_staff_overlap_insert BEFORE INSERT ON AppointmentSegments
                WHEN NEW.IsBlocking = 1 AND {StaffOverlap}
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_STAFF_OVERLAP'); END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER trg_segment_staff_overlap_update BEFORE UPDATE OF StaffMemberId, BlockStart, BlockEnd, IsBlocking ON AppointmentSegments
                WHEN NEW.IsBlocking = 1 AND {StaffOverlap}
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_STAFF_OVERLAP'); END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER trg_segment_resource_overlap_insert BEFORE INSERT ON AppointmentSegments
                WHEN NEW.IsBlocking = 1 AND {ResourceOverlap}
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_RESOURCE_OVERLAP'); END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER trg_segment_resource_overlap_update BEFORE UPDATE OF ResourceId, BlockStart, BlockEnd, IsBlocking ON AppointmentSegments
                WHEN NEW.IsBlocking = 1 AND {ResourceOverlap}
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_RESOURCE_OVERLAP'); END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_payments_no_update BEFORE UPDATE ON Payments
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_PAYMENT_IMMUTABLE'); END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_payments_no_delete BEFORE DELETE ON Payments WHEN OLD.IsDemo = 0
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_PAYMENT_IMMUTABLE'); END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_audit_no_update BEFORE UPDATE ON AuditLogs
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_AUDIT_IMMUTABLE'); END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_audit_no_delete BEFORE DELETE ON AuditLogs WHEN OLD.IsDemo = 0
                BEGIN SELECT RAISE(ABORT, 'SCHEDULEOS_AUDIT_IMMUTABLE'); END;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var name in new[]
                     {
                         "trg_segment_staff_overlap_insert", "trg_segment_staff_overlap_update", "trg_segment_resource_overlap_insert",
                         "trg_segment_resource_overlap_update", "trg_payments_no_update", "trg_payments_no_delete", "trg_audit_no_update", "trg_audit_no_delete",
                     })
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {name};");
        }
    }
}
