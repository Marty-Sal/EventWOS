using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Commands;

public sealed record CreateCrewGroupCommand(
    Guid    VendorId,
    string  Name,
    string? Description,
    Guid    ActingUserId
) : IRequest<Result<CrewGroupDto>>;

public sealed class CreateCrewGroupHandler
    : IRequestHandler<CreateCrewGroupCommand, Result<CrewGroupDto>>
{
    private readonly IAppDbContext _db;
    public CreateCrewGroupHandler(IAppDbContext db) => _db = db;

    public async Task<Result<CrewGroupDto>> Handle(CreateCrewGroupCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.NameRequired", "Group name is required."));
        if (req.Name.Trim().Length > 120)
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.NameTooLong", "Group name must be 120 characters or fewer."));

        var name = req.Name.Trim();
        var dup  = await _db.CrewGroups.AnyAsync(
            g => g.VendorId == req.VendorId && g.Name.ToLower() == name.ToLower(), ct);
        if (dup)
            return Result.Failure<CrewGroupDto>(new Error("CrewGroup.DuplicateName", "You already have a group with that name."));

        var group = new CrewGroup(req.VendorId, name, req.Description, req.ActingUserId);
        _db.CrewGroups.Add(group);
        await _db.SaveChangesAsync(ct);

        return Result.Success(new CrewGroupDto(
            group.Id, group.VendorId, group.Name, group.Description, 0, group.CreatedAt));
    }
}
