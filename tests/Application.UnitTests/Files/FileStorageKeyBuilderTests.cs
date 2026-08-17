using EventWOS.Application.Files;
using EventWOS.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Files;

public sealed class FileStorageKeyBuilderTests
{
    [Fact]
    public void Crew_profile_photo_key_matches_spec_format()
    {
        var owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var fileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var key = FileStorageKeyBuilder.Build(DocumentType.CrewProfilePhoto, owner, null, fileId, ".jpg");
        key.Should().Be($"crew/{owner}/profile/{fileId}.jpg");
    }

    [Fact]
    public void Crew_identification_proof_key_uses_identity_prefix()
    {
        var owner = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var key = FileStorageKeyBuilder.Build(DocumentType.CrewIdentificationProof, owner, null, fileId, ".jpg");
        key.Should().Be($"crew/{owner}/identity/{fileId}.jpg");
    }

    [Fact]
    public void Event_document_key_uses_entityId_when_provided()
    {
        var owner = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var key = FileStorageKeyBuilder.Build(DocumentType.EventDocument, owner, eventId, fileId, ".pdf");
        key.Should().Be($"events/{eventId}/documents/{fileId}.pdf");
    }

    [Fact]
    public void Two_different_files_never_collide_even_for_same_owner_and_type()
    {
        var owner = Guid.NewGuid();
        var key1 = FileStorageKeyBuilder.Build(DocumentType.CrewProfilePhoto, owner, null, Guid.NewGuid(), ".jpg");
        var key2 = FileStorageKeyBuilder.Build(DocumentType.CrewProfilePhoto, owner, null, Guid.NewGuid(), ".jpg");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Thumbnail_key_inserts_suffix_before_the_extension()
    {
        var key = "crew/abc/profile/xyz.jpg";
        FileStorageKeyBuilder.ThumbnailKey(key).Should().Be("crew/abc/profile/xyz-thumb.jpg");
    }
}
