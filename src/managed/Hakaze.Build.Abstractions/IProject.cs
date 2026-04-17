using System.Collections.Immutable;

namespace Hakaze.Build.Abstractions;

/// <summary>
/// Project is the container of targets and project-specified properties.
/// </summary>
public interface IProject
{
    ProjectId Id { get; }

    ImmutableDictionary<string,Property> Properties { get; }
}
