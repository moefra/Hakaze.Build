using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class BuildingBuilder
{
    private Profile? _profile;
    private IConfig? _config;
    private readonly List<IProject> _projects = [];

    public BuildingBuilder WithProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        return this;
    }

    public BuildingBuilder WithProfile(Action<ProfileBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ProfileBuilder();
        configure(builder);
        return WithProfile(builder.Build());
    }

    public BuildingBuilder WithConfig(IConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        return this;
    }

    public BuildingBuilder WithConfig(Action<ConfigBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfigBuilder();
        configure(builder);
        return WithConfig(builder.Build());
    }

    public BuildingBuilder AddProject(IProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _projects.Add(project);
        return this;
    }

    public BuildingBuilder AddProject(Action<ProjectBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ProjectBuilder();
        configure(builder);
        return AddProject(builder.Build());
    }

    public Building Build()
    {
        var projects = _projects.ToImmutableArray();
        ValidateProjects(projects);
        return new Building(RequireProfile(), RequireConfig(), projects);
    }

    private Profile RequireProfile()
    {
        return _profile ?? throw new InvalidOperationException("Profile is required.");
    }

    private IConfig RequireConfig()
    {
        return _config ?? throw new InvalidOperationException("Config is required.");
    }

    private static void ValidateProjects(ImmutableArray<IProject> projects)
    {
        var ids = new HashSet<ProjectId>();
        foreach (var project in projects)
        {
            if (!ids.Add(project.Id))
            {
                throw new InvalidOperationException($"Duplicate project id '{project.Id}' is not allowed.");
            }
        }
    }
}
