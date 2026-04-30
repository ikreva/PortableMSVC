using System.Text.Json.Serialization;

namespace PortableMSVC;

public sealed class VsWherePackage
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = "";

	[JsonPropertyName("version")]
	public string Version { get; set; } = "";

	[JsonPropertyName("type")]
	public string Type { get; set; } = "";

	[JsonPropertyName("installed")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? Installed { get; set; }
}
