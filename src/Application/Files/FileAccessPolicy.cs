using EventWOS.Domain.Enums;

namespace EventWOS.Application.Files;

/// <summary>
/// Pure authorization decisions for file access, factored out of the
/// MediatR handlers so they're trivially unit-testable without EF/DI.
/// "Files must not be publicly accessible by default" — every path here
/// defaults to deny.
/// </summary>
public static class FileAccessPolicy
{
    /// <param name="callerCanManageOthers">Caller holds files:manage (Admin/Manager).</param>
    /// <param name="callerCanReadIdentity">Caller holds files:read_identity (Admin/Manager) — required in addition to files:manage for CrewIdentificationProof, since that permission is deliberately separate/stricter.</param>
    public static bool CanDownload(
        DocumentType type, Guid ownerId, Guid requesterId,
        bool callerCanManageOthers, bool callerCanReadIdentity)
    {
        if (requesterId == ownerId) return true;

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
