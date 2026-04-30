using System.Collections.Generic;

namespace PortableMSVC;

public sealed record PlanRequest(string Vs, string? VcVersion, string? SdkVersion, string? RedistVersion, Architecture Host, IReadOnlyList<Architecture> Targets, bool WithRuntime);
