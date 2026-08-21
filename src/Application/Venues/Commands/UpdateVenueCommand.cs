using EventWOS.Application.Interfaces;
using EventWOS.Application.Venues.DTOs;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Venues.Commands;

public sealed record UpdateVenueCommand(
    Guid    Id,
    string  Name,
    string  AddressLine1,
    string? AddressLine2,
    string  City,
    string? State,
    string? PostalCode,
    string? Country,
    double? Latitude,
    double? Longitude,
    string? Notes,
    Guid    ActingUserId
) : IRequest<Result<VenueDto>>;

public sealed class UpdateVenueHandler : IRequestHandler<UpdateVenueCommand, Result<VenueDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public UpdateVenueHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<VenueDto>> Handle(UpdateVenueCommand req, CancellationToken ct)
    {
        var entity = await _db.Venues.FirstOrDefaultAsync(v => v.Id == req.Id, ct);
        if (entity is null)
            return Result.Failure<VenueDto>(Error.NotFound);

        var name = req.Name?.Trim() ?? "";
        var dup = await _db.Venues
            .Where(v => !v.IsDeleted && v.Id != req.Id && v.Name.ToLower() == name.ToLower())
            .AnyAsync(ct);
        if (dup)
            return Result.Failure<VenueDto>(new Error(
                "Venue.Duplicate", $"A venue named \"{name}\" already exists."));

        try
        {
            entity.Update(
                req.Name, req.AddressLine1, req.AddressLine2, req.City, req.State,
                req.PostalCode, req.Country, req.Latitude, req.Longitude, req.Notes);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<VenueDto>(new Error("Venue.Archived", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<VenueDto>(new Error("Venue.Invalid", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success(CreateVenueHandler.ToDto(entity));
    }
}
