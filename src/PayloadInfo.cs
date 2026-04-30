namespace PortableMSVC;

public sealed record PayloadInfo(string FileName, string Url, string? Sha256, long? Size);
