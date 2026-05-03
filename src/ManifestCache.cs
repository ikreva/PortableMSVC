using System.Text.Json;

namespace PortableMSVC;

public sealed class ManifestCache
{
	// 只使用官方 stable/release channel；preview channel 的包形态可能不符合当前便携解包规则。
	private static readonly Dictionary<string, (string ChannelUrl, string ManifestItemId)> ChannelMap = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
	{
		["latest"] = ("https://aka.ms/vs/stable/channel", "Microsoft.VisualStudio.Manifests.VisualStudio"),
		["2026"] = ("https://aka.ms/vs/18/stable/channel", "Microsoft.VisualStudio.Manifests.VisualStudio"),
		["2022"] = ("https://aka.ms/vs/17/release/channel", "Microsoft.VisualStudio.Manifests.VisualStudio"),
		["2019"] = ("https://aka.ms/vs/16/release/channel", "Microsoft.VisualStudio.Manifests.VisualStudio")
	};

	private readonly string _cacheDirectory;

	private readonly HttpClient _http;

	public static string DefaultCacheDirectory => Path.Combine(AppContext.BaseDirectory, "Cache", "manifests");

	public ManifestCache(string cacheDirectory)
		: this(cacheDirectory, SharedHttpClient.Instance)
	{
	}

	public ManifestCache(string cacheDirectory, HttpClient httpClient)
	{
		_cacheDirectory = cacheDirectory;
		_http = httpClient;
	}

	public async Task<string> EnsureAsync(string vsAlias, bool forceRefresh, CancellationToken ct)
	{
		if (!ChannelMap.TryGetValue(vsAlias, out var channel))
		{
			throw new ArgumentException("不支持的 VS 版本别名 '" + vsAlias + "'。支持的值：" + string.Join(", ", ChannelMap.Keys));
		}
		Directory.CreateDirectory(_cacheDirectory);
		string vsmanPath = Path.Combine(_cacheDirectory, vsAlias + ".vsman.json");
		string metaPath = Path.Combine(_cacheDirectory, vsAlias + ".meta.json");
		ManifestMeta? localMeta = LoadMeta(metaPath);
		if (!forceRefresh && localMeta != null && File.Exists(vsmanPath))
		{
			Console.Error.WriteLine("检查 " + vsAlias + " 是否有更新...");
			string newVsmanUrl = await FetchVsmanUrlAsync(channel.ChannelUrl, channel.ManifestItemId, ct);
			if (newVsmanUrl == localMeta.VsmanUrl)
			{
				Console.Error.WriteLine($"缓存中 {vsAlias} 版本已是最新（{localMeta.BuildVersion}），使用本地缓存。");
				return vsmanPath;
			}
			Console.Error.WriteLine($"{vsAlias} 有新版本，正在更新（当前：{localMeta.BuildVersion}）...");
			await DownloadVsmanAsync(vsAlias, newVsmanUrl, vsmanPath, ct);
			string newBuildVersion = ReadBuildVersionFromFile(vsmanPath);
			SaveMeta(metaPath, new ManifestMeta
			{
				VsAlias = vsAlias,
				ChannelUrl = channel.ChannelUrl,
				VsmanUrl = newVsmanUrl,
				BuildVersion = newBuildVersion,
				CachedAtUtc = DateTime.UtcNow
			});
			Console.Error.WriteLine($"{vsAlias} 已更新到 {newBuildVersion}。");
		}
		else
		{
			if (forceRefresh)
			{
				Console.Error.WriteLine("强制刷新 " + vsAlias + "...");
			}
			else
			{
				Console.Error.WriteLine("本地没有 " + vsAlias + " 的缓存，正在下载...");
			}
			string vsmanUrl = await FetchVsmanUrlAsync(channel.ChannelUrl, channel.ManifestItemId, ct);
			await DownloadVsmanAsync(vsAlias, vsmanUrl, vsmanPath, ct);
			string buildVersion = ReadBuildVersionFromFile(vsmanPath);
			SaveMeta(metaPath, new ManifestMeta
			{
				VsAlias = vsAlias,
				ChannelUrl = channel.ChannelUrl,
				VsmanUrl = vsmanUrl,
				BuildVersion = buildVersion,
				CachedAtUtc = DateTime.UtcNow
			});
			Console.Error.WriteLine($"{vsAlias} 下载完成（{buildVersion}）。");
		}
		return vsmanPath;
	}

	public void PrintStatus()
	{
		Console.WriteLine("缓存目录: " + _cacheDirectory);
		Console.WriteLine();
		foreach (string alias in ChannelMap.Keys)
		{
			string vsmanPath = Path.Combine(_cacheDirectory, alias + ".vsman.json");
			string metaPath = Path.Combine(_cacheDirectory, alias + ".meta.json");
			ManifestMeta? meta = LoadMeta(metaPath);
			if (meta == null || !File.Exists(vsmanPath))
			{
				Console.WriteLine($"  {alias,-8}  (未缓存)");
				continue;
			}
			TimeSpan age = DateTime.UtcNow - meta.CachedAtUtc;
			string ageStr = ((age.TotalDays >= 1.0) ? $"{(int)age.TotalDays} 天前" : ((age.TotalHours >= 1.0) ? $"{(int)age.TotalHours} 小时前" : $"{(int)age.TotalMinutes} 分钟前"));
			Console.WriteLine($"  {alias,-8}  {meta.BuildVersion,-30}  缓存于 {ageStr}");
		}
	}

	private async Task<string> FetchVsmanUrlAsync(string channelUrl, string manifestItemId, CancellationToken ct)
	{
		using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(channelUrl, ct));
		if (!doc.RootElement.TryGetProperty("channelItems", out var items))
		{
			throw new InvalidDataException("channel JSON 缺少 channelItems 字段: " + channelUrl);
		}
		foreach (JsonElement item in items.EnumerateArray())
		{
			if (item.TryGetProperty("id", out var idProp) && (idProp.GetString()?.Equals(manifestItemId, StringComparison.OrdinalIgnoreCase) ?? false))
			{
				if (item.TryGetProperty("payloads", out var payloads) && payloads.GetArrayLength() > 0 && payloads[0].TryGetProperty("url", out var urlProp))
				{
					string? vsmanUrl = urlProp.GetString();
					if (!string.IsNullOrEmpty(vsmanUrl))
					{
						return vsmanUrl;
					}
				}
			}
		}
		throw new InvalidDataException("channel JSON 中未找到 manifest item '" + manifestItemId + "': " + channelUrl);
	}

	private async Task DownloadVsmanAsync(string vsAlias, string vsmanUrl, string vsmanPath, CancellationToken ct)
	{
		Console.Error.WriteLine("正在下载 " + vsAlias + " manifest...");
		await File.WriteAllBytesAsync(vsmanPath, await _http.GetByteArrayAsync(vsmanUrl, ct), ct);
	}

	private static string ReadBuildVersionFromFile(string vsmanPath)
	{
		try
		{
			using FileStream stream = File.OpenRead(vsmanPath);
			using JsonDocument doc = JsonDocument.Parse(stream);
			JsonElement pdv;
			JsonElement bv;
			if (doc.RootElement.TryGetProperty("info", out var info))
			{
				return (info.TryGetProperty("productDisplayVersion", out pdv) ? pdv.GetString() : null) ?? (info.TryGetProperty("buildVersion", out bv) ? bv.GetString() : null) ?? "unknown";
			}
		}
		catch
		{
		}
		return "unknown";
	}

	private static ManifestMeta? LoadMeta(string metaPath)
	{
		if (!File.Exists(metaPath))
		{
			return null;
		}
		try
		{
			string json = File.ReadAllText(metaPath);
			return JsonSerializer.Deserialize(json, PortableMSVCJsonContext.Default.ManifestMeta);
		}
		catch
		{
			return null;
		}
	}

	private static void SaveMeta(string metaPath, ManifestMeta meta)
	{
		string json = JsonSerializer.Serialize(meta, PortableMSVCJsonContext.Default.ManifestMeta);
		File.WriteAllText(metaPath, json);
	}
}
