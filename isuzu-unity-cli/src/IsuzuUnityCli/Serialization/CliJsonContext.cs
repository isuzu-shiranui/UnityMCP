using System.Text.Json.Serialization;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstanceDescriptor))]
[JsonSerializable(typeof(List<ProjectRow>))]
public sealed partial class CliJsonContext : JsonSerializerContext
{
}
