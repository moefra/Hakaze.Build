using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class ConfigBuilder
{
    private readonly Dictionary<string, Property> _properties = [];

    public ConfigBuilder SetProperty(string key, Property value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _properties[key] = value;
        return this;
    }

    public Config Build()
    {
        var properties = _properties.ToImmutableDictionary(StringComparer.Ordinal);
        return new Config(ConfigId.FromProperties(properties), properties);
    }
}
