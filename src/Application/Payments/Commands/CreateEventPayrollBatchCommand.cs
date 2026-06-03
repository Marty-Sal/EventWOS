using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Commands;

/// <summary>
/// Event-centric batch builder. Admin/Manager picks an event, sees every
/// payable party (every vendor with attended crew + every direct-assigned
/// crew member who attended), types per-party amounts, and submits.
///
/// We create ONE PayrollBatch per non-zero line (one per vendor, plus one
/// "direct crew" batch if any direct lines were filled). Each batch holds the
/// CrewPayment rows for that party. This keeps vendor-level disbursement
/// clean — every vendor sees only their own batch.
/// </summary>
public sealed record CreateEventPayrollBatchCommand(
    Guid    EventId,
    IReadOnlyList<EventPayrollBatchLine> Lines,
    string? Notes
) : IRequest<Result<EventPayrollBatchResult>>;

public sealed record EventPayrollBatchLine(
    string  Kind,        // "Vendor" | "DirectCrew"
    Guid    PartyId,     // VendorId or CrewId
    decimal Amount
);

public sealed record EventPayrollBatchResult(
    int             BatchesCreated,
    int             PaymentsCreated,
    decimal         TotalAmount,
    IReadOnlyList<Guid> BatchIds
);

public sealed class CreateEventPayrollBatchValidator
    : AbstractValidator<CreateEventPayrollBatchCommand>
{
    public CreateEventPayrollBatchValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty()
            .Must(l => l.Any(x => x.Amount > 0))
            .WithMessage("Enter an amount for at least one line.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Kind).Must(k => k == "Vendor" || k == "DirectCrew");
            line.RuleFor(l => l.PartyId).NotEmpty();
            line.RuleFor(l => l.Amount).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed class CreateEventPayrollBatchHandler
    : IRequestHandler<CreateEventPayrollBatchCommand, Result<EventPayrollBatchResult>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public CreateEventPayrollBatchHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<EventPayrollBatchResult>> Handle(
        CreateEventPayrollBatchCommand cmd, CancellationToken ct)
    {
        // ── 1. Event must be Completed (payment lifecycle gate)
        var ev = await _db.Events.FindAsync([cmd.EventId], ct);
        if (ev is null)
            return Result.Failure<EventPayrollBatchResult>(
                Error.Custom("Event.NotFound", "Event not found."));
        if (ev.Status != EventStatus.Completed)
            return Result.Failure<EventPayrollBatchResult>(
                Error.Custom("Payroll.EventNotCompleted",
                    "Payments can only be created for Completed events."));

        // ── 2. Filter to non-zero lines
        var lines = cmd.Lines.Where(l => l.Amount > 0).ToList();
        if (lines.Count == 0)
            return Result.Failure<EventPayrollBatchResult>(
                Error.Custom("Payroll.NoAmounts",
                    "Enter an amount for at least one row before creating the batch."));

        // ── 3. Pull all attended assignments for this event (the universe of payable rows)
        var attendedAssignmentIds = await _db.AttendanceRecords
            .Where(r => r.EventId == cmd.EventId && r.Action == AttendanceAction.CheckIn)
            .Select(r => r.AssignmentId)
            .Distinct()
            .ToListAsync(ct);

        if (attendedAssignmentIds.Count == 0)
            return Result.Failure<EventPayrollBatchResult>(
                Error.Custom("Payroll.NoAttendance",
                    "No crew checked in for this event yet — nothing to pay."));

        var rejected = new[]
        {
            AssignmentStatus.Declined,
            AssignmentStatus.RejectedByVendor,
            AssignmentStatus.RejectedByManager,
            AssignmentStatus.NoShow
        };

        var attendedAssignments = await _db.EventAssignments
            .Where(a => a.EventId == cmd.EventId
                     && a.CrewId  != null
                     && !rejected.Contains(a.Status)
                     && attendedAssignmentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.VendorId, CrewId = a.CrewId!.Value })
            .ToListAsync(ct);

        // Skip anyone that already has a payment row.
        var alreadyPaidAssignments = await _db.CrewPayments
            .Where(p => attendedAssignmentIds.Contains(p.AssignmentId))
            .Select(p => p.AssignmentId)
            .ToListAsync(ct);
        var paidSet = alreadyPaidAssignments.ToHashSet();

        var allBatchIds   = new List<Guid>();
        var allCreatedPmt = new List<CrewPayment>();

        // ── 4. Process each non-zero line, creating one batch + N payments
        foreach (var line in lines)
        {
            // Identify the assignments that belong to this line
            var lineAssignments = line.Kind switch
            {
                "Vendor"     => attendedAssignments
                                  .Where(a => a.VendorId == line.PartyId && !paidSet.Contains(a.Id))
                                  .ToList(),
                "DirectCrew" => attendedAssignments
                                  .Where(a => a.VendorId == null
                                           && a.CrewId   == line.PartyId
                                           && !paidSet.Contains(a.Id))
                                  .ToList(),
                _ => new()
            };

            if (lineAssignments.Count == 0)
                continue;   // nothing left to pay for this line — silently skip

            // Per-crew amount = line amount split evenly across the attended crew of this line.
            // For direct crew there's always exactly one assignment, so amount == line.Amount.
            // For vendor lines the manager types the vendor-level total; each crew gets an
            // even share. Vendor can still nudge individual amounts later via per-payment edit.
            var perCrew = Math.Round(line.Amount / lineAssignments.Count, 2);

            // Create the batch
            var batchRef = $"PAY-{cmd.EventId.ToString()[..6].ToUpper()}-{(line.Kind == "Vendor" ? "V" : "C")}-{line.PartyId.ToString()[..6].ToUpper()}-{DateTime.UtcNow:HHmmss}";
            var batch = new PayrollBatch(
                vendorId: line.Kind == "Vendor" ? line.PartyId : (Guid?)null,
                eventId:  cmd.EventId,
                batchRef: batchRef,
                notes:    cmd.Notes);
            await _db.PayrollBatches.AddAsync(batch, ct);
            await _uow.SaveChangesAsync(ct);

            // Create one payment per assignment, attach to the batch, approve immediately.
            foreach (var a in lineAssignments)
            {
                var pmt = new CrewPayment(
                    eventId:      cmd.EventId,
                    assignmentId: a.Id,
                    crewId:       a.CrewId,
                    vendorId:     a.VendorId,            // may be null for direct crew
                    agreedAmount: perCrew,
                    notes:        cmd.Notes);
                pmt.Approve();
                pmt.AttachToPayroll(batch.Id);
                await _db.CrewPayments.AddAsync(pmt, ct);
                allCreatedPmt.Add(pmt);
            }

            batch.SetTotal(perCrew * lineAssignments.Count);
            allBatchIds.Add(batch.Id);
        }

        if (allBatchIds.Count == 0)
            return Result.Failure<EventPayrollBatchResult>(
                Error.Custom("Payroll.AllAlreadyPaid",
                    "Every selected line already has an existing payment for this event."));

        await _uow.SaveChangesAsync(ct);

        // ── 5. Fan out live updates so payment screens refresh in real time
        foreach (var pmt in allCreatedPmt)
        {
            var payload = new
            {
                paymentId = pmt.Id,
                crewId    = pmt.CrewId,
                vendorId  = pmt.VendorId,
                status    = pmt.Status.ToString(),
                action    = "created"
            };
            await _push.PushToUserAsync(pmt.CrewId, "PaymentCreated", payload, ct);
            if (pmt.VendorId is { } vid)
                await _push.PushToUserAsync(vid, "PaymentCreated", payload, ct);
        }
        var batchPayload = new { eventId = cmd.EventId, action = "created", count = allBatchIds.Count };
        await _push.PushToRoleAsync("Admin",   "PayrollUpdated", batchPayload, ct);
        await _push.PushToRoleAsync("Manager", "PayrollUpdated", batchPayload, ct);

        return Result.Success(new EventPayrollBatchResult(
            BatchesCreated:  allBatchIds.Count,
            PaymentsCreated: allCreatedPmt.Count,
            TotalAmount:     allCreatedPmt.Sum(p => p.AgreedAmount),
            BatchIds:        allBatchIds));
    }
}
