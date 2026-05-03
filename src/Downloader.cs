using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PortableMSVC;

public sealed class Downloader
{
	private static readonly Lock ConsoleLock = new();

	private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks = new(StringComparer.OrdinalIgnoreCase);

	private readonly HttpClient _httpClient;

	public Downloader()
		: this(SharedHttpClient.Instance)
	{
	}

	public Downloader(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<DownloadedFile> DownloadAsync(PlannedPayload payload, string cacheDirectory, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(cacheDirectory);
		string baseName = Path.GetFileName(payload.FileName.Replace('\\', Path.DirectorySeparatorChar));
		string prefix = string.IsNullOrWhiteSpace(payload.Sha256) ? Sanitize(payload.PackageId) : payload.Sha256[..Math.Min(12, payload.Sha256.Length)];
		string fileName = prefix + "_" + baseName;
		string destination = Path.Combine(cacheDirectory, fileName);
		SemaphoreSlim downloadLock = DownloadLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
		await downloadLock.WaitAsync(cancellationToken);
		try
		{
			if (File.Exists(destination) && await VerifySha256Async(destination, payload.Sha256, cancellationToken))
			{
				LogStatus("缓存", baseName, "OK");
				return new DownloadedFile(destination, baseName);
			}

			// 缓存文件名带 SHA 前缀，让不同 manifest 版本的 payload 可以共存，
			// 也避免误用旧版本留下的同名文件。
			await using (Stream input = await _httpClient.GetStreamAsync(payload.Url, cancellationToken))
			{
				await using FileStream output = File.Create(destination);
				await input.CopyToAsync(output, cancellationToken);
			}
			if (!(await VerifySha256Async(destination, payload.Sha256, cancellationToken)))
			{
				DeleteInvalidDownload(destination);
				throw new InvalidDataException("SHA-256 校验失败: " + fileName);
			}
			LogStatus("下载", baseName, "OK");
			return new DownloadedFile(destination, baseName);
		}
		finally
		{
			downloadLock.Release();
		}
	}

	public async Task<DownloadedFile> DownloadSdkPayloadAsync(PayloadInfo payload, string cacheDirectory, CancellationToken cancellationToken)
	{
		PlannedPayload planned = new PlannedPayload("WindowsSDK", payload.FileName, payload.Url, payload.Sha256, payload.Size);
		return await DownloadAsync(planned, cacheDirectory, cancellationToken);
	}

	public static void LogStatus(string action, string fileName, string status)
	{
		lock (ConsoleLock)
		{
			// 中文 action（下载/缓存/解包）显示宽度为 4，补一个空格对齐
			string paddedAction = $"{action} ";
			Console.Write($"{paddedAction,-6}{fileName,-72} ");
			if (status.Equals("OK", StringComparison.OrdinalIgnoreCase))
			{
				ConsoleColor previous = Console.ForegroundColor;
				Console.ForegroundColor = ConsoleColor.Green;
				Console.Write("[OK]");
				Console.ForegroundColor = previous;
				Console.WriteLine();
			}
			else
			{
				Console.WriteLine("[" + status + "]");
			}
		}
	}

	private static async Task<bool> VerifySha256Async(string path, string? expected, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(expected))
		{
			return true;
		}

		await using FileStream stream = File.OpenRead(path);
		string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
		return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
	}

	private static string Sanitize(string value)
	{
		char[] invalidChars = Path.GetInvalidFileNameChars();
		// 用 span 一次性扫描，只在有非法字符时才分配新字符串。
		if (value.AsSpan().IndexOfAny(invalidChars) < 0)
		{
			return value;
		}
		HashSet<char> invalidSet = new(invalidChars);
		return string.Create(value.Length, (value, invalidSet), static (span, state) =>
		{
			for (int i = 0; i < state.value.Length; i++)
			{
				span[i] = state.invalidSet.Contains(state.value[i]) ? '_' : state.value[i];
			}
		});
	}

	private static void DeleteInvalidDownload(string path)
	{
		File.Delete(path);
	}
}
