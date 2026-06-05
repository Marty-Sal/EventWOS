using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Manager re-invites a vendor whose invitation was previously rejected.
/// Flips the placeholder row from RejectedByVendor back to Invited and
/// clears rejection state. Notifies the vendor of the new invitation.
/// </summary>
public sealed record ReinviteVendorCommand(
    Guid AssignmentId,
    Guid ReinvitedByUserId
) : IRequest<Result>;

public sealed class ReinviteVendorHandler
    : IRequestHandler<ReinviteVendorCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public ReinviteVendorHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db = db; _uow = uow; _push = push;
    }

    public async Task<Result> Handle(ReinviteVendorCommand req, CancellationToken ct)
    {
        var a = await _db.EventAssignments
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == req.AssignmentId, ct);
        if (a is null)
            return Result.Failure(new Error("Invitation.NotFound", "Invitation not found."));

        try { a.ManagerReinviteVendor(); }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Invitation.InvalidTransition", ex.Message));
        }

        a.UpdatedAt = DateTime.UtcNow;
        a.UpdatedBy = req.ReinvitedByUserId;

        await _uow.SaveChangesAsync(ct);

        if (a.VendorId.HasValue)
        {
            await _push.PushToUserAsync(a.VendorId.Value, "VendorReinvited", new
            {
                assignmentId = a.Id,
                eventId      = a.EventId,
                eventTitle   = a.Event.Title,
                eventStart   = a.Event.StartAt
            }, ct);
        }

        return Result.Success();
    }
}
