using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Users.Queries;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EventWOS.Application.UnitTests.Users;

/// <summary>
/// The profile photo shown on My Profile and in the sidebar.
///
/// Profile photos are private files with no public URL, so /users/me hands out a
/// FileDocument id and the client pulls the bytes through the authenticated
/// download endpoint. These tests pin the resolution rule the whole avatar feature
/// leans on: NEWEST ACTIVE WINS. That is what makes "change photo" work as a
/// replacement -- the client uploads the new row first and retires the old one
/// second, so if that retirement ever fails, the user still sees the right face.
/// </summary>
public class ProfilePhotoResolutionTests
{
    [Fact]
    public async Task The_photo_uploaded_at_registration_is_returned()
    {
        using var db = NewContext();
        var user = SeedUser(db, UserRole.Crew);
        var photo = AddPhoto(db, user.Id, DocumentType.CrewProfilePhoto, DateTime.UtcNow.AddDays(-3));

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(user.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProfilePhotoFileId.Should().Be(photo.Id);
    }

    [Fact]
    public async Task A_vendors_photo_is_found_under_its_own_document_type()
    {
        // Crew and vendor registration store the photo under different types, and
        // the avatar must not care which.
        using var db = NewContext();
        var user = SeedUser(db, UserRole.Vendor);
        var photo = AddPhoto(db, user.Id, DocumentType.VendorProfilePhoto, DateTime.UtcNow.AddDays(-1));

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(user.Id), default);

        result.Value.ProfilePhotoFileId.Should().Be(photo.Id);
    }

    [Fact]
    public async Task The_newest_photo_wins_when_one_replaces_another()
    {
        using var db = NewContext();
        var user = SeedUser(db, UserRole.Crew);
        AddPhoto(db, user.Id, DocumentType.CrewProfilePhoto, DateTime.UtcNow.AddDays(-5));
        var replacement = AddPhoto(db, user.Id, DocumentType.CrewProfilePhoto, DateTime.UtcNow);

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(user.Id), default);

        result.Value.ProfilePhotoFileId.Should().Be(replacement.Id,
            "the replacement must show even if retiring the previous row failed");
    }

    [Fact]
    public async Task A_deleted_photo_is_ignored()
    {
        using var db = NewContext();
        var user = SeedUser(db, UserRole.Crew);
        var retired = AddPhoto(db, user.Id, DocumentType.CrewProfilePhoto, DateTime.UtcNow);
        retired.IsDeleted = true;
        db.SaveChanges();

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(user.Id), default);

        result.Value.ProfilePhotoFileId.Should().BeNull();
    }

    [Fact]
    public async Task An_ID_proof_is_never_mistaken_for_a_profile_photo()
    {
        // Crew upload both at registration. Putting someone's ID scan in their
        // avatar would be a privacy incident, not a cosmetic bug.
        using var db = NewContext();
        var user = SeedUser(db, UserRole.Crew);
        AddPhoto(db, user.Id, DocumentType.CrewIdentificationProof, DateTime.UtcNow);

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(user.Id), default);

        result.Value.ProfilePhotoFileId.Should().BeNull();
    }

    [Fact]
    public async Task Another_users_photo_is_never_returned()
    {
        using var db = NewContext();
        var me = SeedUser(db, UserRole.Crew);
        var someoneElse = SeedUser(db, UserRole.Crew, "9876500099");
        AddPhoto(db, someoneElse.Id, DocumentType.CrewProfilePhoto, DateTime.UtcNow);

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(me.Id), default);

        result.Value.ProfilePhotoFileId.Should().BeNull();
    }

    [Fact]
    public async Task No_upload_means_no_photo_rather_than_a_failure()
    {
        using var db = NewContext();
        var user = SeedUser(db, UserRole.Crew);

        var result = await NewHandler(db).Handle(new GetCurrentUserQuery(user.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProfilePhotoFileId.Should().BeNull("the avatar falls back to the initial");
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"profile-photo-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new NoOpMediator(),
            new AnonymousDbUser());

    private static GetCurrentUserHandler NewHandler(AppDbContext db) =>
        new(db, new StubPermissions());

    private static User SeedUser(AppDbContext db, UserRole role, string mobile = "9876500001")
    {
        var user = new User(mobile, "Sameer Khan", role);
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static FileDocument AddPhoto(AppDbContext db, Guid ownerId, DocumentType type, DateTime createdAt)
    {
        var doc = new FileDocument(
            ownerId, entityId: null, type,
            storageKey: $"crew/{ownerId}/profile/{Guid.NewGuid()}.jpg",
            originalFileName: "me.jpg",
            contentType: "image/jpeg",
            fileSizeBytes: 2048,
            fileHash: "hash",
            provider: StorageProvider.Local);

        db.FileDocuments.Add(doc);
        db.SaveChanges();

        // CreatedAt is set by the base entity, so the ordering the test needs is
        // applied after the insert.
        doc.CreatedAt = createdAt;
        db.SaveChanges();
        return doc;
    }

    private sealed class StubPermissions : IPermissionService
    {
        public Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(
            Guid userId, UserRole role, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "profile:read" });

        public Task InvalidateCacheForUserAsync(Guid userId, CancellationToken ct = default)
            => Task.CompletedTask;
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

    private sealed class AnonymousDbUser : EventWOS.Domain.Interfaces.ICurrentUser
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
