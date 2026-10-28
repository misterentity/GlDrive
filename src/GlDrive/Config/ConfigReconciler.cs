using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GlDrive.Config;

/// <summary>Publishes persisted settings while preserving references held by live consumers.</summary>
internal static class ConfigReconciler
{
    internal static void Apply(object destination, object source)
    {
        if (ReferenceEquals(destination, source)) return;
        foreach (var property in source.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite
                     && p.GetCustomAttribute<JsonIgnoreAttribute>() == null))
        {
            var value = property.GetValue(source);
            var existing = property.GetValue(destination);
            if (property.PropertyType == typeof(IrcAttentionConfig)) property.SetValue(destination, value);
            else if (value is IList sourceList && existing is IList targetList && !property.PropertyType.IsArray)
            {
                if (SameContents(sourceList, targetList)) continue;
                var servers = targetList.OfType<ServerConfig>().ToDictionary(s => s.Id);
                var replacements = (IList)Activator.CreateInstance(value.GetType())!;
                foreach (var item in sourceList)
                {
                    if (item is ServerConfig server && servers.TryGetValue(server.Id, out var retained))
                    {
                        Apply(retained, server);
                        replacements.Add(retained);
                    }
                    else replacements.Add(item);
                }
                // Background operations may still enumerate the previous list.
                // Publish a replacement rather than invalidating their enumerator.
                property.SetValue(destination, replacements);
            }
            else if (value is IDictionary sourceDictionary && existing is IDictionary targetDictionary)
            {
                if (!SameContents(sourceDictionary, targetDictionary))
                    property.SetValue(destination, value);
            }
            else if (value != null && existing != null && property.PropertyType.Namespace == typeof(AppConfig).Namespace
                     && !property.PropertyType.IsEnum)
                Apply(existing, value);
            else property.SetValue(destination, value);
        }
    }

    private static bool SameContents(object first, object second)
        => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(first, first.GetType()),
            JsonSerializer.SerializeToNode(second, second.GetType()));
}
