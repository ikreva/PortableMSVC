
namespace PortableMSVC;

public sealed record PlanRequest(string Vs, string? VcVersion, string? SdkVersion, string? RedistVersion, Architecture Host, IReadOnlyList<Architecture> Targets, bool WithRuntime)
{
	public static bool IsSupportedHost(Architecture host)
	{
		return host != Architecture.Arm;
	}

	public static void ThrowIfUnsupportedHost(Architecture host)
	{
		if (!IsSupportedHost(host))
		{
			throw new ArgumentException(UnsupportedHostMessage(host));
		}
	}

	public static string UnsupportedHostMessage(Architecture host)
	{
		return $"--host {host.Cli()} 不受支持。MSVC host 架构只支持 x64、x86、arm64；arm 表示 ARM32，只能作为 --target 使用。";
	}
}
