namespace EventWOS.Domain.Enums;

/// <summary>
/// EventWOS document categories. Each maps to a storage-key prefix
/// (see Application.Files.FileStorageKeyBuilder) and a validation policy
/// (allowed extensions/MIME types, max size — see FileValidationPolicy).
/// Add new categories here; nothing else in the storage pipeline needs to
/// change (that's the point of the abstraction).
/// </summary>
public enum DocumentType
{
    CrewProfilePhoto = 1,
    CrewIdentificationProof = 2,
    VendorDocument = 3,
    EventDocument = 4
}
