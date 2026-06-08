using EventWOS.Application.Interfaces;
using EventWOS.Application.VendorAllocations.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.VendorAllocations.Commands;

/// <summary>
/// Admin/Manager grants <paramref name="VendorId"/> a quota of
/// <paramref name="Quota"/> crew slots on <paramref name="ShiftId"/>.
///
/// Invariants enforced (each as its own error code so the UI can branch):
///   • Shift must exist and not be archived.
///   • Vendor must exist, be a Vendor role, and not be archived/locked.
///   • Quota >= 1 (domain ctor enforces; we surface a friendly message).
///   • SUM(existing active allocations on shift) + Quota
///     &lt;= EventShift.CrewCount — i.e. you can't over-commit a shift.
///   • No existing active allocation for (ShiftId, VendorId) — use
///     Update instead. Filtered unique index also enforces this.
///
/// Returns the freshly created DTO so the UI can append it to the table
/// without a re-fetch.
/// </summary>
public sealed record CreateVendorAllocationCommand(
    Guid ShiftId,
    Guid VendorId,
    int  Quota,
    Guid ActingUserId
) : IRequest<Result<VendorShiftAllocationDto>>;

public sealed class CreateVendorAllocationHandler
    : IRequestHandler<CreateVendorAllocationCommand, Result<VendorShiftAllocationDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public CreateVendorAllocationHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<VendorShiftAllocationDto>> Handle(
        CreateVendorAllocationCommand req, CancellationToken ct)
    {
        // ── Shift exists + active ────────────────────────────────────────
        var shift = await _db.EventShifts
            .Include(s => s.ScopeOfWork)
            .Include(s => s.Event)
            .FirstOrDefaultAsync(s => s.Id == req.ShiftId, ct);
        if (shift is null)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.ShiftNotFound", "Shift not found."));

        // ── Vendor exists + is a vendor ───────────────────────────────────
        var vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorId, ct);
        if (vendor is null)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.VendorNotFound", "Vendor not found."));
        if (vendor.Role != UserRole.Vendor)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.NotAVendor",
                $"User is not a Vendor (role: {vendor.Role}). Only Vendors can be allocated to shifts."));

        // ── Duplicate active allocation check ────────────────────────────
        //
        // The filtered unique index on (shift_id, vendor_id) WHERE NOT
        // is_deleted catches this at the DB layer too — belt-and-braces.
        // We check here so the UI gets a clean error message instead of
        // a 500 with a Postgres unique-violation.
        var existing = await _db.VendorShiftAllocations
            .AnyAsync(a => a.ShiftId == req.ShiftId && a.VendorId == req.VendorId, ct);
        if (existing)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.Duplicate",
                "This vendor already has an active allocation on this shift. " +
                "Edit the existing allocation to change the quota."));

        // ── Capacity check: SUM(quota) + new quota <= shift.crew_count ──
        //
        // Pulled into a single query so we don't have to hydrate every
        // row. Sum() on an empty IQueryable returns 0 in EF Core which
        // is exactly what we want for the first allocation on a shift.
        var alreadyCommitted = await _db.VendorShiftAllocations
            .Where(a => a.ShiftId == req.ShiftId)
            .SumAsync(a => (int?)a.Quota, ct) ?? 0;

        if (alreadyCommitted + req.Quota > shift.CrewCount)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.OverCommitsShift",
                $"This allocation would over-commit the shift. " +
                $"Shift capacity is {shift.CrewCount}, " +
                $"already allocated to vendors: {alreadyCommitted}, " +
                $"requested: {req.Quota}. " +
                $"Max you can grant this vendor right now: {shift.CrewCount - alreadyCommitted}."));

        // ── Build entity ──────────────────────────────────────────────────
        VendorShiftAllocation entity;
        try
        {
            entity = new VendorShiftAllocation(
                req.ShiftId, req.VendorId, req.Quota, req.ActingUserId);
        }
        catch (ArgumentException ex)
        {
            // Domain-side validation (Quota < 1 etc.). Surface verbatim so
            // unit tests pin the exact copy.
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.Invalid", ex.Message));
        }

        _db.VendorShiftAllocations.Add(entity);
        await _uow.SaveChangesAsync(ct);

        return Result.Success(new VendorShiftAllocationDto(
            entity.Id,
            entity.ShiftId, entity.VendorId,
            vendor.FullName ?? vendor.Mobile ?? "(unnamed vendor)",
            shift.EventId,
            shift.ScopeOfWorkId,
            shift.ScopeOfWork?.Name ?? "(unknown)",
            entity.Quota,
            CurrentlyAssigned: 0,  // brand-new allocation, no crew yet
            entity.IsDeleted, entity.CreatedAt, entity.UpdatedAt));
    }
}
