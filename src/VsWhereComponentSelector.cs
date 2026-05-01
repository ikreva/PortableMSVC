
namespace PortableMSVC;

public static class VsWhereComponentSelector
{
	private const string X86X64ToolsComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
	private const string ArmToolsComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM";
	private const string Arm64ToolsComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM64";

	public static IReadOnlyList<VsWherePackage> SelectVcToolComponents(PackageIndex index, InstallPlan plan)
	{
		HashSet<string> componentIds = new(StringComparer.OrdinalIgnoreCase);
		AddArchitectureComponents(ArchitectureNames.Parse(plan.Host), componentIds);
		foreach (string target in plan.Targets)
		{
			AddArchitectureComponents(ArchitectureNames.Parse(target), componentIds);
		}

		List<VsWherePackage> packages = new();
		AddIfPresent(index, componentIds, X86X64ToolsComponent, packages);
		AddIfPresent(index, componentIds, ArmToolsComponent, packages);
		AddIfPresent(index, componentIds, Arm64ToolsComponent, packages);
		return packages;
	}

	private static void AddArchitectureComponents(Architecture architecture, HashSet<string> componentIds)
	{
		switch (architecture)
		{
		case Architecture.X86:
		case Architecture.X64:
			componentIds.Add(X86X64ToolsComponent);
			break;
		case Architecture.Arm:
			componentIds.Add(ArmToolsComponent);
			break;
		case Architecture.Arm64:
			componentIds.Add(Arm64ToolsComponent);
			break;
		}
	}

	private static void AddIfPresent(PackageIndex index, HashSet<string> wanted, string componentId, List<VsWherePackage> packages)
	{
		if (!wanted.Contains(componentId))
		{
			return;
		}

		PackageInfo? component = index.Find(componentId);
		if (component?.Version is not { Length: > 0 } version)
		{
			return;
		}

		packages.Add(new VsWherePackage
		{
			Id = component.Id,
			Version = version,
			Type = "Component"
		});
	}
}
