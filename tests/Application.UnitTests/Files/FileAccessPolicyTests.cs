using EventOpsOracle.Application.Files;
using EventOpsOracle.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Files;

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

    // ── Vendor-scoped access to their own crew's registration documents ─────
    // Crew approval is delegated to the referring vendor, so that vendor must be
    // able to open the photo and ID proof they are being asked to verify.

    [Fact]
    public void Owning_vendor_can_download_their_crews_profile_photo()
    {
        FileAccessPolicy.CanDownload(DocumentType.CrewProfilePhoto, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false,
            callerIsOwningVendor: true).Should().BeTrue();
    }

    [Fact]
    public void Owning_vendor_can_download_their_crews_identity_proof_without_read_identity()
    {
        // The vendor IS the identity verifier for their own crew; the access is
        // audit-logged rather than blocked.
        FileAccessPolicy.CanDownload(DocumentType.CrewIdentificationProof, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false,
            callerIsOwningVendor: true).Should().BeTrue();
    }

    [Fact]
    public void Vendor_relationship_does_NOT_unlock_non_registration_documents()
    {
        // Scoped deliberately: being someone's vendor is not blanket access to
        // every file that user owns.
        FileAccessPolicy.CanDownload(DocumentType.VendorDocument, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false,
            callerIsOwningVendor: true).Should().BeFalse();

        FileAccessPolicy.CanDownload(DocumentType.EventDocument, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false,
            callerIsOwningVendor: true).Should().BeFalse();
    }

    [Fact]
    public void Unrelated_vendor_still_cannot_download_crew_registration_documents()
    {
        // callerIsOwningVendor is false for a vendor the crew member has no link
        // to, which must stay a denial for both registration document types.
        FileAccessPolicy.CanDownload(DocumentType.CrewProfilePhoto, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false,
            callerIsOwningVendor: false).Should().BeFalse();

        FileAccessPolicy.CanDownload(DocumentType.CrewIdentificationProof, Owner, Stranger,
            callerCanManageOthers: false, callerCanReadIdentity: false,
            callerIsOwningVendor: false).Should().BeFalse();
    }

    [Fact]
    public void Vendor_relationship_does_not_grant_delete()
    {
        // Reviewing a document is not the same as being able to destroy it.
        FileAccessPolicy.CanDelete(DocumentType.CrewIdentificationProof, Owner, Stranger,
            callerCanManageOthers: false).Should().BeFalse();
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
