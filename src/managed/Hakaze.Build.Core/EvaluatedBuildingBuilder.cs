using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class EvaluatedBuildingBuilder
{
    private Profile? _profile;
    private IConfig? _config;
    private readonly List<IProject> _projects = [];
    private readonly List<ITarget> _targets = [];

    public EvaluatedBuildingBuilder WithProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        return this;
    }

    public EvaluatedBuildingBuilder WithProfile(Action<ProfileBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ProfileBuilder();
        configure(builder);
        return WithProfile(builder.Build());
    }

    public EvaluatedBuildingBuilder WithConfig(IConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        return this;
    }

    public EvaluatedBuildingBuilder WithConfig(Action<ConfigBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfigBuilder();
        configure(builder);
        return WithConfig(builder.Build());
    }

    public EvaluatedBuildingBuilder AddProject(IProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _projects.Add(project);
        return this;
    }

    public EvaluatedBuildingBuilder AddProject(Action<ProjectBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ProjectBuilder();
        configure(builder);
        return AddProject(builder.Build());
    }

    public EvaluatedBuildingBuilder AddTarget(ITarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targets.Add(target);
        return this;
    }

    public EvaluatedBuilding Build()
    {
        var projects = _projects.ToImmutableArray();
        ValidateProjects(projects);

        var targets = BuildTargets();

        return new EvaluatedBuilding(RequireProfile(), RequireConfig(), projects, targets);
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

    private ImmutableDictionary<TargetId, ITarget> BuildTargets()
    {
        var builder = ImmutableDictionary.CreateBuilder<TargetId, ITarget>();
        foreach (var target in _targets)
        {
            if (!builder.TryAdd(target.Id, target))
            {
                throw new InvalidOperationException("Duplicate target ids are not allowed.");
            }
        }

        return builder.ToImmutable();
    }
}
