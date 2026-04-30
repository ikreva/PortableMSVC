using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PortableMSVC;

[JsonSerializable(typeof(InstallPlan))]
[JsonSerializable(typeof(PlanIssue))]
[JsonSerializable(typeof(PlannedPackage))]
[JsonSerializable(typeof(PlannedPayload))]
[JsonSerializable(typeof(ManifestMeta))]
[JsonSerializable(typeof(VsWhereState))]
[JsonSerializable(typeof(PortableSetupStatus))]
[JsonSerializable(typeof(IReadOnlyList<VsWhereInstance>))]
[JsonSerializable(typeof(string[]))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PortableMSVCJsonContext : JsonSerializerContext;
