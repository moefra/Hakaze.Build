using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class ProjectBuilder
{
    private ProjectId? _id;
    private readonly Dictionary<string, Property> _properties = [];

    public ProjectBuilder WithId(ProjectId id)
    {
        _id = id;
        return this;
    }

    public ProjectBuilder SetProperty(string key, Property value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _properties[key] = value;
        return this;
    }

    public Project Build()
    {
        return new Project(RequireId(), _properties.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private ProjectId RequireId()
    {
        if (_id is not { } id)
        {
            throw new InvalidOperationException("Project id is required.");
        }

        return id;
    }
}
