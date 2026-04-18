using System.Collections.Immutable;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Core;

namespace Hakaze.Build.Core.Tests;

public class EvaluatedBuildingBuilderTests
{
    [Test]
    public async Task EvaluatedBuildingBuilder_BuildsGraphWithTargets()
    {
        var config = new ConfigBuilder()
                    .SetProperty("runtime", new StringProperty("native"))
                    .Build();
        var projectId = new ProjectId("/workspace/app");
        var target = new TestTarget(
            new TargetId(projectId, null, new TargetName("Compile"), new TargetSource("Program.cs")));

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(static profile => profile.WithName("Debug"))
                      .WithConfig(config)
                      .AddProject(project => project
                                          .WithId(projectId)
                                          .SetProperty("lang", new StringProperty("csharp")))
                      .AddTarget(target)
                      .Build();

        await Assert.That(building.Profile.Name).IsEqualTo("Debug");
        await Assert.That(building.Config.Id).IsEqualTo(config.Id);
        await Assert.That(building.Projects.Length).IsEqualTo(1);
        await Assert.That(building.Targets.Count).IsEqualTo(1);
        await Assert.That(building.Targets[target.Id]).IsSameReferenceAs(target);
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_Build_ThrowsWhenProfileMissing()
    {
        var builder = new EvaluatedBuildingBuilder()
                     .WithConfig(new ConfigBuilder().Build());

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Profile is required.");
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_Build_ThrowsWhenConfigMissing()
    {
        var builder = new EvaluatedBuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build());

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Config is required.");
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_Build_ThrowsWhenProjectIdsRepeat()
    {
        var sharedId = new ProjectId("/workspace/app");

        var builder = new EvaluatedBuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                     .WithConfig(new ConfigBuilder().Build())
                     .AddProject(new ProjectBuilder().WithId(sharedId).Build())
                     .AddProject(new ProjectBuilder().WithId(sharedId).Build());

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage($"Duplicate project id '{sharedId}' is not allowed.");
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_Build_ThrowsWhenTargetIdsRepeat()
    {
        var config = new ConfigBuilder().Build();
        var projectId = new ProjectId("/workspace/app");
        var targetId = new TargetId(projectId, null, new TargetName("Compile"), new TargetSource("Program.cs"));

        var builder = new EvaluatedBuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                     .WithConfig(config)
                     .AddTarget(new TestTarget(targetId))
                     .AddTarget(new TestTarget(targetId));

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Duplicate target ids are not allowed.");
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_AddTarget_WithBuilder_AddsBuiltTarget()
    {
        var config = new ConfigBuilder().Build();
        var targetId = new TargetId(null, null, new TargetName("Compile"), null);

        var building = new EvaluatedBuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                     .WithConfig(config)
                     .AddTarget(target => target
                         .WithId(targetId)
                         .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"))))
                     .Build();

        await Assert.That(building.Targets.Count).IsEqualTo(1);
        await Assert.That(building.Targets.ContainsKey(targetId)).IsTrue();
        await Assert.That(building.Targets[targetId]).IsTypeOf<Target>();
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_AddTarget_WithBuilder_StillRejectsDuplicateIds()
    {
        var config = new ConfigBuilder().Build();
        var targetId = new TargetId(null, null, new TargetName("Compile"), null);

        var builder = new EvaluatedBuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                     .WithConfig(config)
                     .AddTarget(target => target
                         .WithId(targetId)
                         .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("first"))))
                     .AddTarget(target => target
                         .WithId(targetId)
                         .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("second"))));

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Duplicate target ids are not allowed.");
    }

    [Test]
    public async Task EvaluatedBuildingBuilder_Build_AllowsNullProjectConfigAndSourceInTargetId()
    {
        var config = new ConfigBuilder().Build();
        var targetId = new TargetId(null, null, new TargetName("Compile"), null);
        var target = new TestTarget(targetId);

        var building = new EvaluatedBuildingBuilder()
                     .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                     .WithConfig(config)
                     .AddTarget(target)
                     .Build();

        await Assert.That(building.Targets[targetId]).IsSameReferenceAs(target);
    }

    private sealed class TestTarget(TargetId id) : ITarget
    {
        public TargetId Id { get; } = id;

        public ImmutableArray<TargetId> RequiredPreparation { get; } = [];

        public Task<ExecutionResult> ExecuteAsync(
            IEvaluatedBuilding building,
            ImmutableDictionary<TargetId, object?> collectedExecutionResults,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
        }
    }
}
