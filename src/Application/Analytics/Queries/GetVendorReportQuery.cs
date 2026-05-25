using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Analytics.Queries;

public sealed record VendorReportDto(
    Guid    VendorId,
    string  VendorName,
    // Crew stats
    int     TotalCrewInRoster,
    int     TotalAssignmentsMade,
    int     AssignmentsConfirmed,
    int     AssignmentsAttended,
    int     AssignmentsPending,
    int     AssignmentsRejected,
    decimal ConfirmationRate,
    decimal AttendanceRate,
    // Payment stats
    decimal TotalAgreedAmount,
    decimal TotalPaidAmount,
    decimal TotalPendingAmount,
    // Event stats
    int     TotalEventsWorked,
    // Recent crew
    IReadOnlyList<VendorCrewStatDto> TopCrew
);

public sealed record VendorCrewStatDto(
    Guid    CrewId,
    string  CrewName,
    string  CrewMobile,
    decimal DisciplineScore,
    int     EventsAttended,
    int     AssignmentsForThisVendor,
    string  LastStatus
);

public sealed record GetVendorReportQuery(Guid VendorUserId)
    : IRequest<Result<VendorReportDto>>;

public sealed class GetVendorReportHandler
    : IRequestHandler<GetVendorReportQuery, Result<VendorReportDto>>
{
    private readonly IAppDbContext _db;
    public GetVendorReportHandler(IAppDbContext db) => _db = db;

    public async Task<Result<VendorReportDto>> Handle(GetVendorReportQuery req, CancellationToken ct)
    {
        var vendor = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == req.VendorUserId && u.Role == UserRole.Vendor, ct);

        if (vendor is null)
            return Result.Failure<VendorReportDto>(new Error("Vendor.NotFound", "Vendor not found."));

        // All assignments for this vendor
        var assignments = await _db.EventAssignments
            .Where(a => a.VendorId == req.VendorUserId && !a.IsDeleted)
            .ToListAsync(ct);

        var total       = assignments.Count;
        var confirmed   = assignments.Count(a => a.Status is AssignmentStatus.ManagerApproved
                                                           or AssignmentStatus.Confirmed);
        var attended    = assignments.Count(a => a.Status == AssignmentStatus.Attended);
        var pending     = assignments.Count(a => a.Status is AssignmentStatus.Invited
                                                           or AssignmentStatus.VendorApproved
                                                           or AssignmentStatus.PendingManagerApproval);
        var rejected    = assignments.Count(a => a.Status is AssignmentStatus.RejectedByVendor
                                                           or AssignmentStatus.RejectedByManager
                                                           or AssignmentStatus.Declined);

        var confirmRate  = total > 0 ? Math.Round((decimal)(confirmed + attended) / total * 100, 1) : 0m;
        var attendRate   = (confirmed + attended) > 0
            ? Math.Round((decimal)attended / (confirmed + attended) * 100, 1) : 0m;

        var eventsWorked = assignments
            .Where(a => a.Status == AssignmentStatus.Attended)
            .Select(a => a.EventId)
            .Distinct()
            .Count();

        // Payment stats
        var payments = await _db.CrewPayments
            .Where(p => p.VendorId == req.VendorUserId && !p.IsDeleted)
            .ToListAsync(ct);

        var totalAgreed  = payments.Sum(p => p.AgreedAmount);
        var totalPaid    = payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.PaidAmount ?? 0);
        var totalPending = payments.Where(p => p.Status == PaymentStatus.Pending).Sum(p => p.AgreedAmount);

        // Crew roster
        var crewIds = await _db.VendorCrewMappings
            .Where(m => m.VendorId == req.VendorUserId && m.IsActive)
            .Select(m => m.CrewId)
            .ToListAsync(ct);

        var topCrew = await _db.Users
            .Where(u => crewIds.Contains(u.Id))
            .OrderByDescending(u => u.DisciplineScore)
            .Take(10)
            .ToListAsync(ct);

        var crewStats = topCrew.Select(c =>
        {
            var crewAssignments = assignments.Where(a => a.CrewId == c.Id).ToList();
            var last = crewAssignments.OrderByDescending(a => a.CreatedAt).FirstOrDefault();
            return new VendorCrewStatDto(
                c.Id, c.FullName, c.Mobile,
                c.DisciplineScore, c.EventsAttended,
                crewAssignments.Count,
                last?.Status.ToString() ?? "—");
        }).ToList();

        return Result.Success(new VendorReportDto(
            vendor.Id, vendor.FullName,
            crewIds.Count,
            total, confirmed, attended, pending, rejected,
            confirmRate, attendRate,
            totalAgreed, totalPaid, totalPending,
            eventsWorked,
            crewStats));
    }
}
