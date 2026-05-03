using System.Text;
using System.Text.Json;

namespace PortableMSVC;

public static class FakeVsWhere
{
	public static int Run(string[] args)
	{
		return Run(args, AppContext.BaseDirectory);
	}

	public static int Run(string[] args, string baseDirectory)
	{
		try
		{
			if (IsHelpCommand(args))
			{
				PrintHelp();
				return 0;
			}

			VsWhereOptions options = VsWhereOptions.Parse(args);
			// baseDirectory 可能是通过 junction 访问的路径（如 Setup.bat 建立的系统 junction）。
			// 尝试解析 junction 为真实物理路径，确保 ".." 导航到便携安装目录。
			// 如果不是 junction 或解析失败，回退到原始路径。
			string resolvedBase = TryResolveDirectory(baseDirectory);
			string visualStudioDirectory = Path.GetFullPath(Path.Combine(resolvedBase, ".."));
			string portableRoot = Path.GetFullPath(Path.Combine(visualStudioDirectory, ".."));
			string packagesDirectory = Path.Combine(visualStudioDirectory, "Packages");
			VsWhereState state = LoadState(packagesDirectory);
			VsWhereInstance? instance = CreateInstance(state, portableRoot);
			if (instance == null || !MatchesRequires(instance, options) || !MatchesVersionRange(instance, options))
			{
				WriteEmpty(options);
				return 0;
			}
			if (options.FindPattern != null)
			{
				WriteFindResults(instance, options);
				return 0;
			}
			if (options.Property != null)
			{
				string? value = GetProperty(instance, options.Property);
				if (value == null)
				{
					return 1;
				}
				Console.WriteLine(value);
				return 0;
			}
			WriteInstances(new[] { instance }, options);
			return 0;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
		{
			WriteException(ex, args);
			return 1;
		}
	}

	private static void WriteException(Exception ex, string[] args)
	{
		Console.Error.WriteLine(args.Any(static arg => arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase)) ? ex.ToString() : ex.Message);
	}

	public static bool IsVsWhereProcess()
	{
		string? processPath = Environment.ProcessPath;
		string name = Path.GetFileNameWithoutExtension(processPath ?? "");
		return name.Equals("vswhere", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsHelpCommand(string[] args)
	{
		return args.Length == 0 || args.Any(arg =>
			arg.Equals("help", StringComparison.OrdinalIgnoreCase) ||
			arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
			arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
			arg.Equals("/?", StringComparison.OrdinalIgnoreCase));
	}

	private static void PrintHelp()
	{
		string exe = Path.GetFileName(Environment.ProcessPath ?? "vswhere.exe");
		Console.WriteLine($"""
Portable MSVC fake vswhere

用法：
  {exe} [参数...]

参数：
  -latest                 返回最新实例（兼容参数）
  -all                    返回所有实例（兼容参数）
  -products <产品>        筛选产品；支持 *（兼容参数）
  -requires <组件>        要求实例包含指定组件，可重复指定
  -requiresAny            任一 -requires 组件匹配即可
  -version <范围>         筛选安装版本，如 17 或 [17.0,18.0)
  -format <格式>          输出格式：json（默认）| text | xml
  -property <属性>        只输出指定属性
  -find <模式>            在 BuildTools 下查找文件，如 **\clang-cl.exe
  -utf8                   使用 UTF-8 输出（兼容参数）
  -nologo                 不输出标题（兼容参数）
  -h, --help, /?, help    显示帮助

支持属性：
  installationPath
  installationVersion
  displayName
  productId
  productPath
  catalog_productLineVersion
  catalog_productDisplayVersion
  catalog_productSemanticVersion
  catalog_buildVersion

便携命令：
  --setup                 注册系统 vswhere junction 和 Windows SDK 注册表
  --clean                 回滚 --setup 创建的 junction 和注册表
  --portable-msvc-probe   输出固定探测标记

示例：
  {exe} -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
  {exe} -products * -format json -utf8
  {exe} -products * -format json -utf8 -find **\clang-cl.exe
  {exe} --setup
  {exe} --clean
""");
	}

	// 将目录路径解析为真实物理路径（穿透 junction/symlink）。
	// 用于处理 Setup.bat 建立的系统 junction，确保 ".." 导航到便携安装目录。
	private static string TryResolveDirectory(string path)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			FileSystemInfo? resolved = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
			return resolved?.FullName ?? fullPath;
		}
		catch
		{
			return path;
		}
	}

	private static VsWhereState LoadState(string packagesDirectory)
	{
		string path = Path.Combine(packagesDirectory, "state.json");
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("portable vswhere metadata 不存在: " + path, path);
		}
		string json = File.ReadAllText(path);
		return JsonSerializer.Deserialize(json, PortableMSVCJsonContext.Default.VsWhereState)
			?? throw new InvalidDataException("portable vswhere metadata 无效: " + path);
	}

	private static VsWhereInstance? CreateInstance(VsWhereState state, string portableRoot)
	{
		if (string.IsNullOrWhiteSpace(state.InstallationPath))
		{
			return null;
		}
		string installationPath = Path.IsPathRooted(state.InstallationPath)
			? Path.GetFullPath(state.InstallationPath)
			: Path.GetFullPath(Path.Combine(portableRoot, state.InstallationPath));
		string productPath = Path.GetFullPath(Path.Combine(installationPath, state.LaunchParams.FileName));
		VsWhereLocalizedResource? en = state.LocalizedResources.FirstOrDefault(x => x.Language.Equals("en-us", StringComparison.OrdinalIgnoreCase));
		VsWhereLocalizedResource? first = state.LocalizedResources.FirstOrDefault();
		return new VsWhereInstance
		{
			InstallationName = state.InstallationName,
			InstallationPath = installationPath,
			InstallationVersion = state.InstallationVersion,
			InstallDate = state.InstallDate,
			DisplayName = en?.Title ?? first?.Title ?? "Visual Studio Build Tools",
			Description = en?.Description ?? first?.Description ?? "",
			ProductId = state.Product.Id,
			ProductPath = productPath,
			Catalog = state.CatalogInfo,
			Packages = state.SelectedPackages
		};
	}

	private static bool MatchesRequires(VsWhereInstance instance, VsWhereOptions options)
	{
		if (options.Requires.Count == 0)
		{
			return true;
		}
		HashSet<string> packageIds = instance.Packages.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return options.RequiresAny
			? options.Requires.Any(packageIds.Contains)
			: options.Requires.All(packageIds.Contains);
	}

	// 解析 vswhere -version 参数支持的版本范围格式：
	//   "17"          精确主版本匹配
	//   "[16.0,18.0)" 区间（[ 闭区间，( 开区间）
	//   "[17.0,"      只有下界
	//   ",18.0)"      只有上界
	private static bool MatchesVersionRange(VsWhereInstance instance, VsWhereOptions options)
	{
		if (options.VersionRange == null)
		{
			return true;
		}
		string range = options.VersionRange.Trim();
		if (!Version.TryParse(instance.InstallationVersion, out Version? instanceVersion))
		{
			return true;
		}

		// 区间格式
		if (range.StartsWith('[') || range.StartsWith('('))
		{
			bool minInclusive = range[0] == '[';
			bool maxInclusive = range[^1] == ']';
			string inner = range[1..^1];
			int comma = inner.IndexOf(',');
			if (comma < 0)
			{
				return true;
			}
			string minStr = inner[..comma].Trim();
			string maxStr = inner[(comma + 1)..].Trim();
			if (!string.IsNullOrEmpty(minStr) && Version.TryParse(minStr, out Version? minVer))
			{
				int cmp = instanceVersion.CompareTo(minVer);
				if (minInclusive ? cmp < 0 : cmp <= 0)
				{
					return false;
				}
			}
			if (!string.IsNullOrEmpty(maxStr) && Version.TryParse(maxStr, out Version? maxVer))
			{
				int cmp = instanceVersion.CompareTo(maxVer);
				if (maxInclusive ? cmp > 0 : cmp >= 0)
				{
					return false;
				}
			}
			return true;
		}

		// 简单版本前缀匹配（如 "17" 匹配 "17.x.x.x"）
		return instance.InstallationVersion.Equals(range, StringComparison.OrdinalIgnoreCase)
			|| instance.InstallationVersion.StartsWith(range + ".", StringComparison.OrdinalIgnoreCase);
	}

	private static void WriteFindResults(VsWhereInstance instance, VsWhereOptions options)
	{
		string[] results = Find(instance.InstallationPath, options.FindPattern!).ToArray();
		if (options.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine(JsonSerializer.Serialize(results, PortableMSVCJsonContext.Default.StringArray));
			return;
		}
		foreach (string result in results)
		{
			Console.WriteLine(result);
		}
	}

	private static IEnumerable<string> Find(string root, string pattern)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}
		string normalized = pattern.Replace('/', '\\');
		if (normalized.StartsWith("**\\", StringComparison.Ordinal))
		{
			string fileName = normalized[3..];
			foreach (string file in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
			{
				yield return file;
			}
			yield break;
		}
		string direct = Path.GetFullPath(Path.Combine(root, normalized));
		if (File.Exists(direct))
		{
			yield return direct;
		}
	}

	private static void WriteInstances(IReadOnlyList<VsWhereInstance> instances, VsWhereOptions options)
	{
		if (options.Format.Equals("text", StringComparison.OrdinalIgnoreCase))
		{
			WriteText(instances);
			return;
		}

		if (options.Format.Equals("xml", StringComparison.OrdinalIgnoreCase))
		{
			WriteXml(instances);
			return;
		}
		Console.WriteLine(JsonSerializer.Serialize(instances, PortableMSVCJsonContext.Default.IReadOnlyListVsWhereInstance));
	}

	private static void WriteText(IReadOnlyList<VsWhereInstance> instances)
	{
		for (int i = 0; i < instances.Count; i++)
		{
			VsWhereInstance instance = instances[i];
			if (i > 0)
			{
				Console.WriteLine();
			}

			WriteTextProperty("instanceId", instance.InstanceId);
			WriteTextProperty("installDate", instance.InstallDate);
			WriteTextProperty("installationName", instance.InstallationName);
			WriteTextProperty("installationPath", instance.InstallationPath);
			WriteTextProperty("installationVersion", instance.InstallationVersion);
			WriteTextProperty("productId", instance.ProductId);
			WriteTextProperty("productPath", instance.ProductPath);
			WriteTextProperty("isComplete", BoolInt(instance.IsComplete));
			WriteTextProperty("isLaunchable", BoolInt(instance.IsLaunchable));
			WriteTextProperty("isPrerelease", BoolInt(instance.IsPrerelease));
			WriteTextProperty("isRebootRequired", BoolInt(instance.IsRebootRequired));
			WriteTextProperty("displayName", instance.DisplayName);
			WriteTextProperty("description", instance.Description);
			WriteTextProperty("catalog_buildVersion", instance.Catalog.BuildVersion);
			WriteTextProperty("catalog_id", instance.Catalog.Id);
			WriteTextProperty("catalog_productDisplayVersion", instance.Catalog.ProductDisplayVersion);
			WriteTextProperty("catalog_productLine", instance.Catalog.ProductLine);
			WriteTextProperty("catalog_productLineVersion", instance.Catalog.ProductLineVersion);
			WriteTextProperty("catalog_productName", instance.Catalog.ProductName);
			WriteTextProperty("catalog_productSemanticVersion", instance.Catalog.ProductSemanticVersion);
		}
	}

	private static void WriteTextProperty(string name, string value)
	{
		Console.WriteLine(name + ": " + value);
	}

	private static void WriteXml(IReadOnlyList<VsWhereInstance> instances)
	{
		StringBuilder builder = new StringBuilder();
		builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
		builder.AppendLine("<instances>");
		foreach (VsWhereInstance instance in instances)
		{
			builder.AppendLine("  <instance>");
			WriteElement(builder, "instanceId", instance.InstanceId);
			WriteElement(builder, "installationName", instance.InstallationName);
			WriteElement(builder, "installationPath", instance.InstallationPath);
			WriteElement(builder, "installationVersion", instance.InstallationVersion);
			WriteElement(builder, "productId", instance.ProductId);
			WriteElement(builder, "productPath", instance.ProductPath);
			WriteElement(builder, "isComplete", BoolInt(instance.IsComplete));
			WriteElement(builder, "isLaunchable", BoolInt(instance.IsLaunchable));
			WriteElement(builder, "isPrerelease", BoolInt(instance.IsPrerelease));
			WriteElement(builder, "isRebootRequired", BoolInt(instance.IsRebootRequired));
			WriteElement(builder, "displayName", instance.DisplayName);
			WriteElement(builder, "description", instance.Description);
			builder.AppendLine("    <catalog>");
			WriteElement(builder, "buildVersion", instance.Catalog.BuildVersion, 6);
			WriteElement(builder, "id", instance.Catalog.Id, 6);
			WriteElement(builder, "productDisplayVersion", instance.Catalog.ProductDisplayVersion, 6);
			WriteElement(builder, "productLine", instance.Catalog.ProductLine, 6);
			WriteElement(builder, "productLineVersion", instance.Catalog.ProductLineVersion, 6);
			WriteElement(builder, "productName", instance.Catalog.ProductName, 6);
			WriteElement(builder, "productSemanticVersion", instance.Catalog.ProductSemanticVersion, 6);
			builder.AppendLine("    </catalog>");
			builder.AppendLine("  </instance>");
		}
		builder.AppendLine("</instances>");
		Console.Write(builder.ToString());
	}

	private static void WriteElement(StringBuilder builder, string name, string value, int indent = 4)
	{
		builder.Append(' ', indent).Append('<').Append(name).Append('>')
			.Append(System.Security.SecurityElement.Escape(value))
			.Append("</").Append(name).AppendLine(">");
	}

	private static string BoolInt(bool value)
	{
		return value ? "1" : "0";
	}

	private static string? GetProperty(VsWhereInstance instance, string property)
	{
		return property switch
		{
			"installationPath" => instance.InstallationPath,
			"installationVersion" => instance.InstallationVersion,
			"displayName" => instance.DisplayName,
			"productId" => instance.ProductId,
			"productPath" => instance.ProductPath,
			"catalog_productLineVersion" => instance.Catalog.ProductLineVersion,
			"catalog_productDisplayVersion" => instance.Catalog.ProductDisplayVersion,
			"catalog_productSemanticVersion" => instance.Catalog.ProductSemanticVersion,
			"catalog_buildVersion" => instance.Catalog.BuildVersion,
			_ => null,
		};
	}

	private static void WriteEmpty(VsWhereOptions options)
	{
		if (options.Format.Equals("text", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		if (options.Format.Equals("xml", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<instances>\r\n</instances>");
		}
		else
		{
			Console.WriteLine("[]");
		}
	}

	private sealed class VsWhereOptions
	{
		public string Format { get; private set; } = "json";

		public string? Property { get; private set; }

		public string? FindPattern { get; private set; }

		public bool RequiresAny { get; private set; }

		public List<string> Requires { get; } = new List<string>();

		// 版本范围过滤，格式如 "[16.0,18.0)" 或 "17.0"，null 表示不过滤。
		public string? VersionRange { get; private set; }

		public static VsWhereOptions Parse(string[] args)
		{
			VsWhereOptions options = new VsWhereOptions();
			for (int i = 0; i < args.Length; i++)
			{
				string arg = args[i];
				if (arg.Equals("-format", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					options.Format = args[++i];
				}
				else if (arg.Equals("-property", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					options.Property = args[++i];
				}
				else if (arg.Equals("-find", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					options.FindPattern = args[++i];
				}
				else if (arg.Equals("-requires", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					options.Requires.Add(args[++i]);
				}
				else if (arg.Equals("-requiresAny", StringComparison.OrdinalIgnoreCase))
				{
					options.RequiresAny = true;
				}
				else if (arg.Equals("-version", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					options.VersionRange = args[++i];
				}
				else if ((arg.Equals("-products", StringComparison.OrdinalIgnoreCase) ||
				          arg.Equals("-latest", StringComparison.OrdinalIgnoreCase) ||
				          arg.Equals("-all", StringComparison.OrdinalIgnoreCase) ||
				          arg.Equals("-prerelease", StringComparison.OrdinalIgnoreCase) ||
				          arg.Equals("-legacy", StringComparison.OrdinalIgnoreCase) ||
				          arg.Equals("-nologo", StringComparison.OrdinalIgnoreCase) ||
				          arg.Equals("-utf8", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
				{
					i++;
				}
			}
			return options;
		}
	}
}
