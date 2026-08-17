# File & Image Storage Module

Production-ready file/image storage for EventWOS. PostgreSQL holds only
metadata; the actual bytes live in pluggable object storage (or local disk
in dev). Business/handler code depends on one interface — swapping the
backend is a config change, not a code change.

## Folder structure

```
src/Domain/
  Enums/DocumentType.cs            Crew Profile Photo, Crew ID Proof, Vendor Document, Event Document
  Enums/StorageProvider.cs         Local | S3Compatible | AzureBlob (recorded per-row)
  Entities/FileDocument.cs         Metadata-only entity (BaseEntity: audit + soft-delete)

src/Application/
  Common/IFileStorage.cs           Upload/Download/Delete/Exists/GetPresignedDownloadUrl — the ONE
                                    abstraction business code depends on
  Common/IImageProcessor.cs        Optimize + thumbnail, kept separate from storage
  Files/
    FileValidationPolicy.cs        Size/MIME/extension rules per DocumentType — authoritative,
                                    re-checked server-side regardless of client checks
    FileStorageKeyBuilder.cs       Mints opaque Guid-based keys (crew/{id}/profile/{fileId}.jpg, ...)
    FileAccessPolicy.cs            Pure self-or-admin authorization decisions (unit-testable, no DI)
    DTOs/FileDocumentDto.cs        Client-facing shape — never exposes StorageKey
    Commands/UploadFileCommand.cs  Validate -> optimize (images) -> IFileStorage.UploadAsync -> persist
    Commands/DeleteFileCommand.cs  Storage delete (best-effort) + metadata soft-delete
    Queries/DownloadFileQuery.cs   Authorize -> load metadata -> IFileStorage.DownloadAsync -> stream

src/Infrastructure/
  Storage/LocalFileStorage.cs         Dev/MVP only — NOT durable on ephemeral container disks
  Storage/S3CompatibleFileStorage.cs  AWS S3 / Cloudflare R2 / MinIO (all speak the S3 API)
  Storage/AzureBlobFileStorage.cs     Azure Blob Storage
  Storage/ImageSharpProcessor.cs      Re-encode + thumbnail via SixLabors.ImageSharp

src/Persistence/
  Configurations/FileDocumentConfiguration.cs   snake_case columns, unique index on storage_key
  Migrations/20260817000000_AddFileDocuments.cs

src/Api/
  Controllers/FilesController.cs   POST /files/upload, GET /files/{id}/download, DELETE /files/{id}
                                    — all [Authorize], all gated by [Permission]

src/BlazorWeb/
  Services/FilesApiService.cs       Thin HTTP client — no storage credentials ever reach the browser
  Components/FileUploadInput.razor  Generic <InputFile> wrapper, reusable for any DocumentType

tests/Application.UnitTests/Files/
  FileValidationPolicyTests.cs, FileAccessPolicyTests.cs, FileStorageKeyBuilderTests.cs
```

## Switching storage provider (dev -> production)

One config value, `Storage:Provider`, read once in `Program.cs` to pick the
DI registration. No Application/Domain/Api code changes.

```json
"Storage": {
  "Provider": "Local",              // "Local" | "S3" | "AzureBlob"
  "Local":     { "RootPath": "/app/file-storage" },
  "S3":        { "BucketName": "", "Region": "us-east-1", "ServiceUrl": "", "AccessKey": "", "SecretKey": "" },
  "AzureBlob": { "ConnectionString": "", "ContainerName": "eventwos-files" }
}
```

- **AWS S3**: set `S3:BucketName` + `S3:Region`, leave `ServiceUrl` empty (or use IAM role — leave AccessKey/SecretKey empty too).
- **Cloudflare R2 / MinIO**: set `S3:ServiceUrl` to the R2/MinIO endpoint + `S3:AccessKey`/`S3:SecretKey`.
- **Azure Blob**: set `Provider: "AzureBlob"` + `AzureBlob:ConnectionString`.

**On Railway, set secrets as environment variables, never in appsettings.json**
(double-underscore maps to nested config): `Storage__S3__AccessKey`, `Storage__S3__SecretKey`, `Storage__AzureBlob__ConnectionString`, etc.

## Security model

- Nothing is public by default. Every endpoint in `FilesController` requires
  `[Authorize]` + a `[Permission]`; `FileAccessPolicy` then re-checks
  owner-or-admin per request.
- `CrewIdentificationProof` is stricter than every other type: reading it
  requires the separate `files:read_identity` permission (not just
  `files:manage`), and **every** read — including the owner viewing their
  own — writes an `AuditAction.SensitiveDocumentAccessed` row.
- Storage keys are Guid-based, never derived from the client filename —
  eliminates path traversal and filename-collision/overwrite risks.
- Size/MIME/extension are validated server-side in `FileValidationPolicy`
  regardless of what the client already checked.
- Storage credentials (S3/Azure) live only in `Infrastructure`/config — the
  Blazor client only ever talks to `FilesController`, never to the storage
  backend directly.

## Known follow-ups (not yet wired up)

- Crew self-registration (anonymous, before the account exists) still needs
  to be rewired to use this module for the profile-photo/ID-proof upload
  steps of the registration form — tracked separately.
- `User.AvatarUrl` is not populated from uploads anymore (per "never store
  permanent public URLs") — profile photo retrieval should go through
  `GET /files/{id}/download`, keyed off the latest `CrewProfilePhoto`
  `FileDocument` for that owner. A small "get my active profile photo id"
  lookup would close this gap.
