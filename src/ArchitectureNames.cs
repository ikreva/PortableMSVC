using System;

namespace PortableMSVC;

public static class ArchitectureNames
{
	public static Architecture Parse(string value)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"x86" => Architecture.X86,
			"x64" => Architecture.X64,
			"arm" => Architecture.Arm,
			"arm64" => Architecture.Arm64,
			_ => throw new ArgumentException("未知架构 '" + value + "'。"),
		};
	}

	public static string Cli(this Architecture architecture)
	{
		return architecture switch
		{
			Architecture.X86 => "x86",
			Architecture.X64 => "x64",
			Architecture.Arm => "arm",
			Architecture.Arm64 => "arm64",
			_ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
		};
	}

	public static string Package(this Architecture architecture)
	{
		return architecture switch
		{
			Architecture.X86 => "x86",
			Architecture.X64 => "x64",
			Architecture.Arm => "arm",
			Architecture.Arm64 => "arm64",
			_ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
		};
	}

	public static string PackageTitle(this Architecture architecture)
	{
		return architecture switch
		{
			Architecture.X86 => "X86",
			Architecture.X64 => "X64",
			Architecture.Arm => "ARM",
			Architecture.Arm64 => "ARM64",
			_ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
		};
	}
}
