using System.Text.Json;

namespace PortableMSVC;

public sealed class ManifestLoader
{
	public static readonly string[] KnownVsAliases = ["2019", "2022", "2026", "latest"];

	private readonly string _manifestDirectory;

	public ManifestLoader(string manifestDirectory)
	{
		_manifestDirectory = manifestDirectory;
	}

	public PackageIndex LoadVsManifest(string vs)
	{
		string normalized = NormalizeVs(vs);
		string path = Path.Combine(_manifestDirectory, normalized + ".vsman.json");
		return LoadFromFile(normalized, path);
	}

	public static PackageIndex LoadFromFile(string vsAlias, string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException($"Manifest 文件不存在: {path}\n请运行 'cache refresh --vs {vsAlias}' 下载 manifest，或使用 '--cache <目录>' 指定包含 manifest 文件的目录。", path);
		}
		using FileStream stream = File.OpenRead(path);
		using JsonDocument document = JsonDocument.Parse(stream);
		JsonElement root = document.RootElement;
		JsonElement info = root.GetProperty("info");
		string productVersion = TryGetString(info, "productDisplayVersion") ?? TryGetString(root.GetProperty("info"), "buildVersion") ?? vsAlias;
		string buildVersion = TryGetString(info, "buildVersion") ?? productVersion;
		string productSemanticVersion = TryGetString(info, "productSemanticVersion") ?? buildVersion;
		// 一些旧版/离线 manifest 缺少产品线字段，因此这里保留稳定回退值，
		// 用于生成 metadata 和命令提示符标题。
		string fallbackProductLineVersion = NormalizeVs(vsAlias) switch
		{
			"2026" => "18",
			"2022" => "17",
			"2019" => "16",
			_ => vsAlias,
		};
		string productLine = TryGetString(info, "productLine") ?? ("Dev" + fallbackProductLineVersion);
		string productLineVersion = TryGetString(info, "productLineVersion") ?? productVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fallbackProductLineVersion;
		string productInfoId = TryGetString(info, "id") ?? "VisualStudio";
		List<PackageInfo> packages = new List<PackageInfo>();
		foreach (JsonElement packageElement in root.GetProperty("packages").EnumerateArray())
		{
			string id = packageElement.GetProperty("id").GetString() ?? throw new InvalidDataException("包条目缺少 id 字段。");
			IReadOnlyList<string> dependencies = ReadDependencies(packageElement);
			IReadOnlyList<PayloadInfo> payloads = ReadPayloads(packageElement);
			packages.Add(new PackageInfo(
				id,
				id.ToLowerInvariant(),
				TryGetString(packageElement, "version"),
				TryGetString(packageElement, "language"),
				dependencies,
				payloads,
				TryGetString(packageElement, "type"),
				TryGetString(packageElement, "chip"),
				TryGetString(packageElement, "machineArch"),
				TryGetString(packageElement, "productArch")));
		}
		return new PackageIndex(vsAlias, productVersion, buildVersion, productSemanticVersion, productLine, productLineVersion, productInfoId, packages);
	}

	public static string NormalizeVs(string value)
	{
		string normalized = value.Trim().ToLowerInvariant();
		return normalized switch
		{
			"17" => "2022",
			"18" => "2026",
			"16" => "2019",
			"2019" or "2022" or "2026" or "latest" => normalized,
			_ => throw new ArgumentException("不支持的 --vs 值 '" + value + "'。支持的值：latest, 2026, 2022, 2019。"),
		};
	}

	private static IReadOnlyList<string> ReadDependencies(JsonElement element)
	{
		// manifest 中 dependencies 是 JSON 对象，键为依赖 ID（或带后缀的唯一键），
		// 值为版本约束字符串或含 "id" 字段的对象。
		// 当值为对象且含 "id" 字段时，以 "id" 字段为准（键可能带数字后缀）。
		if (!element.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
		{
			return Array.Empty<string>();
		}
		List<string> values = new List<string>();
		foreach (JsonProperty prop in deps.EnumerateObject())
		{
			string id;
			if (prop.Value.ValueKind == JsonValueKind.Object &&
				prop.Value.TryGetProperty("id", out var idProp) &&
				idProp.ValueKind == JsonValueKind.String)
			{
				id = idProp.GetString() ?? prop.Name;
			}
			else
			{
				id = prop.Name;
			}
			if (!string.IsNullOrEmpty(id))
			{
				values.Add(id);
			}
		}
		return values;
	}

	private static IReadOnlyList<PayloadInfo> ReadPayloads(JsonElement element)
	{
		if (!element.TryGetProperty("payloads", out var array) || array.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<PayloadInfo>();
		}
		List<PayloadInfo> payloads = new List<PayloadInfo>();
		foreach (JsonElement payload in array.EnumerateArray())
		{
			string? fileName = TryGetString(payload, "fileName") ?? TryGetString(payload, "id");
			string? url = TryGetString(payload, "url");
			if (fileName != null && url != null)
			{
				payloads.Add(new PayloadInfo(fileName, url, TryGetString(payload, "sha256"), TryGetInt64(payload, "size")));
			}
		}
		return payloads;
	}

	private static string? TryGetString(JsonElement element, string propertyName)
	{
		JsonElement property;
		return (element.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.String) ? property.GetString() : null;
	}

	private static long? TryGetInt64(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.Number when property.TryGetInt64(out var value) => value,
			JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
			_ => null,
		};
	}
}
