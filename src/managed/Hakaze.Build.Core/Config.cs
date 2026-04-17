using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class Config(ConfigId id, ImmutableDictionary<string, Property> properties) : IConfig
{
    public ConfigId Id { get; } = id;

    public ImmutableDictionary<string, Property> Properties { get; } = properties;
}
