using EventWOS.Application.Files;
using EventWOS.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Files;

/// <summary>
/// "Files must not be publicly accessible by default" — every case here
/// defaults to deny unless explicitly the owner or an appropriately
/// permissioned admin/manager. Identification proof is deliberately
/// stricter than every other document type.
/// </summary>
public sealed class FileAccessPolicyTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    [Fact]
    public void Owner_can_always_download_their_own_file()
    {
        FileAccessPolicy.CanDownload(DocumentType.CrewIdentificationProof, Owner, Owner,
            callerCanManageOthers: false, callerCanReadIdentity: false).Should().BeTrue();
    }

    [Fact]
    public void Stranger_without_permissions_cannot_download_anything()
    {
        FileAccessPolicy.CanDownload(DocumentType.CrewProfilePhoto, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false).Should().BeFalse();
    }

    [Fact]
    public void Manager_permission_alone_is_NOT_enough_to_read_identity_proof()
    {
        // files:manage grants profile photos / vendor / event docs, but identity proof
        // requires the separate, stricter files:read_identity permission.
        FileAccessPolicy.CanDownload(DocumentType.CrewIdentificationProof, Owner, Stranger,
            callerCanManageOthers: true, callerCanReadIdentity: false).Should().BeFalse();
    }

    [Fact]
    public void Files_read_identity_permission_grants_identity_proof_access()
    {
        FileAccessPolicy.CanDownload(DocumentType.CrewIdentificationProof, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: true).Should().BeTrue();
    }

    [Fact]
    public void Files_manage_permission_grants_access_to_non_identity_documents()
    {
        FileAccessPolicy.CanDownload(DocumentType.VendorDocument, Owner, Stranger,
            callerCanManageOthers: true, callerCanReadIdentity: false).Should().BeTrue();
    }

    [Fact]
    public void Owner_can_always_delete_their_own_file()
        => FileAccessPolicy.CanDelete(DocumentType.EventDocument, Owner, Owner, callerCanManageOthers: false).Should().BeTrue();

    [Fact]
    public void Stranger_without_manage_permission_cannot_delete()
        => FileAccessPolicy.CanDelete(DocumentType.EventDocument, Owner, Stranger, callerCanManageOthers: false).Should().BeFalse();

    [Fact]
    public void Uploading_for_another_owner_requires_manage_permission()
    {
        FileAccessPolicy.CanUploadFor(Owner, Stranger, callerCanManageOthers: false).Should().BeFalse();
        FileAccessPolicy.CanUploadFor(Owner, Stranger, callerCanManageOthers: true).Should().BeTrue();
    }
}
