using EventWOS.Application.Events.Commands;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// EVENT_UPDATED and SHIFT_CHANGED were the two dormant scenarios that mattered: both
/// had a seeded template, a policy entry and a deep link, and nothing in the system
/// ever raised them. A venue or time change reached nobody, so the people travelling
/// to the job were the last to know.
///
/// These tests pin the trigger AND the silences -- a notification for every edit
/// would train people to ignore the channel, which is the same failure as sending
/// none.
/// </summary>
public class EventAndShiftChangeWiringTests
{
    // ── EVENT_UPDATED ───────────────────────────────────────────────────────

    [Fact]
    public async Task Moving_an_event_to_a_new_venue_fans_out_before_the_save()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (dispatcher, uow) = Recording(db);

        var result = await new UpdateEventHandler(db, uow, dispatcher).Handle(
            Edit(ev, venue: "Jio Garden", address: "BKC, Mumbai"), default);

        result.IsSuccess.Should().BeTrue();
        dispatcher.FanOuts.Should().HaveCount(1);

        var fan = dispatcher.FanOuts[0];
        fan.TemplateCode.Should().Be(NotificationTemplateCodes.EventUpdated);
        fan.Audience.Should().Be(NotificationAudience.EventCrewAndVendors);
        fan.Data![NotificationTokens.VenueName].Should().Contain("Jio Garden").And.Contain("BKC");

        uow.FanOutsStagedAtSaveTime.Should().Be(1,
            "the fan-out must be staged BEFORE the save, or a rollback announces a change that never happened");
    }

    [Fact]
    public async Task A_time_change_is_legible_in_the_message()
    {
        // The seeded body is "New details: {{EventDate}} at {{VenueName}}", so a
        // time-only move has to be visible inside EventDate or the message reads
        // exactly like the one before it.
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (dispatcher, uow) = Recording(db);

        await new UpdateEventHandler(db, uow, dispatcher).Handle(
            Edit(ev, startAt: ev.StartAt.AddHours(2), endAt: ev.EndAt.AddHours(2)), default);

        dispatcher.FanOuts.Should().HaveCount(1);
        dispatcher.FanOuts[0].Data![NotificationTokens.EventDate].Should().Contain("20:00");
    }

    [Fact]
    public async Task Editing_the_description_alone_tells_nobody()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (dispatcher, uow) = Recording(db);

        var result = await new UpdateEventHandler(db, uow, dispatcher).Handle(
            Edit(ev, description: "Now with a bigger stage"), default);

        result.IsSuccess.Should().BeTrue();
        dispatcher.FanOuts.Should().BeEmpty("nobody travels differently because the blurb changed");
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_the_same_details_twice_is_one_piece_of_news()
    {
        using var db = NewContext();
        var ev = SeedEvent(db);
        var (dispatcher, uow) = Recording(db);
        var handler = new UpdateEventHandler(db, uow, dispatcher);

        await handler.Handle(Edit(ev, venue: "Jio Garden"), default);
        await handler.Handle(Edit(ev, venue: "Jio Garden"), default);

        // The second call is a no-op edit, so it must not even raise a request.
        dispatcher.FanOuts.Should().HaveCount(1);
    }

    // ── SHIFT_CHANGED ───────────────────────────────────────────────────────

    [Fact]
    public async Task Moving_a_shift_tells_the_crew_and_the_vendor_on_it()
    {
        using var db = NewContext();
        var (ev, shift, vendor, crew) = SeedStaffedShift(db);
        var (dispatcher, uow) = Recording(db);

        var result = await new UpdateEventShiftHandler(db, uow, dispatcher).Handle(
            new UpdateEventShiftCommand(shift.Id, shift.ScopeOfWorkId, shift.CrewCount,
                                        shift.StartAt.AddHours(1), shift.EndAt!.Value.AddHours(1)),
            default);

        result.IsSuccess.Should().BeTrue();

        dispatcher.Requests.Should().OnlyContain(r => r.TemplateCode == NotificationTemplateCodes.ShiftChanged);
        dispatcher.Requests.Select(r => r.RecipientUserId)
                  .Should().Contain(new[] { crew.Id, vendor.Id });

        dispatcher.Requests[0].Data![NotificationTokens.ShiftName]
                  .Should().Contain("Box Office").And.Contain("09:00",
                      "the new window is the only detail the seeded body can carry");

        uow.RequestsStagedAtSaveTime.Should().Be(dispatcher.Requests.Count,
            "staged before the save, like every other call site");
    }

    [Fact]
    public async Task Growing_the_seat_count_tells_the_vendor_whose_quota_moved_and_not_the_crew()
    {
        // The reported symptom behind this: a shift grew from 2 seats to 3, the
        // vendor's quota silently followed, and their Assign Crew modal still said
        // "full" because nothing told them anything had changed.
        using var db = NewContext();
        var (ev, shift, vendor, crew) = SeedStaffedShift(db);
        var (dispatcher, uow) = Recording(db);

        var result = await new UpdateEventShiftHandler(db, uow, dispatcher).Handle(
            new UpdateEventShiftCommand(shift.Id, shift.ScopeOfWorkId, shift.CrewCount + 1,
                                        shift.StartAt, shift.EndAt),
            default);

        result.IsSuccess.Should().BeTrue();
        dispatcher.Requests.Select(r => r.RecipientUserId).Should().Equal(vendor.Id);
        dispatcher.Requests.Should().NotContain(r => r.RecipientUserId == crew.Id,
            "a crew member does not care that the shift has one more seat");
    }

    [Fact]
    public async Task Deleting_a_shift_tells_the_vendor_it_was_removed()
    {
        using var db = NewContext();
        var (ev, shift, vendor, _) = SeedStaffedShift(db, withCrew: false, withSecondShift: true);
        var (dispatcher, uow) = Recording(db);

        var result = await new ArchiveEventShiftHandler(db, uow, dispatcher).Handle(
            new ArchiveEventShiftCommand(shift.Id, ev.CreatedByUserId), default);

        result.IsSuccess.Should().BeTrue();

        var sent = dispatcher.Requests.Should().ContainSingle().Subject;
        sent.RecipientUserId.Should().Be(vendor.Id);
        sent.TemplateCode.Should().Be(NotificationTemplateCodes.ShiftChanged);
        sent.Data![NotificationTokens.ShiftName].Should().Contain("removed",
            "a shift that vanishes from My Events with no message is indistinguishable from a bug");
        sent.BusinessEventKey.Should().Be($"shift:{shift.Id}:changed:removed",
            "archiving happens once, so the key needs no timestamp");
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static UpdateEventCommand Edit(
        Event ev, string? venue = null, string? address = null, string? description = null,
        DateTime? startAt = null, DateTime? endAt = null)
        => new(ev.Id, ev.Title, description ?? ev.Description,
               venue ?? ev.Venue, address ?? ev.Address,
               startAt ?? ev.StartAt, endAt ?? ev.EndAt, ev.MaxCrew);

    private static (RecordingDispatcher, SnapshottingUnitOfWork) Recording(AppDbContext db)
    {
        var dispatcher = new RecordingDispatcher();
        return (dispatcher, new SnapshottingUnitOfWork(dispatcher, db));
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"change-wiring-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(), new AnonymousUser());

    private static Event SeedEvent(AppDbContext db)
    {
        var ev = new Event("Sunburn Arena", "Main stage", "NSCI Dome", "Worli, Mumbai",
            new DateTime(2026, 9, 12, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 12, 23, 0, 0, DateTimeKind.Utc),
            Guid.NewGuid());
        ev.Publish();
        db.Events.Add(ev);
        db.SaveChanges();
        return ev;
    }

    private static (Event ev, EventShift shift, User vendor, User crew) SeedStaffedShift(
        AppDbContext db, bool withCrew = true, bool withSecondShift = false)
    {
        var admin = Guid.NewGuid();
        var start = new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);

        var ev = new Event("The MIX", null, "Jio Garden", "BKC, Mumbai",
                           start, start.AddHours(9), admin, maxCrew: 2);
        ev.Publish();
        db.Events.Add(ev);

        var scope = new EventWOS.Domain.Entities.ScopeOfWork("Box Office", null, admin);
        db.ScopesOfWork.Add(scope);

        var shift = new EventShift(ev.Id, scope.Id, 2, start, start.AddHours(3), admin);
        db.EventShifts.Add(shift);

        if (withSecondShift)
            db.EventShifts.Add(new EventShift(ev.Id, scope.Id, 2, start, start.AddHours(3), admin));

        var vendor = new User("1122334455", "Sameer Khan", UserRole.Vendor);
        var crew   = new User("1231231231", "Anant Shivkumar", UserRole.Crew);
        db.Users.AddRange(vendor, crew);
        db.SaveChanges();

        db.VendorShiftAllocations.Add(new VendorShiftAllocation(shift.Id, vendor.Id, 2, admin));

        var anchor = new EventAssignment(ev.Id, shift.Id, crewId: null, vendorId: vendor.Id, admin);
        anchor.VendorAcceptInvite();
        db.EventAssignments.Add(anchor);

        if (withCrew)
        {
            var row = new EventAssignment(ev.Id, shift.Id, crew.Id, vendor.Id, vendor.Id);
            row.CrewAccept();
            row.VendorApprove();
            row.ManagerApprove();
            db.EventAssignments.Add(row);
        }

        db.SaveChanges();
        return (ev, shift, vendor, crew);
    }

    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public List<NotificationRequest> Requests { get; } = new();
        public List<NotificationFanOutRequest> FanOuts { get; } = new();
        public void Enqueue(NotificationRequest request) => Requests.Add(request);
        public void Enqueue(IEnumerable<NotificationRequest> requests) => Requests.AddRange(requests);
        public void EnqueueFanOut(NotificationFanOutRequest request) => FanOuts.Add(request);
    }

    /// <summary>
    /// Saves for real (the shift handlers read back after saving) while recording how
    /// much was staged at save time -- that count is what proves the enqueue happened
    /// inside the transaction rather than after it.
    /// </summary>
    private sealed class SnapshottingUnitOfWork : IUnitOfWork
    {
        private readonly RecordingDispatcher _dispatcher;
        private readonly AppDbContext        _db;

        public SnapshottingUnitOfWork(RecordingDispatcher dispatcher, AppDbContext db)
        {
            _dispatcher = dispatcher; _db = db;
        }

        public int RequestsStagedAtSaveTime { get; private set; }
        public int FanOutsStagedAtSaveTime { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            RequestsStagedAtSaveTime = _dispatcher.Requests.Count;
            FanOutsStagedAtSaveTime  = _dispatcher.FanOuts.Count;
            return _db.SaveChangesAsync(ct);
        }

        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NoOpMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AnonymousUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? Mobile => null;
        public UserRole? Role => null;
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public Guid? SessionId => null;
        public string? DeviceId => null;
        public string? IpAddress => null;
        public bool IsAuthenticated => false;
        public bool IsInRole(UserRole role) => false;
        public bool HasPermission(string permission) => false;
    }
}
