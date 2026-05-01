
namespace PortableMSVC;

public sealed class ManifestMeta
{
	public string VsAlias { get; set; } = "";

	public string ChannelUrl { get; set; } = "";

	public string VsmanUrl { get; set; } = "";

	public string BuildVersion { get; set; } = "";

	public DateTime CachedAtUtc { get; set; }
}
