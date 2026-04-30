using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PortableMSVC;

public sealed class PlanBuilder
{
	private static readonly Architecture[] AllArchitectures =
	[
		Architecture.X86,
		Architecture.X64,
		Architecture.Arm,
		Architecture.Arm64
	];

	public InstallPlan Build(PackageIndex index, PlanRequest request)
	{
		List<PlanIssue> issues = new();
		ResolvedVersion msvc;
		ResolvedVersion sdk;
		ResolvedVersion redist;
		try
		{
			msvc = index.ResolveMsvcVersion(request.VcVersion);
			sdk = index.ResolveSdkVersion(request.SdkVersion);
			redist = index.ResolveRedistVersion(request.RedistVersion, msvc.FullVersion);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			return Failed(index, request, ex.Message);
		}
		if (request.Host == Architecture.Arm)
		{
			return UnsupportedHost(index, request, msvc, sdk, redist);
		}
		Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase);
		List<Architecture> supportedTargets = new();
		foreach (Architecture target in request.Targets.Distinct())
		{
			string compilerTools = $"Microsoft.VC.{msvc.FullVersion}.Tools.Host{request.Host.PackageTitle()}.Target{target.PackageTitle()}.base";
			string vcvarsHostTarget = "Microsoft.VisualCpp.Tools.Host" + request.Host.PackageTitle() + ".Target" + target.PackageTitle();
			bool hasCompilerTools = index.Contains(compilerTools);
			bool hasVcvarsHostTarget = index.Contains(vcvarsHostTarget);

			// 编译器包提供这个 host/target 组合对应的 cl/link/lib。
			// VisualCpp.Tools 包很小，但提供官方 vcvars*.bat 包装脚本。
			// 二者缺一就把该组合视为不支持，避免继续产生 CRT/ATL/PGO/SDK 的级联错误。
			if (!hasCompilerTools)
			{
				issues.Add(MissingCompilerToolsIssue(index, compilerTools, request.Host, target, msvc));
			}
			if (hasCompilerTools && !hasVcvarsHostTarget)
			{
				issues.Add(MissingVcvarsHostTargetIssue(vcvarsHostTarget, request.Host, target, msvc));
			}
			if (!hasCompilerTools || !hasVcvarsHostTarget)
			{
				continue;
			}

			supportedTargets.Add(target);
			Add(compilerTools, "compiler-tools");
			Add(vcvarsHostTarget, "vcvars-host-target");
			string? resourcePackage = ResolveCompilerResourcePackage(index, msvc.FullVersion, request.Host, target);
			if (resourcePackage == null)
			{
				issues.Add(MissingPackageIssue($"Microsoft.VC.{msvc.FullVersion}.Tools.Host{request.Host.PackageTitle()}.Target{target.PackageTitle()}.Res.base", request, msvc));
			}
			else
			{
				Add(resourcePackage, "compiler-resources");
			}
			Add($"Microsoft.VC.{msvc.FullVersion}.CRT.{target.Package()}.Desktop.base", "crt-desktop-libs");
			Add($"Microsoft.VC.{msvc.FullVersion}.CRT.{target.Package()}.Store.base", "crt-store-libs");
			Add($"Microsoft.VC.{msvc.FullVersion}.Premium.Tools.Host{request.Host.PackageTitle()}.Target{target.PackageTitle()}.base", "premium-tools");
			Add($"Microsoft.VC.{msvc.FullVersion}.PGO.{target.Package()}.base", "pgo-libs");
			Add($"Microsoft.VC.{msvc.FullVersion}.ATL.{target.Package()}.base", "atl-libs");
			if (target is Architecture.X86 or Architecture.X64)
			{
				Add($"Microsoft.VC.{msvc.FullVersion}.ASAN.{target.Package()}.base", "asan-libs");
			}
			string? redistPackage = ResolveRedistPackage(index, redist.FullVersion, target);
			if (redistPackage == null)
			{
				issues.Add(new PlanIssue("error", "missing-redist", $"MSVC redist {redist.FullVersion} 没有 target {target.Cli()} 对应的 redist 包。"));
			}
			else
			{
				Add(redistPackage, "redist");
			}
		}
		if (supportedTargets.Count > 0)
		{
			AddCommonRoots();
		}
		List<PlannedPackage> rootPackages = new();
		foreach ((string id, string reason) in roots.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
		{
			PackageInfo? package = index.Find(id);
			if (package is null)
			{
				issues.Add(MissingPackageIssue(id, request, msvc));
				rootPackages.Add(new PlannedPackage(id, Required: true, reason, 0));
			}
			else
			{
				rootPackages.Add(new PlannedPackage(package.Id, Required: true, reason, package.Payloads.Count));
			}
		}
		IReadOnlyList<PackageInfo> closurePackages = index.ResolveDependencyClosure(roots.Keys, ShouldExpandDependencies)
			.Where(x => !IsMsvcAuxiliaryPropsPackage(x.Id))
			.ToList();
		List<PlannedPackage> dependencyClosure = closurePackages
			.Select(x => new PlannedPackage(x.Id, Required: true, roots.GetValueOrDefault(x.Id, "dependency"), x.Payloads.Count))
			.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
			.ToList();
		List<PlannedPayload> payloads = closurePackages
			.SelectMany(package => package.Payloads.Select(payload => new PlannedPayload(package.Id, payload.FileName, payload.Url, payload.Sha256, payload.Size)))
			.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
			.ToList();
		foreach (PlannedPayload payload in payloads)
		{
			if (string.IsNullOrWhiteSpace(payload.Url) || string.IsNullOrWhiteSpace(payload.Sha256))
			{
				issues.Add(new PlanIssue("error", "payload-metadata", "Payload '" + payload.FileName + "' 缺少 url 或 sha256。"));
			}
		}
		IReadOnlyList<string> sdkPayloads = supportedTargets.Count == 0
			? Array.Empty<string>()
			: SelectSdkPayloads(index, sdk.FullVersion, supportedTargets, issues);
		IReadOnlyList<PlannedPayload> runtimePayloads = supportedTargets.Count == 0
			? Array.Empty<PlannedPayload>()
			: request.WithRuntime ? SelectRuntimeInstallerPayloads(index, supportedTargets, issues) : Array.Empty<PlannedPayload>();
		return new InstallPlan(index.VsAlias, index.ProductVersion, msvc.FullVersion, sdk.FullVersion, redist.FullVersion, request.Host.Cli(), request.Targets.Select(x => x.Cli()).ToList(), rootPackages, dependencyClosure, payloads, sdkPayloads, runtimePayloads, issues);
		void Add(string key2, string value2)
		{
			roots.TryAdd(key2, value2);
		}

		void AddCommonRoots()
		{
			// 官方 vcvars/VsDevCmd 包是当前便携路线的一部分。
			// 后续通过修补 winsdk.bat 找便携 SDK，不写 SDK 或 VS 注册表状态。
			Add("Microsoft.VisualCpp.DIA.SDK", "dia-sdk");
			Add("Microsoft.VisualCpp.Tools.Core.x86", "vcvars-core");
			Add("Microsoft.VisualStudio.VC.DevCmd", "devcmd");
			Add("Microsoft.VisualStudio.VC.vcvars", "vcvars");
			Add("Microsoft.VisualStudio.VsDevCmd.Core.WinSdk", "devcmd-winsdk");
			Add("Microsoft.VC." + msvc.FullVersion + ".CRT.Headers.base", "crt-headers");
			Add("Microsoft.VC." + msvc.FullVersion + ".CRT.Source.base", "crt-source");
			Add("Microsoft.VC." + msvc.FullVersion + ".ASAN.Headers.base", "asan-headers");
			Add("Microsoft.VC." + msvc.FullVersion + ".PGO.Headers.base", "pgo-headers");
			Add("Microsoft.VC." + msvc.FullVersion + ".ATL.Headers.base", "atl-headers");
			Add("Microsoft.VC." + msvc.FullVersion + ".ATL.Source.base", "atl-source");
		}
	}

	private static InstallPlan Failed(PackageIndex index, PlanRequest request, string message)
	{
		return new InstallPlan(index.VsAlias, index.ProductVersion, request.VcVersion ?? "", request.SdkVersion ?? "", request.RedistVersion ?? request.VcVersion ?? "", request.Host.Cli(), request.Targets.Select(x => x.Cli()).ToList(), Array.Empty<PlannedPackage>(), Array.Empty<PlannedPackage>(), Array.Empty<PlannedPayload>(), Array.Empty<string>(), Array.Empty<PlannedPayload>(), new[] { new PlanIssue("error", "version-resolution", message) });
	}

	private static bool IsMsvcAuxiliaryPropsPackage(string packageId)
	{
		if (packageId.StartsWith("Microsoft.VisualCpp.Servicing.", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		const string prefix = "Microsoft.VC.";
		if (!packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return packageId.IndexOf(".Props", prefix.Length, StringComparison.OrdinalIgnoreCase) >= 0
			|| packageId.IndexOf(".Servicing", prefix.Length, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool ShouldExpandDependencies(PackageInfo package)
	{
		if (package.Id.Equals("Microsoft.VisualCpp.Tools.Core.x86", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return !package.Id.StartsWith("Microsoft.VisualCpp.Tools.Host", StringComparison.OrdinalIgnoreCase);
	}

	private static InstallPlan UnsupportedHost(PackageIndex index, PlanRequest request, ResolvedVersion msvc, ResolvedVersion sdk, ResolvedVersion redist)
	{
		PlanIssue issue = new PlanIssue(
			"error",
			"unsupported-host-architecture",
			$"VS {index.VsAlias} / MSVC {msvc.FullVersion} 不支持 host arm。MSVC host 架构只支持 x64、x86、arm64；arm 表示 ARM32，只能作为 target 使用。",
			"请改用 --host x64、--host x86 或 --host arm64；如果要生成 ARM32 程序，请使用 --target arm。");
		return new InstallPlan(
			index.VsAlias,
			index.ProductVersion,
			msvc.FullVersion,
			sdk.FullVersion,
			redist.FullVersion,
			request.Host.Cli(),
			request.Targets.Select(x => x.Cli()).ToList(),
			Array.Empty<PlannedPackage>(),
			Array.Empty<PlannedPackage>(),
			Array.Empty<PlannedPayload>(),
			Array.Empty<string>(),
			Array.Empty<PlannedPayload>(),
			new[] { issue });
	}

	private static PlanIssue MissingPackageIssue(string id, PlanRequest request, ResolvedVersion msvc)
	{
		if (id.Contains(".TargetARM.", StringComparison.OrdinalIgnoreCase) || id.Contains(".targetarm.", StringComparison.OrdinalIgnoreCase) || id.Contains(".ARM.", StringComparison.OrdinalIgnoreCase) || id.Contains(".arm.", StringComparison.OrdinalIgnoreCase))
		{
			return new PlanIssue("error", "missing-package", "必需包 '" + id + "' 在 manifest 中不存在。", "MSVC " + msvc.FullVersion + " 可能不支持 ARM32 target。请改用 target arm64，或选择仍包含 ARM32 包的旧版 VS/MSVC 组合。");
		}
		return new PlanIssue("error", "missing-package", "必需包 '" + id + "' 在 manifest 中不存在。");
	}

	private static PlanIssue MissingCompilerToolsIssue(PackageIndex index, string id, Architecture host, Architecture target, ResolvedVersion msvc)
	{
		string suggestion = BuildCompilerToolsSuggestion(index, host, target, msvc);
		return new PlanIssue(
			"error",
			"missing-compiler-tools",
			$"MSVC {msvc.FullVersion} 不支持主机架构 {host.Cli()} 到目标架构 {target.Cli()} 的编译器工具。",
			suggestion);
	}

	private static string BuildCompilerToolsSuggestion(PackageIndex index, Architecture host, Architecture target, ResolvedVersion msvc)
	{
		IReadOnlyList<Architecture> hostsForTarget = SupportedHostsForTarget(index, msvc.FullVersion, target);
		if (hostsForTarget.Count > 0 && !hostsForTarget.Contains(host))
		{
			return $"当前 MSVC 支持目标架构 {target.Cli()}，但可用主机架构只有 {FormatArchitectures(hostsForTarget)}。请改用 --host {hostsForTarget[^1].Cli()}，或选择支持 {host.Cli()} 主机架构的新版 MSVC。";
		}

		IReadOnlyList<Architecture> targetsForHost = SupportedTargetsForHost(index, msvc.FullVersion, host);
		if (targetsForHost.Count > 0 && !targetsForHost.Contains(target))
		{
			return $"当前 MSVC 支持主机架构 {host.Cli()}，但可用目标架构只有 {FormatArchitectures(targetsForHost)}。请调整 --target 或选择支持该目标架构的 MSVC。";
		}

		return target == Architecture.Arm
			? "请改用目标架构 arm64，或选择支持 ARM32 目标架构的旧版 MSVC。"
			: "请换用当前 VS/MSVC 支持的主机和目标架构组合。";
	}

	private static IReadOnlyList<Architecture> SupportedHostsForTarget(PackageIndex index, string msvcVersion, Architecture target)
	{
		return AllArchitectures
			.Where(host => host != Architecture.Arm)
			.Where(host => index.Contains($"Microsoft.VC.{msvcVersion}.Tools.Host{host.PackageTitle()}.Target{target.PackageTitle()}.base"))
			.ToList();
	}

	private static IReadOnlyList<Architecture> SupportedTargetsForHost(PackageIndex index, string msvcVersion, Architecture host)
	{
		return AllArchitectures
			.Where(target => index.Contains($"Microsoft.VC.{msvcVersion}.Tools.Host{host.PackageTitle()}.Target{target.PackageTitle()}.base"))
			.ToList();
	}

	private static string FormatArchitectures(IReadOnlyList<Architecture> architectures)
	{
		return string.Join("、", architectures.Select(x => x.Cli()));
	}

	private static PlanIssue MissingVcvarsHostTargetIssue(string id, Architecture host, Architecture target, ResolvedVersion msvc)
	{
		return new PlanIssue(
			"error",
			"missing-vcvars-host-target",
			$"MSVC {msvc.FullVersion} 不支持主机架构 {host.Cli()} 到目标架构 {target.Cli()} 的官方 vcvars 脚本。",
			"请换用当前 VS/MSVC 支持的主机和目标架构组合。");
	}

	private static string? ResolveRedistPackage(PackageIndex index, string redistVersion, Architecture target)
	{
		string suffix = ((target == Architecture.Arm) ? ".onecore.desktop" : "");
		string direct = $"Microsoft.VC.{redistVersion}.CRT.Redist.{target.Package()}{suffix}.base";
		if (index.Contains(direct))
		{
			return direct;
		}
		string meta = "Microsoft.VisualCpp.CRT.Redist." + target.Package() + suffix;
		return index.Find(meta)?.Dependencies
			.Select(x => new { Id = x, Version = TryGetRedistPackageVersion(x) })
			.Where(x => x.Version != null && NaturalVersionComparer.Instance.Compare(x.Version, redistVersion) <= 0)
			.OrderBy(x => x.Version!, NaturalVersionComparer.Instance)
			.LastOrDefault()?.Id;
	}

	private static string? TryGetRedistPackageVersion(string packageId)
	{
		const string prefix = "Microsoft.VC.";
		const string marker = ".CRT.Redist.";
		if (!packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		int markerIndex = packageId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (markerIndex < 0)
		{
			return null;
		}
		return packageId[prefix.Length..markerIndex];
	}

	private static string? ResolveCompilerResourcePackage(PackageIndex index, string msvcVersion, Architecture host, Architecture target)
	{
		string prefix = $"Microsoft.VC.{msvcVersion}.Tools.Host{host.PackageTitle()}.Target{target.PackageTitle()}";
		string res = prefix + ".Res.base";
		if (index.Contains(res))
		{
			return res;
		}
		string resources = prefix + ".Resources.base";
		return index.Contains(resources) ? resources : null;
	}

	private static IReadOnlyList<string> SelectSdkPayloads(PackageIndex index, string sdkVersion, IReadOnlyList<Architecture> targets, List<PlanIssue> issues)
	{
		PackageInfo? package = index.FindSdkPayloadPackage(sdkVersion);
		if (package is null)
		{
			issues.Add(new PlanIssue("error", "missing-sdk", "Windows SDK " + sdkVersion + " 的 payload 包在 manifest 中未找到。"));
			return Array.Empty<string>();
		}
		HashSet<string> available = package.Payloads.Select(x => x.FileName.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
		SortedSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
		Require("Windows SDK for Windows Store Apps Tools-x86_en-us.msi");
		Require("Windows SDK for Windows Store Apps Headers-x86_en-us.msi");
		Require("Windows SDK for Windows Store Apps Libs-x86_en-us.msi");
		Require("Universal CRT Headers Libraries and Sources-x86_en-us.msi");
		Require("Universal CRT Redistributable-x86_en-us.msi");
		AddIfPresent("Windows SDK for Windows Store Apps Headers OnecoreUap-x86_en-us.msi");

		// Header MSI 体积较小，并且 SDK 布局中有些 include 会跨架构共享；
		// lib 仍然只选择用户请求的目标架构。
		foreach (Architecture architecture in AllArchitectures)
		{
			AddIfPresent("Windows SDK Desktop Headers " + architecture.Cli() + "-x86_en-us.msi");
		}
		foreach (Architecture architecture in AllArchitectures)
		{
			AddIfPresent("Windows SDK OnecoreUap Headers " + architecture.Cli() + "-x86_en-us.msi");
		}
		foreach (Architecture target in targets.Distinct())
		{
			Require("Windows SDK Desktop Libs " + target.Cli() + "-x86_en-us.msi");
		}
		return selected.ToList();
		void AddIfPresent(string fileName)
		{
			string normalized = "Installers\\" + fileName;
			if (available.Contains(normalized))
			{
				selected.Add(normalized);
			}
		}
		void Require(string fileName)
		{
			string normalized = "Installers\\" + fileName;
			if (available.Contains(normalized))
			{
				selected.Add(normalized);
			}
			else
			{
				issues.Add(new PlanIssue("error", "missing-sdk-payload", $"Windows SDK payload '{fileName}' 在 SDK {sdkVersion} 中不存在。"));
			}
		}
	}

	private static IReadOnlyList<PlannedPayload> SelectRuntimeInstallerPayloads(PackageIndex index, IReadOnlyList<Architecture> targets, List<PlanIssue> issues)
	{
		List<PlannedPayload> payloads = new();
		foreach (Architecture target in targets.Distinct())
		{
			string arch = target.Cli();
			PackageInfo? redist = FindRuntimePackage(index, arch, redist: true);
			if (redist == null)
			{
				issues.Add(new PlanIssue("warning", "missing-runtime-installer", "manifest 中没有找到 " + arch + " 的 VC_redist 安装器。"));
			}
			else
			{
				AddRuntimePackage(payloads, arch, redist, payload => payload.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
			}

			PackageInfo? debug = FindRuntimePackage(index, arch, redist: false);
			if (debug == null)
			{
				issues.Add(new PlanIssue("warning", "missing-runtime-debug-installer", "manifest 中没有找到 " + arch + " 的 VC debug runtime 安装器。"));
			}
			else
			{
				AddRuntimePackage(payloads, arch, debug, payload =>
					payload.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
					payload.FileName.EndsWith(".cab", StringComparison.OrdinalIgnoreCase));
			}
		}

		return payloads
			.GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
			.Select(x => x.First())
			.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static PackageInfo? FindRuntimePackage(PackageIndex index, string arch, bool redist)
	{
		IEnumerable<PackageInfo> packages = index.Packages.Where(package => RuntimePackageIdMatches(package.Id, arch, redist));

		return packages
			.Where(package => RuntimePackageMatchesArch(package, arch, redist))
			.OrderBy(package => redist && package.Id.EndsWith(".Latest", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
			.ThenBy(package => package.Version ?? "", NaturalVersionComparer.Instance)
			.LastOrDefault();
	}

	private static bool RuntimePackageIdMatches(string packageId, string arch, bool redist)
	{
		if (redist)
		{
			return packageId.Equals("Microsoft.VisualCpp.Redist.14.Latest", StringComparison.OrdinalIgnoreCase) ||
				packageId.Equals("Microsoft.VisualCpp.Redist.14", StringComparison.OrdinalIgnoreCase);
		}

		if (arch.Equals("arm64", StringComparison.OrdinalIgnoreCase))
		{
			return packageId.Equals("Microsoft.VisualCpp.RuntimeDebug.14.ARM64", StringComparison.OrdinalIgnoreCase);
		}

		if (arch.Equals("arm", StringComparison.OrdinalIgnoreCase))
		{
			return packageId.Equals("Microsoft.VisualCpp.RuntimeDebug.14.ARM", StringComparison.OrdinalIgnoreCase) ||
				packageId.Equals("Microsoft.VisualCpp.RuntimeDebug.14", StringComparison.OrdinalIgnoreCase);
		}

		return packageId.Equals("Microsoft.VisualCpp.RuntimeDebug.14", StringComparison.OrdinalIgnoreCase);
	}

	private static bool RuntimePackageMatchesArch(PackageInfo package, string arch, bool redist)
	{
		bool hasArchitectureMetadata =
			!string.IsNullOrWhiteSpace(package.Chip) ||
			!string.IsNullOrWhiteSpace(package.MachineArch) ||
			!string.IsNullOrWhiteSpace(package.ProductArch);
		bool architectureMatches =
			ArchitectureMetadataMatches(package.Chip, arch) ||
			ArchitectureMetadataMatches(package.MachineArch, arch) ||
			ArchitectureMetadataMatches(package.ProductArch, arch);
		if (hasArchitectureMetadata)
		{
			return architectureMatches;
		}

		string expected = redist ? "VC_redist." + arch + ".exe" : RuntimeDebugMsiFileName(arch);
		return package.Payloads.Any(payload => payload.FileName.Equals(expected, StringComparison.OrdinalIgnoreCase));
	}

	private static string RuntimeDebugMsiFileName(string arch)
	{
		return arch.ToLowerInvariant() switch
		{
			"arm" => "vc_RuntimeDebug_arm.msi",
			"arm64" => "vc_RuntimeDebug_arm64.msi",
			_ => "vc_RuntimeDebug.msi"
		};
	}

	private static bool ArchitectureMetadataMatches(string? value, string arch)
	{
		return value != null && value.Equals(arch, StringComparison.OrdinalIgnoreCase);
	}

	private static void AddRuntimePackage(List<PlannedPayload> payloads, string arch, PackageInfo package, Func<PayloadInfo, bool> include)
	{
		foreach (PayloadInfo payload in package.Payloads.Where(include))
		{
			string fileName = Path.Combine("Runtime", arch, Path.GetFileName(payload.FileName.Replace('\\', Path.DirectorySeparatorChar)));
			payloads.Add(new PlannedPayload(package.Id, fileName, payload.Url, payload.Sha256, payload.Size));
		}
	}
}
