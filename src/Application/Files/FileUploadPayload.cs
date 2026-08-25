namespace EventOpsOracle.Application.Files;

/// <summary>
/// Raw upload bytes + client-declared metadata, before any validation has
/// run. Used wherever a command needs to carry a file alongside other
/// fields (e.g. RegisterCrewCommand's profile photo / ID proof) rather than
/// going through FilesController's authenticated upload endpoint.
/// </summary>
public sealed record FileUploadPayload(byte[] Content, string FileName, string ContentType);
