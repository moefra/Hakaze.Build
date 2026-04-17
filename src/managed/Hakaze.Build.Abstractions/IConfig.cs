using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;
/// <summary>
/// Configuration is the container of properties.
/// It can apply to any project,so it don't belong to any project.
/// </summary>
public interface IConfig
{
    ConfigId Id { get; }

    ImmutableDictionary<string,Property> Properties { get; }
}
