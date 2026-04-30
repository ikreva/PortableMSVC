namespace PortableMSVC;

public sealed record PlannedPackage(string Id, bool Required, string Reason, int PayloadCount);
