using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Commands;

/// <summary>Rename or update description on a grp. Ownership enforced.</summary>
public sealed record UpdateCrewGroupCommand(
    Guid    GroupId,
    string? Name,
    string? Description,
    Guid    ActingVendorId
) : IRequest<Result<CrewGroupDto>>;

public sealed class UpdateCrewGroupHandler
    : IRequestHandler<UpdateCrewGroupCommand, Result<CrewGroupDto>>
{
    private readonly IAppDbContext _db;
    public UpdateCrewGroupHandler(IAppDbContext db) => _db = db;

    public async Task<Result<CrewGroupDto>> Handle(UpdateCrewGroupCommand req, CancellationToken ct)
    {
        var grp = await _db.CrewGroups.FirstOrDefaultAsync(g => g.Id == req.GroupId, ct);
        if (grp is null)
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.NotFound", "Group not found."));
        if (grp.VendorId != req.ActingVendorId)
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.Forbidden", "That group does not belong to you."));

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            var newName = req.Name.Trim();
            if (newName.Length > 120)
                return Result.Failure<CrewGroupDto>(new Error("CrewGroup.NameTooLong", "Group name must be 120 characters or fewer."));

            if (!string.Equals(newName, grp.Name, StringComparison.OrdinalIgnoreCase))
            {
                var clash = await _db.CrewGroups.AnyAsync(
                    g => g.VendorId == grp.VendorId
                      && g.Id != grp.Id
                      && g.Name.ToLower() == newName.ToLower(), ct);
                if (clash)
                    return Result.Failure<CrewGroupDto>(new Error("CrewGroup.DuplicateName", "You already have a group with that name."));
            }

            grp.Rename(newName, req.ActingVendorId);
        }

        if (req.Description is not null)
            grp.SetDescription(req.Description, req.ActingVendorId);

        await _db.SaveChangesAsync(ct);

        var memberCount = await _db.CrewGroupMembers.CountAsync(m => m.CrewGroupId == grp.Id, ct);
        return Result.Success(new CrewGroupDto(
            grp.Id, grp.VendorId, grp.Name, grp.Description, memberCount, grp.CreatedAt));
    }
}
