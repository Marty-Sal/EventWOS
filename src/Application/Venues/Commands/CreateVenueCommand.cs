using EventWOS.Application.Interfaces;
using EventWOS.Application.Venues.DTOs;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using DomainVenue = EventWOS.Domain.Entities.Venue;

namespace EventWOS.Application.Venues.Commands;

public sealed record CreateVenueCommand(
    string  Name,
    string  AddressLine1,
    string? AddressLine2,
    string? ShortAddress,
    string  City,
    string? State,
    string? PostalCode,
    string? Country,
    double? Latitude,
    double? Longitude,
    string? Notes,
    Guid    ActingUserId
) : IRequest<Result<VenueDto>>;

public sealed class CreateVenueHandler : IRequestHandler<CreateVenueCommand, Result<VenueDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public CreateVenueHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<VenueDto>> Handle(CreateVenueCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Result.Failure<VenueDto>(new Error("Venue.NameRequired", "Name is required."));

        var name = req.Name.Trim();
        var dup = await _db.Venues
            .Where(v => !v.IsDeleted && v.Name.ToLower() == name.ToLower())
            .AnyAsync(ct);
        if (dup)
            return Result.Failure<VenueDto>(new Error(
                "Venue.Duplicate",
                $"A venue named \"{name}\" already exists. If it's archived, restore it instead of creating a new one."));

        DomainVenue entity;
        try
        {
            entity = new DomainVenue(
                name, req.AddressLine1, req.AddressLine2, req.ShortAddress, req.City,
                req.State, req.PostalCode, req.Country, req.Latitude, req.Longitude,
                req.Notes, req.ActingUserId);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<VenueDto>(new Error("Venue.Invalid", ex.Message));
        }

        _db.Venues.Add(entity);
        await _uow.SaveChangesAsync(ct);

        return Result.Success(ToDto(entity));
    }

    internal static VenueDto ToDto(DomainVenue v) => new(
        v.Id, v.Name, v.AddressLine1, v.AddressLine2, v.ShortAddress, v.City, v.State,
        v.PostalCode, v.Country, v.Latitude, v.Longitude, v.Notes, v.IsDeleted,
        v.CreatedAt, v.UpdatedAt);
}
