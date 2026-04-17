using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class Project(ProjectId id, ImmutableDictionary<string, Property> properties) : IProject
{
    public ProjectId Id { get; } = id;

    public ImmutableDictionary<string, Property> Properties { get; } = properties;
}
