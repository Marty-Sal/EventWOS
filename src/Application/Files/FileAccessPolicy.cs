using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Files;

/// <summary>
/// Pure authorization decisions for file access, factored out of the
/// MediatR handlers so they're trivially unit-testable without EF/DI.
/// "Files must not be publicly accessible by default" — every path here
/// defaults to deny.
/// </summary>
public static class FileAccessPolicy
{
    /// <summary>
    /// Document types a crew member submits during self-registration. These are
    /// exactly the documents the approving vendor has to look at to make an
    /// approve/reject decision, so they are the ONLY types the vendor
    /// relationship below can unlock — a vendor never gains blanket access to
    /// everything a crew user owns.
    /// </summary>
    public static bool IsCrewRegistrationDocument(DocumentType type)
        => type is DocumentType.CrewProfilePhoto or DocumentType.CrewIdentificationProof;

    /// <param name="callerCanManageOthers">Caller holds files:manage (Admin/Manager).</param>
    /// <param name="callerCanReadIdentity">Caller holds files:read_identity (Admin/Manager) — required in addition to files:manage for CrewIdentificationProof, since that permission is deliberately separate/stricter.</param>
    /// <param name="callerIsOwningVendor">
    /// Caller is the vendor this crew owner belongs to — either the crew's
    /// approving vendor (registered with that vendor's referral code) or their
    /// current vendor. Crew approval is delegated to the vendor, so the vendor
    /// must be able to open the photo and ID proof they are being asked to
    /// verify; without this they saw the documents listed but were refused on
    /// download. Scoped to registration documents only, and every identity-proof
    /// read is still audit-logged by the handler.
    /// </param>
    public static bool CanDownload(
        DocumentType type, Guid ownerId, Guid requesterId,
        bool callerCanManageOthers, bool callerCanReadIdentity,
        bool callerIsOwningVendor = false)
    {
        if (requesterId == ownerId) return true;

        if (callerIsOwningVendor && IsCrewRegistrationDocument(type)) return true;

        if (type == DocumentType.CrewIdentificationProof)
            return callerCanReadIdentity;

        return callerCanManageOthers;
    }

    public static bool CanDelete(
        DocumentType type, Guid ownerId, Guid requesterId, bool callerCanManageOthers)
        => requesterId == ownerId || callerCanManageOthers;

    public static bool CanUploadFor(Guid ownerId, Guid requesterId, bool callerCanManageOthers)
        => requesterId == ownerId || callerCanManageOthers;
}
