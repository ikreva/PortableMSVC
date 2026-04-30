using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortableMSVC;

public static class Cli
{
	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
			{
				PrintHelp();
				return 0;
			}
			string command = args[0].ToLowerInvariant();
			Dictionary<string, List<string>> options = ParseOptions(args.Skip(1).ToArray());
			switch (command)
			{
			case "list":
				ValidateKnownOptions(options, "--vs", "--cache");
				return await ListAsync(options);
			case "plan":
				ValidateKnownOptions(options, "--vs", "--vc", "--sdk", "--redist", "--host", "--target", "--cache", "--with-runtime");
				return await PlanAsync(options, writeJson: true);
			case "install":
				ValidateKnownOptions(options, "--vs", "--vc", "--sdk", "--redist", "--host", "--target", "--output", "--cache", "--download-cache", "--dry-run", "--copy-runtime-dlls", "--with-runtime");
				if (HasOption(options, "--dry-run"))
				{
					return await PlanAsync(options, writeJson: true);
				}
				return await InstallAsync(options);
			case "cache":
				ValidateKnownOptions(options, "--vs", "--cache", "--force");
				return await CacheAsync(options);
			default:
				Console.Error.WriteLine("未知命令 '" + command + "'。");
				PrintHelp();
				return 2;
			}
		}
		catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or InvalidOperationException)
		{
			await Console.Error.WriteLineAsync(ex.Message);
			return 2;
		}
	}

	private static async Task<int> ListAsync(Dictionary<string, List<string>> options)
	{
		IReadOnlyList<string> vsValues = GetValues(options, "--vs");
		string vs = ((vsValues.Count == 0) ? "latest" : vsValues[0]);
		string normalized = ManifestLoader.NormalizeVs(vs);
		PackageIndex index = await LoadManifestAsync(normalized, options, CancellationToken.None);

		string vsDisplayName = index.VsAlias switch
		{
			"latest" => $"latest ({ProductLineVersionToYear(index.ProductLineVersion)})",
			_ => index.VsAlias
		};
		Console.WriteLine($"Visual Studio {vsDisplayName}  {index.ProductVersion}");
		Console.WriteLine();
		PrintVersionList("MSVC 工具集版本", index.GetMsvcVersions());
		Console.WriteLine();
		PrintVersionList("Windows SDK 版本", index.GetSdkVersions());
		Console.WriteLine();
		PrintVersionList("MSVC Redist 版本", index.GetRedistVersions());
		return 0;
	}

	private static void PrintVersionList(string title, IEnumerable<string> versions)
	{
		IReadOnlyList<string> list = versions as IReadOnlyList<string> ?? versions.ToList();
		Console.WriteLine(title);
		Console.WriteLine(new string('-', 18));
		if (list.Count == 0)
		{
			Console.WriteLine("  (无)");
			return;
		}
		// 横向排列，每行最多 4 个，最新版本标注 (latest)
		const int columns = 4;
		for (int i = 0; i < list.Count; i += columns)
		{
			var sb = new System.Text.StringBuilder("  ");
			for (int j = i; j < Math.Min(i + columns, list.Count); j++)
			{
				string ver = list[j];
				bool isLatest = j == list.Count - 1;
				string label = isLatest ? $"{ver} (latest)" : ver;
				sb.Append($"{label,-26}");
			}
			Console.WriteLine(sb.ToString().TrimEnd());
		}
	}

	private static async Task<int> PlanAsync(Dictionary<string, List<string>> options, bool writeJson)
	{
		PlanRequest request = BuildRequest(options);
		PackageIndex index = await LoadManifestAsync(request.Vs, options, CancellationToken.None);
		InstallPlan plan = new PlanBuilder().Build(index, request);
		if (writeJson)
		{
			string json = JsonSerializer.Serialize(plan, PortableMSVCJsonContext.Default.InstallPlan);
			Console.WriteLine(json);
		}
		else
		{
			Console.WriteLine($"{(plan.Success ? "PASS" : "FAIL")} {plan.Vs} vc={plan.MsvcVersion} sdk={plan.SdkVersion} redist={plan.RedistVersion} host={plan.Host} target={string.Join(",", plan.Targets)}");
		}
		foreach (PlanIssue issue in plan.Issues)
		{
			TextWriter output = (issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? Console.Error : Console.Out);
			output.WriteLine($"{IssueSeverityLabel(issue.Severity)}  {issue.Message}");
			if (!string.IsNullOrWhiteSpace(issue.Suggestion))
			{
				output.WriteLine("建议: " + issue.Suggestion);
			}
		}
		return plan.Success ? 0 : 1;
	}

	private static async Task<int> InstallAsync(Dictionary<string, List<string>> options)
	{
		PlanRequest request = BuildRequest(options);
		PackageIndex index = await LoadManifestAsync(request.Vs, options, CancellationToken.None);
		InstallPlan plan = new PlanBuilder().Build(index, request);
		if (!plan.Success)
		{
			foreach (PlanIssue issue in plan.Issues)
			{
				Console.Error.WriteLine($"{IssueSeverityLabel(issue.Severity)}  {issue.Message}");
				if (!string.IsNullOrWhiteSpace(issue.Suggestion))
				{
					Console.Error.WriteLine("建议: " + issue.Suggestion);
				}
			}
			return 1;
		}
		string output = GetOption(options, "--output") ?? Path.Combine(AppContext.BaseDirectory, "MSVC");
		string downloadCache = GetOption(options, "--download-cache") ?? Path.Combine(AppContext.BaseDirectory, "Cache", "Downloads");
		bool copyRuntimeDlls = HasOption(options, "--copy-runtime-dlls");
		await new InstallRunner().RunAsync(index, plan, output, downloadCache, copyRuntimeDlls, CancellationToken.None);
		Console.WriteLine("安装完成: " + Path.GetFullPath(output));
		return 0;
	}

	private static async Task<int> CacheAsync(Dictionary<string, List<string>> options)
	{
		IReadOnlyList<string> positional = GetValues(options, "");
		string subCommand = ((positional.Count > 0) ? positional[0].ToLowerInvariant() : "status");
		string cacheDir = GetOption(options, "--cache") ?? ManifestCache.DefaultCacheDirectory;
		ManifestCache manifestCache = new ManifestCache(cacheDir);

		if (subCommand == "status")
		{
			manifestCache.PrintStatus();
			return 0;
		}

		if (subCommand == "refresh")
		{
			IReadOnlyList<string> vsValues = GetValues(options, "--vs");
			IReadOnlyList<string> aliases = vsValues.Count == 0
				? new[] { "latest", "2026", "2022", "2019" }
				: vsValues.Select(ManifestLoader.NormalizeVs).Distinct().ToList();
			bool force = HasOption(options, "--force");
			foreach (string alias in aliases)
			{
				await manifestCache.EnsureAsync(alias, force, CancellationToken.None);
			}
			Console.WriteLine("所有缓存已更新。");
			return 0;
		}

		Console.Error.WriteLine("未知 cache 子命令 '" + subCommand + "'。支持：status, refresh");
		return 2;
	}

	private static async Task<PackageIndex> LoadManifestAsync(string vsAlias, Dictionary<string, List<string>> options, CancellationToken ct)
	{
		string? explicitCache = GetOption(options, "--cache");
		if (explicitCache != null)
		{
			string directPath = Path.Combine(explicitCache, vsAlias + ".vsman.json");
			return ManifestLoader.LoadFromFile(vsAlias, directPath);
		}
		ManifestCache cache = new ManifestCache(ManifestCache.DefaultCacheDirectory);
		return ManifestLoader.LoadFromFile(vsAlias, await cache.EnsureAsync(vsAlias, forceRefresh: false, ct));
	}

	private static PlanRequest BuildRequest(Dictionary<string, List<string>> options)
	{
		string vs = GetOption(options, "--vs") ?? "latest";
		Architecture host = ArchitectureNames.Parse(GetOption(options, "--host") ?? "x64");
		if (host == Architecture.Arm)
		{
			throw new ArgumentException("--host arm 不受支持。MSVC host 架构只支持 x64、x86、arm64；arm 表示 ARM32，只能作为 --target 使用。");
		}
		IReadOnlyList<string> targetValues = GetValues(options, "--target");
		List<Architecture> targets = targetValues.Count == 0
			? new List<Architecture> { Architecture.X64 }
			: targetValues.SelectMany((string x) => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Select(ArchitectureNames.Parse).Distinct()
				.ToList();
		return new PlanRequest(ManifestLoader.NormalizeVs(vs), GetOption(options, "--vc"), GetOption(options, "--sdk"), GetOption(options, "--redist"), host, targets, HasOption(options, "--with-runtime"));
	}

	private static Dictionary<string, List<string>> ParseOptions(string[] args)
	{
		Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		result[""] = new List<string>();
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			if (!arg.StartsWith("--", StringComparison.Ordinal))
			{
				result[""].Add(arg);
				continue;
			}
			if (!result.TryGetValue(arg, out var values))
			{
				values = (result[arg] = new List<string>());
			}
			while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
			{
				values.Add(args[++i]);
			}
		}
		return result;
	}

	private static void ValidateKnownOptions(Dictionary<string, List<string>> options, params string[] knownOptions)
	{
		HashSet<string> known = new HashSet<string>(knownOptions, StringComparer.OrdinalIgnoreCase);
		foreach (string option in options.Keys)
		{
			if (option.Length > 0 && !known.Contains(option))
			{
				throw new ArgumentException("未知参数 '" + option + "'。");
			}
		}
	}

	private static string? GetOption(Dictionary<string, List<string>> options, string name)
	{
		List<string>? values;
		return (options.TryGetValue(name, out values) && values.Count > 0) ? values[0] : null;
	}

	private static IReadOnlyList<string> GetValues(Dictionary<string, List<string>> options, string name)
	{
		List<string>? values;
		return options.TryGetValue(name, out values) ? values : new List<string>();
	}

	private static bool HasOption(Dictionary<string, List<string>> options, string name)
	{
		return options.ContainsKey(name);
	}

	private static string IssueSeverityLabel(string severity)
	{
		return severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "错误" : severity.ToUpperInvariant();
	}

	private static string ProductLineVersionToYear(string productLineVersion)
	{
		return productLineVersion switch
		{
			"18" => "2026",
			"17" => "2022",
			"16" => "2019",
			_ => productLineVersion
		};
	}

	private static void PrintHelp()
	{
		string exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "PortableMSVC");
		Console.WriteLine($"""
Portable MSVC 工具链提取器

用法：
  {exe} <命令> [参数...]

命令：
  list     列出可用的 MSVC / SDK 版本
  plan     生成安装计划（不下载，输出 JSON）
  install  下载并安装工具链
  cache    管理本地 manifest 缓存

参数：
  --vs <版本>           Visual Studio 版本：latest（默认）| 2026 | 2022 | 2019
  --vc <版本>           MSVC 工具版本，如 14.44、14.50（默认：最新）
  --sdk <版本>          Windows SDK 版本，如 26100、22621（默认：最新）
  --redist <版本>       MSVC redist 版本（默认：跟随 --vc）
  --host <架构>         编译器 host 架构：x64（默认）| x86 | arm64
  --target <架构>       编译目标架构：x64（默认）| x86 | arm | arm64
                        可指定多个，空格或逗号分隔：--target x64 x86
  --output <目录>       安装输出目录（默认：exe 同目录下的 MSVC\）
  --cache <目录>        指定本地 manifest 缓存目录，跳过联网检查
  --copy-runtime-dlls   复制运行/调试 DLL 到编译器 bin 目录
  --with-runtime        下载 VC runtime / debug runtime 官方安装包
  --dry-run             仅生成安装计划，不执行下载和安装

架构支持：
  VS 版本       host                 target
  2019          x86, x64             x86, x64, arm, arm64
  2022          x86, x64, arm64      x86, x64, arm, arm64
  2026/latest   x86, x64, arm64      x86, x64, arm64

  注：arm 表示 ARM32，只能作为 target，不支持作为 host。

示例：
  {exe} list
  {exe} list --vs 2022
  {exe} plan --vs 2022 --vc 14.44 --sdk 26100 --target x64 x86
  {exe} install --vs 2022 --vc 14.44 --sdk 26100 --target x64 x86
  {exe} install --vs 2022 --target x64 --copy-runtime-dlls
  {exe} install --vs 2022 --target x64 x86 --with-runtime
  {exe} install --dry-run --vs latest --target x64
  {exe} cache status
  {exe} cache refresh
  {exe} cache refresh --vs 2022 --force
""");
	}
}
