using FluentAssertions;
using Xunit;

// NOTE on the unqualified-name landmine:
// This file lives in EventWOS.Application.UnitTests.Domain. When the compiler
// resolves a bare `ScopeOfWork`, it walks the namespace tree upward and finds
// EventWOS.Application.ScopeOfWork (the catalog feature folder) BEFORE it
// reaches any using-directive alias. A `using ScopeOfWork = ...` alias loses
// the tie-break. So we type-alias via a `using static`-free pattern: every
// reference is to the typedef below.

namespace EventWOS.Application.UnitTests.Domain;

// Alias placed INSIDE the namespace — wins over the inherited
// EventWOS.Application.ScopeOfWork namespace lookup.
using ScopeOfWork = EventWOS.Domain.Entities.ScopeOfWork;

/// <summary>
/// Pins the invariants on <see cref="ScopeOfWork"/>. The catalog is small but
/// it's load-bearing for every event-shift in Phase B+, so the basic rules
/// (name required, length-bounded, archived rows can't be edited, archive +
/// restore are idempotent) need to stay green.
///
/// Lives in the same Application test project as EventUpdateTests for now —
/// rule from those tests applies: spin up a dedicated Domain project only
/// when there's a second domain rule that benefits from isolation.
/// </summary>
public sealed class ScopeOfWorkTests
{
    private static ScopeOfWork NewScope(string name = "Box Office", string? desc = null) =>
        new(name, desc, createdByUserId: Guid.NewGuid());

    [Fact]
    public void Constructor_sets_name_description_and_audit_fields()
    {
        var creator = Guid.NewGuid();
        var s = new ScopeOfWork("Box Office", "Run ticketing", creator);

        s.Name.Should().Be("Box Office");
        s.Description.Should().Be("Run ticketing");
        s.CreatedByUserId.Should().Be(creator);
        s.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Constructor_trims_whitespace_on_name_and_description()
    {
        var s = new ScopeOfWork("  Gates  ", "   Door duty   ", Guid.NewGuid());
        s.Name.Should().Be("Gates");
        s.Description.Should().Be("Door duty");
    }

    [Fact]
    public void Constructor_treats_blank_description_as_null()
    {
        var s = new ScopeOfWork("Gates", "   ", Guid.NewGuid());
        s.Description.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_throws_when_name_is_missing(string? badName)
    {
        // ArgumentException so the handler can convert to a Result.Failure with
        // a clean error code (the CreateScopeOfWorkHandler catches and surfaces
        // ex.Message verbatim).
        var act = () => new ScopeOfWork(badName!, null, Guid.NewGuid());
        act.Should().Throw<ArgumentException>()
           .WithMessage("*name is required*");
    }

    [Fact]
    public void Constructor_throws_when_name_exceeds_80_characters()
    {
        var tooLong = new string('x', 81);
        var act = () => new ScopeOfWork(tooLong, null, Guid.NewGuid());
        act.Should().Throw<ArgumentException>()
           .WithMessage("*80 characters or fewer*");
    }

    [Fact]
    public void Constructor_throws_when_description_exceeds_500_characters()
    {
        var tooLong = new string('x', 501);
        var act = () => new ScopeOfWork("Box Office", tooLong, Guid.NewGuid());
        act.Should().Throw<ArgumentException>()
           .WithMessage("*500 characters or fewer*");
    }

    [Fact]
    public void Update_changes_name_and_description_and_sets_updated_at()
    {
        var s = NewScope("Box Office", "Old");
        s.UpdatedAt.Should().BeNull();   // pristine

        s.Update("Box Office (VIP)", "New copy");

        s.Name.Should().Be("Box Office (VIP)");
        s.Description.Should().Be("New copy");
        s.UpdatedAt.Should().NotBeNull();
        s.UpdatedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, precision: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Update_throws_when_row_is_archived()
    {
        var s = NewScope();
        s.Archive(Guid.NewGuid());

        var act = () => s.Update("Anything", null);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*archived*restore*");
    }

    [Fact]
    public void Archive_is_idempotent()
    {
        var s = NewScope();
        var actor = Guid.NewGuid();

        s.Archive(actor);
        var firstDeletedAt = s.DeletedAt;

        // Calling again must not throw and must not bump DeletedAt — preserves
        // the original audit timestamp from the first archive.
        var act = () => s.Archive(Guid.NewGuid());
        act.Should().NotThrow();
        s.IsDeleted.Should().BeTrue();
        s.DeletedAt.Should().Be(firstDeletedAt);
    }

    [Fact]
    public void Restore_clears_archive_audit_fields()
    {
        var s = NewScope();
        s.Archive(Guid.NewGuid());
        s.IsDeleted.Should().BeTrue();

        s.Restore();

        s.IsDeleted.Should().BeFalse();
        s.DeletedAt.Should().BeNull();
        s.DeletedBy.Should().BeNull();
        s.UpdatedAt.Should().NotBeNull();   // restore bumps updated_at
    }

    [Fact]
    public void Restore_is_idempotent_on_already_active_row()
    {
        var s = NewScope();
        s.IsDeleted.Should().BeFalse();

        var act = () => s.Restore();
        act.Should().NotThrow();
        s.IsDeleted.Should().BeFalse();
    }
}
