using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PortableMSVC;

public sealed partial class PackageIndex
{
	private readonly Dictionary<string, List<PackageInfo>> _packages;

	public string VsAlias { get; }

	public string ProductVersion { get; }

	public string BuildVersion { get; }

	public string ProductSemanticVersion { get; }

	public string ProductLine { get; }

	public string ProductLineVersion { get; }

	public string ProductInfoId { get; }

	public IReadOnlyList<PackageInfo> Packages { get; }

	public PackageIndex(string vsAlias, string productVersion, string buildVersion, string productSemanticVersion, string productLine, string productLineVersion, string productInfoId, IReadOnlyList<PackageInfo> packages)
	{
		VsAlias = vsAlias;
		ProductVersion = productVersion;
		BuildVersion = buildVersion;
		ProductSemanticVersion = productSemanticVersion;
		ProductLine = productLine;
		ProductLineVersion = productLineVersion;
		ProductInfoId = productInfoId;
		Packages = packages;
		_packages = packages
			.GroupBy(x => x.IdLower, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
	}

	public PackageInfo? Find(string id)
	{
		if (!_packages.TryGetValue(id.ToLowerInvariant(), out List<PackageInfo>? packages))
		{
			return null;
		}

		// manifest 存在多语言重复包时，优先选择 neutral/en-US 包。
		return packages.FirstOrDefault(x => x.Language is null or "en-US" or "neutral")
			?? packages.FirstOrDefault(x => x.Language == null)
			?? packages[0];
	}

	public bool Contains(string id)
	{
		return _packages.ContainsKey(id.ToLowerInvariant());
	}

	public IReadOnlyList<PackageInfo> ResolveDependencyClosure(IEnumerable<string> rootIds, Func<PackageInfo, bool>? expandDependencies = null)
	{
		List<PackageInfo> result = new();
		HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
		Stack<string> stack = new(rootIds.Reverse());
		expandDependencies ??= _ => true;
		while (stack.Count > 0)
		{
			string id = stack.Pop();
			if (!visited.Add(id))
			{
				continue;
			}
			PackageInfo? package = Find(id);
			if (package is not null)
			{
				result.Add(package);
				if (expandDependencies(package))
				{
					for (int i = package.Dependencies.Count - 1; i >= 0; i--)
					{
						stack.Push(package.Dependencies[i]);
					}
				}
			}
		}
		return result;
	}

	public IReadOnlyList<string> GetMsvcVersions()
	{
		return (from x in Packages
			select MsvcToolsBaseRegex().Match(x.Id) into x
			where x.Success
			select x.Groups["version"].Value into x
			where !x.Contains("Preview", StringComparison.OrdinalIgnoreCase) && !x.Contains("Premium", StringComparison.OrdinalIgnoreCase)
			select x).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, NaturalVersionComparer.Instance).ToList();
	}

	public IReadOnlyList<string> GetRedistVersions()
	{
		return (from x in Packages
			select RedistBaseRegex().Match(x.Id) into x
			where x.Success
			select x.Groups["version"].Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, NaturalVersionComparer.Instance).ToList();
	}

	public IReadOnlyList<string> GetSdkVersions()
	{
		return (from x in Packages
			select SdkRegex().Match(x.Id) into x
			where x.Success
			select x.Groups["version"].Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, NaturalVersionComparer.Instance).ToList();
	}

	public ResolvedVersion ResolveMsvcVersion(string? requested)
	{
		return ResolveVersion("MSVC", GetMsvcVersions(), requested);
	}

	public ResolvedVersion ResolveRedistVersion(string? requested, string? vcVersion)
	{
		IReadOnlyList<string> redistVersions = GetRedistVersions();
		if (redistVersions.Count == 0)
		{
			throw new InvalidOperationException("manifest 中没有找到任何 redist 版本。");
		}
		if (!string.IsNullOrWhiteSpace(requested))
		{
			return ResolveVersion("redist", redistVersions, requested);
		}
		if (string.IsNullOrWhiteSpace(vcVersion) || vcVersion.Equals("latest", StringComparison.OrdinalIgnoreCase))
		{
			string latest = redistVersions[redistVersions.Count - 1];
			return new ResolvedVersion(latest, latest);
		}
		ResolvedVersion vcResolved = ResolveVersion("MSVC", GetMsvcVersions(), vcVersion);
		string vcFull = vcResolved.FullVersion;
		string? candidate = redistVersions.Where(r => NaturalVersionComparer.Instance.Compare(r, vcFull) <= 0).LastOrDefault();
		if (candidate == null)
		{
			throw new InvalidOperationException($"没有找到不高于 MSVC {vcFull} 的 redist 版本。可用 redist 版本：{string.Join(", ", redistVersions)}");
		}
		return new ResolvedVersion(candidate, candidate);
	}

	public ResolvedVersion ResolveSdkVersion(string? requested)
	{
		IReadOnlyList<string> versions = GetSdkVersions();
		string? alias = requested;
		if (string.IsNullOrWhiteSpace(alias))
		{
			alias = versions.LastOrDefault()?.Split('.').Last();
		}
		return ResolveVersion("Windows SDK", versions, alias);
	}

	public PackageInfo? FindSdkPayloadPackage(string fullSdkVersion)
	{
		PackageInfo? sdkPackage = Find("Win10SDK_" + fullSdkVersion) ?? Find("Win11SDK_" + fullSdkVersion);
		if (sdkPackage is null)
		{
			string? shortVersion = fullSdkVersion.Split('.').LastOrDefault();
			if (shortVersion != null)
			{
				sdkPackage = Find("Win10SDK_10.0." + shortVersion) ?? Find("Win11SDK_10.0." + shortVersion);
			}
		}
		if (sdkPackage is null)
		{
			return null;
		}
		foreach (string dependency in sdkPackage.Dependencies)
		{
			PackageInfo? package = Find(dependency);
			if (package is not null && package.Payloads.Count > 0)
			{
				return package;
			}
		}
		return (sdkPackage.Payloads.Count > 0) ? sdkPackage : null;
	}

	private static ResolvedVersion ResolveVersion(string kind, IReadOnlyList<string> versions, string? requested)
	{
		if (versions.Count == 0)
		{
			throw new InvalidOperationException("manifest 中没有找到任何 " + kind + " 版本。");
		}
		if (string.IsNullOrWhiteSpace(requested) || requested.Equals("latest", StringComparison.OrdinalIgnoreCase))
		{
			string latest = versions[versions.Count - 1];
			return new ResolvedVersion(latest, latest);
		}
		string normalized = requested.Trim();

		// 版本字符串格式不固定：14.44、14.44.17.14、26100 都要能自然匹配完整版本。
		List<string> matches = versions
			.Where(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase)
				|| x.StartsWith(normalized + ".", StringComparison.OrdinalIgnoreCase)
				|| x.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase)
				|| x.Split('.').Last().Equals(normalized, StringComparison.OrdinalIgnoreCase))
			.OrderBy(x => x, NaturalVersionComparer.Instance)
			.ToList();
		if (matches.Count == 0)
		{
			throw new ArgumentException($"{kind} 版本 '{requested}' 未找到。可用版本：{string.Join(", ", versions)}");
		}
		return new ResolvedVersion(normalized, matches[matches.Count - 1]);
	}

	[GeneratedRegex("^Microsoft\\.VC\\.(?<version>.+)\\.Tools\\.HostX64\\.TargetX64\\.base$", RegexOptions.IgnoreCase)]
	private static partial Regex MsvcToolsBaseRegex();

	[GeneratedRegex("^Microsoft\\.VC\\.(?<version>.+)\\.CRT\\.Redist\\.(?:x86|x64|arm64|arm)(?:\\.onecore\\.desktop)?\\.base$", RegexOptions.IgnoreCase)]
	private static partial Regex RedistBaseRegex();

	[GeneratedRegex("^Win(?:10|11)SDK_(?<version>10\\.0\\.\\d+)$", RegexOptions.IgnoreCase)]
	private static partial Regex SdkRegex();
}
