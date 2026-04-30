using System;
using System.IO;
using System.IO.Compression;

namespace PortableMSVC;

public static class VsixExtractor
{
	public static void ExtractContents(string vsixPath, string outputDirectory)
	{
		using ZipArchive archive = ZipFile.OpenRead(vsixPath);
		string root = EnsureTrailingSeparator(Path.GetFullPath(outputDirectory));
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			if (entry.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith('/'))
			{
				string relative = entry.FullName["Contents/".Length..].Replace('/', Path.DirectorySeparatorChar);
				string destination = Path.GetFullPath(Path.Combine(outputDirectory, relative));
				if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("ZIP 条目路径非法（路径遍历）: " + entry.FullName);
				}
				string? destinationDirectory = Path.GetDirectoryName(destination);
				if (destinationDirectory != null)
				{
					Directory.CreateDirectory(destinationDirectory);
				}
				entry.ExtractToFile(destination, overwrite: true);
			}
		}
	}

	private static string EnsureTrailingSeparator(string path)
	{
		return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
	}
}
