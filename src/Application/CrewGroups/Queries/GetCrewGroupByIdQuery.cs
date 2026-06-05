using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Queries;

/// <summary>Group detail + member list. Caller passes ActingVendorId to enforce ownership.</summary>
public sealed record GetCrewGroupByIdQuery(
    Guid GroupId,
    Guid ActingVendorId
) : IRequest<Result<CrewGroupDetailDto>>;

public sealed class GetCrewGroupByIdHandler
    : IRequestHandler<GetCrewGroupByIdQuery, Result<CrewGroupDetailDto>>
{
    private readonly IAppDbContext _db;
    public GetCrewGroupByIdHandler(IAppDbContext db) => _db = db;

    public async Task<Result<CrewGroupDetailDto>> Handle(GetCrewGroupByIdQuery req, CancellationToken ct)
    {
        var grp = await _db.CrewGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == req.GroupId, ct);
        if (grp is null)
            return Result.Failure<CrewGroupDetailDto>(new Error("CrewGroup.NotFound", "Group not found."));
        if (grp.VendorId != req.ActingVendorId)
            return Result.Failure<CrewGroupDetailDto>(new Error("CrewGroup.Forbidden", "That group does not belong to you."));

        var members = await (
            from m in _db.CrewGroupMembers.AsNoTracking()
            join u in _db.Users.AsNoTracking() on m.CrewId equals u.Id
            where m.CrewGroupId == grp.Id
            orderby u.FullName
            select new CrewGroupMemberDto(
                m.Id, u.Id, u.FullName, u.Mobile,
                u.DisciplineScore, u.EventsAttended, m.AddedAt)
        ).ToListAsync(ct);

        return Result.Success(new CrewGroupDetailDto(
            grp.Id, grp.VendorId, grp.Name, grp.Description, grp.CreatedAt, members));
    }
}
