using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleOS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Businesses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    LogoPath = table.Column<string>(type: "TEXT", nullable: true),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CurrencyDecimals = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    IsDemoWorkspace = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Businesses", x => x.Id);
                    table.CheckConstraint("CK_Business_Decimals", "CurrencyDecimals BETWEEN 0 AND 3");
                });

            migrationBuilder.CreateTable(
                name: "BusinessHolidays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessHolidays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    IsOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    OpenTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    CloseTime = table.Column<TimeOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessHours", x => x.Id);
                    table.CheckConstraint("CK_BusinessHours_Range", "IsOpen = 0 OR CloseTime > OpenTime");
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequences",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NextValue = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequences", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ServiceCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: true),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    AvailableFrom = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    AvailableUntil = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.CheckConstraint("CK_Resource_Capacity", "Capacity >= 1");
                    table.ForeignKey(
                        name: "FK_Resources_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Staff",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    Position = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SkillsText = table.Column<string>(type: "TEXT", nullable: true),
                    EmploymentStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Staff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Staff_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Services",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    PrepMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CleanupMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DepositMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImagePath = table.Column<string>(type: "TEXT", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                    table.CheckConstraint("CK_Service_Buffers", "PrepMinutes >= 0 AND CleanupMinutes >= 0");
                    table.CheckConstraint("CK_Service_Duration", "DurationMinutes > 0");
                    table.CheckConstraint("CK_Service_Price", "PriceMinor >= 0 AND DepositMinor >= 0 AND DepositMinor <= PriceMinor");
                    table.ForeignKey(
                        name: "FK_Services_ServiceCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ServiceCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ResourceMaintenance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ResourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<DateTime>(type: "TEXT", nullable: false),
                    End = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceMaintenance", x => x.Id);
                    table.CheckConstraint("CK_Maintenance_Range", "\"End\" > \"Start\"");
                    table.ForeignKey(
                        name: "FK_ResourceMaintenance_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedPhone = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", nullable: true),
                    Birthday = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredStaffId = table.Column<int>(type: "INTEGER", nullable: true),
                    AllowEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowSms = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAnonymized = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Staff_PreferredStaffId",
                        column: x => x.PreferredStaffId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StaffShifts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StaffMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    BreakStart = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    BreakEnd = table.Column<TimeOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffShifts", x => x.Id);
                    table.CheckConstraint("CK_Shift_Range", "EndTime > StartTime");
                    table.ForeignKey(
                        name: "FK_StaffShifts_Staff_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffTimeOff",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StaffMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<DateTime>(type: "TEXT", nullable: false),
                    End = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffTimeOff", x => x.Id);
                    table.CheckConstraint("CK_TimeOff_Range", "\"End\" > \"Start\"");
                    table.ForeignKey(
                        name: "FK_StaffTimeOff_Staff_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordSalt = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    StaffMemberId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_User_Role", "Role BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_Users_Staff_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ServiceResourceRequirements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceType = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceResourceRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceResourceRequirements_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StaffMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffSkills_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StaffSkills_Staff_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SubmissionKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: true),
                    Start = table.Column<DateTime>(type: "TEXT", nullable: false),
                    End = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerNotes = table.Column<string>(type: "TEXT", nullable: true),
                    InternalNotes = table.Column<string>(type: "TEXT", nullable: true),
                    CancellationReason = table.Column<string>(type: "TEXT", nullable: true),
                    SubtotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxRateBasisPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DepositRequiredMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    RemindByEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemindBySms = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReminderLeadHours = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByName = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.CheckConstraint("CK_Appointment_Money", "SubtotalMinor >= 0 AND DiscountMinor >= 0 AND TaxMinor >= 0 AND TotalMinor >= 0 AND DepositRequiredMinor >= 0");
                    table.CheckConstraint("CK_Appointment_Range", "\"End\" >= \"Start\"");
                    table.CheckConstraint("CK_Appointment_Status", "Status BETWEEN 0 AND 7");
                    table.ForeignKey(
                        name: "FK_Appointments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CustomerTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerTags_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    FromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ToStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentEvents_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    PrepMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CleanupMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitPriceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DepositMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    StaffMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Start = table.Column<DateTime>(type: "TEXT", nullable: false),
                    End = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BlockStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BlockEnd = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsBlocking = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentSegments", x => x.Id);
                    table.CheckConstraint("CK_Segment_Duration", "DurationMinutes > 0 AND PrepMinutes >= 0 AND CleanupMinutes >= 0");
                    table.CheckConstraint("CK_Segment_Money", "UnitPriceMinor >= 0 AND DiscountMinor >= 0 AND DiscountMinor <= UnitPriceMinor AND DepositMinor >= 0");
                    table.CheckConstraint("CK_Segment_Range", "\"End\" > \"Start\" AND BlockStart <= \"Start\" AND BlockEnd >= \"End\"");
                    table.ForeignKey(
                        name: "FK_AppointmentSegments_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppointmentSegments_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentSegments_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentSegments_Staff_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerEmail = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerPhone = table.Column<string>(type: "TEXT", nullable: true),
                    SubtotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxRateBasisPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerNotes = table.Column<string>(type: "TEXT", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.CheckConstraint("CK_Invoice_Money", "TotalMinor >= 0 AND SubtotalMinor >= 0");
                    table.ForeignKey(
                        name: "FK_Invoices_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDismissed = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReminderId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReceiptNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SubmissionKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ReversesPaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RecordedByName = table.Column<string>(type: "TEXT", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payment_Amount", "AmountMinor > 0");
                    table.CheckConstraint("CK_Payment_Kind", "Kind BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_Payment_Reversal", "(Kind IN (2, 3)) = (ReversesPaymentId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Payments_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Payments_ReversesPaymentId",
                        column: x => x.ReversesPaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", nullable: true),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reminders_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    StaffName = table.Column<string>(type: "TEXT", nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitPriceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    LineTotalMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentEvents_AppointmentId_Timestamp",
                table: "AppointmentEvents",
                columns: new[] { "AppointmentId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_CustomerId_Start",
                table: "Appointments",
                columns: new[] { "CustomerId", "Start" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_LocationId",
                table: "Appointments",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Reference",
                table: "Appointments",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Start",
                table: "Appointments",
                column: "Start");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Status",
                table: "Appointments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_SubmissionKey",
                table: "Appointments",
                column: "SubmissionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSegments_AppointmentId",
                table: "AppointmentSegments",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSegments_ResourceId_BlockStart_BlockEnd",
                table: "AppointmentSegments",
                columns: new[] { "ResourceId", "BlockStart", "BlockEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSegments_ServiceId",
                table: "AppointmentSegments",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSegments_StaffMemberId_BlockStart_BlockEnd",
                table: "AppointmentSegments",
                columns: new[] { "StaffMemberId", "BlockStart", "BlockEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSegments_Start",
                table: "AppointmentSegments",
                column: "Start");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CustomerId",
                table: "AuditLogs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessHolidays_Date",
                table: "BusinessHolidays",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessHours_DayOfWeek",
                table: "BusinessHours",
                column: "DayOfWeek",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_FullName",
                table: "Customers",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NormalizedEmail",
                table: "Customers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NormalizedPhone",
                table: "Customers",
                column: "NormalizedPhone");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PreferredStaffId",
                table: "Customers",
                column: "PreferredStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTags_CustomerId_Tag",
                table: "CustomerTags",
                columns: new[] { "CustomerId", "Tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId",
                table: "InvoiceLines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AppointmentId",
                table: "Invoices",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                table: "Invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_AppointmentId",
                table: "Notifications",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AppointmentId",
                table: "Payments",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CustomerId",
                table: "Payments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ReceiptNumber",
                table: "Payments",
                column: "ReceiptNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RecordedAt",
                table: "Payments",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ReversesPaymentId",
                table: "Payments",
                column: "ReversesPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SubmissionKey",
                table: "Payments",
                column: "SubmissionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_AppointmentId",
                table: "Reminders",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_DedupKey",
                table: "Reminders",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_Status_DueAt",
                table: "Reminders",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceMaintenance_ResourceId_Start_End",
                table: "ResourceMaintenance",
                columns: new[] { "ResourceId", "Start", "End" });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_LocationId",
                table: "Resources",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceResourceRequirements_ServiceId_ResourceType",
                table: "ServiceResourceRequirements",
                columns: new[] { "ServiceId", "ResourceType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Services_CategoryId",
                table: "Services",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Staff_LocationId",
                table: "Staff",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffShifts_StaffMemberId",
                table: "StaffShifts",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffSkills_ServiceId",
                table: "StaffSkills",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffSkills_StaffMemberId_ServiceId",
                table: "StaffSkills",
                columns: new[] { "StaffMemberId", "ServiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffTimeOff_StaffMemberId_Start_End",
                table: "StaffTimeOff",
                columns: new[] { "StaffMemberId", "Start", "End" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_StaffMemberId",
                table: "Users",
                column: "StaffMemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentEvents");

            migrationBuilder.DropTable(
                name: "AppointmentSegments");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Businesses");

            migrationBuilder.DropTable(
                name: "BusinessHolidays");

            migrationBuilder.DropTable(
                name: "BusinessHours");

            migrationBuilder.DropTable(
                name: "CustomerTags");

            migrationBuilder.DropTable(
                name: "InvoiceLines");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "NumberSequences");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "Reminders");

            migrationBuilder.DropTable(
                name: "ResourceMaintenance");

            migrationBuilder.DropTable(
                name: "ServiceResourceRequirements");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "StaffShifts");

            migrationBuilder.DropTable(
                name: "StaffSkills");

            migrationBuilder.DropTable(
                name: "StaffTimeOff");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "Services");

            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.DropTable(
                name: "ServiceCategories");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Staff");

            migrationBuilder.DropTable(
                name: "Locations");
        }
    }
}
