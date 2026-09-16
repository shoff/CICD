using System.Text.Json;
using System.Text.Json.Serialization;
using Cicd.Plugins.Sdk;

namespace Cicd.Core.Plugins;

public static class PluginManifestReader
{
    public const string FileName = "plugin.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new SidesConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginManifest Read(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(stream, Options)
            ?? throw new InvalidDataException($"Plugin manifest '{path}' is empty.");
        Validate(manifest, path);
        return manifest;
    }

    public static PluginManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, Options)
            ?? throw new InvalidDataException("Plugin manifest is empty.");
        Validate(manifest, "<string>");
        return manifest;
    }

    private static void Validate(PluginManifest manifest, string source)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.' or '_')))
        {
            throw new InvalidDataException($"Plugin manifest '{source}' has an invalid id '{manifest.Id}'. Use letters, digits, '-', '.' or '_'.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            throw new InvalidDataException($"Plugin manifest '{source}' does not name an assembly.");
        }
        if (manifest.Sides == PluginSide.None)
        {
            throw new InvalidDataException($"Plugin manifest '{source}' must declare at least one side.");
        }
    }

    /// <summary>Accepts "sides": ["server", "agent"] or "sides": "both".</summary>
    private sealed class SidesConverter : JsonConverter<PluginSide>
    {
        public override PluginSide Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return ParseOne(reader.GetString()!);
            }
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Expected a string or array for 'sides'.");
            }
            var result = PluginSide.None;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                result |= ParseOne(reader.GetString() ?? "");
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, PluginSide value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (value.HasFlag(PluginSide.Server)) writer.WriteStringValue("server");
            if (value.HasFlag(PluginSide.Agent)) writer.WriteStringValue("agent");
            writer.WriteEndArray();
        }

        private static PluginSide ParseOne(string value) => value.ToLowerInvariant() switch
        {
            "server" => PluginSide.Server,
            "agent" => PluginSide.Agent,
            "both" => PluginSide.Both,
            _ => throw new JsonException($"Unknown plugin side '{value}'."),
        };
    }
}
