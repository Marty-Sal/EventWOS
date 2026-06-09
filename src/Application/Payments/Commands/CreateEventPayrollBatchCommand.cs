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
/// crew member who attended), types the <b>per-crew rate</b> for each party,
/// and submits.
///
/// Phase D step 23: Amount semantics is now PER-CREW, not per-line. The
/// total billed to the manager for a vendor line is
/// <c>Amount × AttendedCrewCount</c>; for a direct-crew line the count is
/// always 1, so total == amount. Each child <c>CrewPayment.AgreedAmount</c>
/// is also set to the per-crew rate (previously 0 for vendor lines, forcing
/// the vendor to type the amount again before paying).
///
/// We create ONE PayrollBatch per non-zero line (one per vendor, plus one
/// "direct crew" batch if any direct lines were filled). Each batch holds
/// the CrewPayment rows for that party. This keeps vendor-level disbursement
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

        // ── 3. Pull every assignment whose Status == Attended (the payable universe).
        //    Status is the source of truth — set by real CheckIn (MarkAttended)
        //    AND by AdminMarkAttended overrides. The earlier CheckIn-record
        //    filter silently dropped admin-overridden crew.
        var attendedAssignments = await _db.EventAssignments
            .Where(a => a.EventId == cmd.EventId
                     && a.CrewId  != null
                     && a.Status  == AssignmentStatus.Attended)
            .Select(a => new { a.Id, a.VendorId, CrewId = a.CrewId!.Value })
            .ToListAsync(ct);

        if (attendedAssignments.Count == 0)
            return Result.Failure<EventPayrollBatchResult>(
                Error.Custom("Payroll.NoAttendance",
                    "No crew have attended this event yet — nothing to pay."));

        // Skip anyone that already has a payment row.
        var attendedAssignmentIds = attendedAssignments.Select(a => a.Id).ToList();
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

            // Phase D step 23: Amount is now PER-CREW for both line kinds.
            //   • Vendor line  → per-crew × attended-crew = total paid to vendor.
            //                    Each child CrewPayment.AgreedAmount = per-crew rate
            //                    (no more "vendor types amount at payout time").
            //   • Direct crew  → per-crew × 1 = same as before. Single row.
            var isVendorLine = line.Kind == "Vendor";

            var batchRef = $"PAY-{cmd.EventId.ToString()[..6].ToUpper()}-{(isVendorLine ? "V" : "C")}-{line.PartyId.ToString()[..6].ToUpper()}-{DateTime.UtcNow:HHmmss}";
            var batch = new PayrollBatch(
                vendorId: isVendorLine ? line.PartyId : (Guid?)null,
                eventId:  cmd.EventId,
                batchRef: batchRef,
                notes:    cmd.Notes);
            await _db.PayrollBatches.AddAsync(batch, ct);
            await _uow.SaveChangesAsync(ct);

            // Create one CrewPayment per attended crew on this line.
            // Each row carries the SAME per-crew rate as AgreedAmount —
            // vendor (or direct flow) just confirms the pre-filled number
            // at payout time, no manual entry.
            foreach (var a in lineAssignments)
            {
                var pmt = new CrewPayment(
                    eventId:      cmd.EventId,
                    assignmentId: a.Id,
                    crewId:       a.CrewId,
                    vendorId:     a.VendorId,
                    agreedAmount: line.Amount,  // per-crew rate, identical for every row in this line
                    notes:        cmd.Notes);
                pmt.Approve();
                pmt.AttachToPayroll(batch.Id);
                await _db.CrewPayments.AddAsync(pmt, ct);
                allCreatedPmt.Add(pmt);
            }

            // Batch total = per-crew × crew count.
            // (For direct-crew lines, crewCount == 1, so equals line.Amount.)
            var lineTotal = line.Amount * lineAssignments.Count;
            batch.SetTotal(lineTotal);
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
