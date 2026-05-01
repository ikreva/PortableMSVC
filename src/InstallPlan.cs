
namespace PortableMSVC;

public sealed record InstallPlan(string Vs, string ProductVersion, string MsvcVersion, string SdkVersion, string RedistVersion, string Host, IReadOnlyList<string> Targets, IReadOnlyList<PlannedPackage> RootPackages, IReadOnlyList<PlannedPackage> DependencyClosure, IReadOnlyList<PlannedPayload> Payloads, IReadOnlyList<string> SdkPayloads, IReadOnlyList<PlannedPayload> RuntimePayloads, IReadOnlyList<PlanIssue> Issues)
{
	public bool Success => Issues.All((PlanIssue x) => !StringComparer.OrdinalIgnoreCase.Equals(x.Severity, "error"));
}
