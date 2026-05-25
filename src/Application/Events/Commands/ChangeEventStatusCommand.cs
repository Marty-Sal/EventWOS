using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

public sealed record ChangeEventStatusCommand(Guid EventId, string Action, string? Reason = null)
    : IRequest<Result>;

public sealed class ChangeEventStatusHandler : IRequestHandler<ChangeEventStatusCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public ChangeEventStatusHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(ChangeEventStatusCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure(new Error("Event.NotFound", "Event not found."));

        try
        {
            switch (req.Action.ToLower())
            {
                case "publish":   ev.Publish();             break;
                case "start":     ev.Start();               break;
                case "complete":  ev.Complete();            break;
                case "cancel":    ev.Cancel(req.Reason);    break;
                default: return Result.Failure(new Error("Event.InvalidAction", $"Unknown action: {req.Action}"));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Event.InvalidTransition", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
