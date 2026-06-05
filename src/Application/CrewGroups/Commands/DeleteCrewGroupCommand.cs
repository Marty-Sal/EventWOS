using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Commands;

/// <summary>Soft-deletes a group + its member rows. Crew roster is untouched.</summary>
public sealed record DeleteCrewGroupCommand(
    Guid GroupId,
    Guid ActingVendorId
) : IRequest<Result>;

public sealed class DeleteCrewGroupHandler : IRequestHandler<DeleteCrewGroupCommand, Result>
{
    private readonly IAppDbContext _db;
    public DeleteCrewGroupHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(DeleteCrewGroupCommand req, CancellationToken ct)
    {
        var grp = await _db.CrewGroups.FirstOrDefaultAsync(g => g.Id == req.GroupId, ct);
        if (grp is null) return Result.Failure(new Error("CrewGroup.NotFound", "Group not found."));
        if (grp.VendorId != req.ActingVendorId)
            return Result.Failure(new Error("CrewGroup.Forbidden", "That group does not belong to you."));

        var members = await _db.CrewGroupMembers
            .Where(m => m.CrewGroupId == grp.Id).ToListAsync(ct);
        foreach (var m in members) m.SoftDelete(req.ActingVendorId);
        grp.SoftDelete(req.ActingVendorId);

        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
