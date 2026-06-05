using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Commands;

/// <summary>
/// Replace the group's member list with the provided crew ids.
/// Only crew belonging to the acting vendor's roster are accepted.
/// Members not in the new list are soft-deleted; new members are added.
/// </summary>
public sealed record SetCrewGroupMembersCommand(
    Guid              GroupId,
    IReadOnlyList<Guid> CrewIds,
    Guid              ActingVendorId
) : IRequest<Result<CrewGroupDto>>;

public sealed class SetCrewGroupMembersHandler
    : IRequestHandler<SetCrewGroupMembersCommand, Result<CrewGroupDto>>
{
    private readonly IAppDbContext _db;
    public SetCrewGroupMembersHandler(IAppDbContext db) => _db = db;

    public async Task<Result<CrewGroupDto>> Handle(SetCrewGroupMembersCommand req, CancellationToken ct)
    {
        var grp = await _db.CrewGroups.FirstOrDefaultAsync(g => g.Id == req.GroupId, ct);
        if (grp is null)
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.NotFound", "Group not found."));
        if (grp.VendorId != req.ActingVendorId)
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.Forbidden", "That group does not belong to you."));

        var desired = req.CrewIds?.Distinct().ToHashSet() ?? new HashSet<Guid>();

        // Validate every desired crew belongs to this vendor's roster.
        if (desired.Count > 0)
        {
            var validRosterIds = await _db.Users
                .Where(u => u.Role == UserRole.Crew && !u.IsDeleted && u.VendorId == req.ActingVendorId)
                .Where(u => desired.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);

            if (validRosterIds.Count != desired.Count)
            {
                var bad = desired.Except(validRosterIds).ToList();
                return Result.Failure<CrewGroupDto>(new Error(
                    "CrewGroup.CrewNotInRoster",
                    $"{bad.Count} crew member(s) are not in your roster."));
            }
        }

        var existing = await _db.CrewGroupMembers
            .Where(m => m.CrewGroupId == grp.Id).ToListAsync(ct);
        var existingIds = existing.Select(m => m.CrewId).ToHashSet();

        // Soft-delete rows no longer desired.
        foreach (var m in existing.Where(m => !desired.Contains(m.CrewId)))
            m.SoftDelete(req.ActingVendorId);

        // Add new rows for crew not already in the grp.
        foreach (var crewId in desired.Where(id => !existingIds.Contains(id)))
            _db.CrewGroupMembers.Add(new CrewGroupMember(grp.Id, crewId, req.ActingVendorId));

        await _db.SaveChangesAsync(ct);

        return Result.Success(new CrewGroupDto(
            grp.Id, grp.VendorId, grp.Name, grp.Description, desired.Count, grp.CreatedAt));
    }
}
