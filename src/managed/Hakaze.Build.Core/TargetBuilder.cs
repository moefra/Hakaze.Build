using System.Collections.Immutable;
using Hakaze.Build.Abstractions;

namespace Hakaze.Build.Core;

public sealed class TargetBuilder
{
    private ProjectId? _projectId;
    private ConfigId? _configId;
    private TargetName? _name;
    private TargetSource? _source;
    private readonly List<TargetId> _requiredPreparation = [];
    private Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, Task<ExecutionResult>>? _execute;

    public TargetBuilder WithId(TargetId id)
    {
        _projectId = id.ProjectId;
        _configId = id.ConfigId;
        _name = id.Name;
        _source = id.Source;
        return this;
    }

    public TargetBuilder WithProjectId(ProjectId? projectId)
    {
        _projectId = projectId;
        return this;
    }

    public TargetBuilder WithConfigId(ConfigId? configId)
    {
        _configId = configId;
        return this;
    }

    public TargetBuilder WithName(TargetName name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name.Name);
        _name = name;
        return this;
    }

    public TargetBuilder WithSource(TargetSource? source)
    {
        _source = source;
        return this;
    }

    public TargetBuilder WithRequiredPreparation(IEnumerable<TargetId> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _requiredPreparation.Clear();
        _requiredPreparation.AddRange(dependencies);
        return this;
    }

    public TargetBuilder AddRequiredPreparation(TargetId dependency)
    {
        _requiredPreparation.Add(dependency);
        return this;
    }

    public TargetBuilder WithExecute(
        Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, Task<ExecutionResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        return this;
    }

    public Target Build()
    {
        return new Target(
            new TargetId(_projectId, _configId, RequireName(), _source),
            _requiredPreparation.ToImmutableArray(),
            RequireExecute());
    }

    private TargetName RequireName()
    {
        if (_name is not { } name || string.IsNullOrWhiteSpace(name.Name))
        {
            throw new InvalidOperationException("Target name is required.");
        }

        return name;
    }

    private Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, Task<ExecutionResult>> RequireExecute()
    {
        return _execute ?? throw new InvalidOperationException("Target execute delegate is required.");
    }
}
