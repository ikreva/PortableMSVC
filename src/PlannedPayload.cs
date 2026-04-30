namespace PortableMSVC;

public sealed record PlannedPayload(string PackageId, string FileName, string Url, string? Sha256, long? Size);
