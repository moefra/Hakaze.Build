using System.Collections.Immutable;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Core;

namespace Hakaze.Build.Core.Tests;

public class EvaluatedBuildExtensionsTests
{
    [Test]
    public async Task Execute_RunsDependenciesBeforeTarget()
    {
        var order = new List<string>();
        var config = new ConfigBuilder().Build();
        var projectId = new ProjectId("/workspace/app");
        var prepareId = new TargetId(projectId, null, new TargetName("Prepare"), new TargetSource("prepare"));
        var buildId = new TargetId(projectId, null, new TargetName("Build"), new TargetSource("build"));

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(new RecordingTarget(prepareId, [], (_, _, _) =>
                      {
                          order.Add("prepare");
                          return new SuccessfulExecution("prepared");
                      }))
                      .AddTarget(new RecordingTarget(buildId, [prepareId], (_, results, _) =>
                      {
                          order.Add("build");
                          if (!results.ContainsKey(prepareId))
                          {
                              throw new InvalidOperationException("Missing prepare result.");
                          }

                          return new SuccessfulExecution("built");
                      }))
                      .Build();

        var result = await building.Execute(buildId);

        await Assert.That(result).IsTypeOf<SuccessfulExecution>();
        await Assert.That(order).IsEquivalentTo(["prepare", "build"]);
    }

    [Test]
    public async Task Execute_RunsSharedDependencyOnlyOnce()
    {
        var count = 0;
        var config = new ConfigBuilder().Build();
        var projectId = new ProjectId("/workspace/app");
        var prepareId = new TargetId(projectId, null, new TargetName("Prepare"), new TargetSource("prepare"));
        var leftId = new TargetId(projectId, null, new TargetName("Left"), new TargetSource("left"));
        var rightId = new TargetId(projectId, null, new TargetName("Right"), new TargetSource("right"));
        var rootId = new TargetId(projectId, null, new TargetName("Root"), new TargetSource("root"));

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(new RecordingTarget(prepareId, [], (_, _, _) =>
                      {
                          count++;
                          return new SuccessfulExecutionWithResult<string>("prepared", "prepare-value");
                      }))
                      .AddTarget(new RecordingTarget(leftId, [prepareId], (_, results, _) =>
                          results[prepareId] as string == "prepare-value"
                              ? new SuccessfulExecution("left")
                              : throw new InvalidOperationException("Left target did not receive shared dependency result.")))
                      .AddTarget(new RecordingTarget(rightId, [prepareId], (_, results, _) =>
                          results[prepareId] as string == "prepare-value"
                              ? new SuccessfulExecution("right")
                              : throw new InvalidOperationException("Right target did not receive shared dependency result.")))
                      .AddTarget(new RecordingTarget(rootId, [leftId, rightId], (_, results, _) =>
                          results.ContainsKey(prepareId)
                              ? new SuccessfulExecution("root")
                              : throw new InvalidOperationException("Root target did not receive transitive dependency result.")))
                      .Build();

        _ = await building.Execute(rootId);

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Execute_ReturnsDependencyFailureWithoutRunningTarget()
    {
        var targetRan = false;
        var config = new ConfigBuilder().Build();
        var projectId = new ProjectId("/workspace/app");
        var prepareId = new TargetId(projectId, null, new TargetName("Prepare"), new TargetSource("prepare"));
        var buildId = new TargetId(projectId, null, new TargetName("Build"), new TargetSource("build"));

        var failure = new FailedExecution("prepare failed", null);
        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(new RecordingTarget(prepareId, [], (_, _, _) => failure))
                      .AddTarget(new RecordingTarget(buildId, [prepareId], (_, _, _) =>
                      {
                          targetRan = true;
                          return new SuccessfulExecution("built");
                      }))
                      .Build();

        var result = await building.Execute(buildId);

        await Assert.That(result).IsSameReferenceAs(failure);
        await Assert.That(targetRan).IsFalse();
    }

    [Test]
    public async Task Execute_PassesNullForSuccessfulDependencyWithoutValue()
    {
        object? dependencyValue = "unset";
        var config = new ConfigBuilder().Build();
        var projectId = new ProjectId("/workspace/app");
        var prepareId = new TargetId(projectId, null, new TargetName("Prepare"), new TargetSource("prepare"));
        var buildId = new TargetId(projectId, null, new TargetName("Build"), new TargetSource("build"));

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(new RecordingTarget(prepareId, [], (_, _, _) => new SuccessfulExecution("prepared")))
                      .AddTarget(new RecordingTarget(buildId, [prepareId], (_, results, _) =>
                      {
                          dependencyValue = results[prepareId];
                          return new SuccessfulExecution("built");
                      }))
                      .Build();

        _ = await building.Execute(buildId);

        await Assert.That(dependencyValue).IsNull();
    }

    [Test]
    public async Task Execute_ThrowsWhenTargetIsMissing()
    {
        var config = new ConfigBuilder().Build();
        var missingId = new TargetId(
            new ProjectId("/workspace/app"),
            null,
            new TargetName("Missing"),
            new TargetSource("missing"));
        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .Build();

        Action action = () => _ = building.Execute(missingId).GetAwaiter().GetResult();

        await Assert.That(action).ThrowsException()
            .WithMessage($"Target '{missingId}' was not found.");
    }

    [Test]
    public async Task Execute_ThrowsWhenDependencyCycleExists()
    {
        var config = new ConfigBuilder().Build();
        var projectId = new ProjectId("/workspace/app");
        var firstId = new TargetId(projectId, null, new TargetName("First"), new TargetSource("first"));
        var secondId = new TargetId(projectId, null, new TargetName("Second"), new TargetSource("second"));

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(new RecordingTarget(firstId, [secondId], (_, _, _) => new SuccessfulExecution("first")))
                      .AddTarget(new RecordingTarget(secondId, [firstId], (_, _, _) => new SuccessfulExecution("second")))
                      .Build();

        Action action = () => _ = building.Execute(firstId).GetAwaiter().GetResult();

        await Assert.That(action).ThrowsException()
            .WithMessage($"Cyclic target dependency detected for '{firstId}'.");
    }

    [Test]
    public async Task Execute_WorksWhenTargetIdsUseNullProjectAndSource()
    {
        var config = new ConfigBuilder().Build();
        var prepareId = new TargetId(null, null, new TargetName("Prepare"), null);
        var buildId = new TargetId(null, null, new TargetName("Build"), null);

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(new RecordingTarget(prepareId, [], (_, _, _) =>
                          new SuccessfulExecutionWithResult<string>("prepared", "prepare-value")))
                      .AddTarget(new RecordingTarget(buildId, [prepareId], (_, results, _) =>
                          results[prepareId] as string == "prepare-value"
                              ? new SuccessfulExecution("built")
                              : throw new InvalidOperationException("Missing dependency value.")))
                      .Build();

        var result = await building.Execute(buildId);

        await Assert.That(result).IsTypeOf<SuccessfulExecution>();
    }

    [Test]
    public async Task Execute_WorksWithTargetsBuiltFromTargetBuilder()
    {
        var config = new ConfigBuilder().Build();
        var prepareId = new TargetId(null, null, new TargetName("Prepare"), null);
        var buildId = new TargetId(null, null, new TargetName("Build"), null);

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(new ProfileBuilder().WithName("Debug").Build())
                      .WithConfig(config)
                      .AddTarget(target => target
                          .WithId(prepareId)
                          .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(
                              new SuccessfulExecutionWithResult<string>("prepared", "prepare-value"))))
                      .AddTarget(target => target
                          .WithId(buildId)
                          .AddRequiredPreparation(prepareId)
                          .WithExecute((_, results, _) =>
                              Task.FromResult<ExecutionResult>(
                                  results[prepareId] as string == "prepare-value"
                                      ? new SuccessfulExecution("built")
                                      : throw new InvalidOperationException("Missing dependency value."))))
                      .Build();

        var result = await building.Execute(buildId);

        await Assert.That(result).IsTypeOf<SuccessfulExecution>();
    }

    private sealed class RecordingTarget(
        TargetId id,
        ImmutableArray<TargetId> requiredPreparation,
        Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, ExecutionResult> execute) : ITarget
    {
        public RecordingTarget(
            TargetId id,
            IEnumerable<TargetId> requiredPreparation,
            Func<IEvaluatedBuilding, ImmutableDictionary<TargetId, object?>, CancellationToken, ExecutionResult> execute)
            : this(id, requiredPreparation.ToImmutableArray(), execute)
        {
        }

        public TargetId Id { get; } = id;

        public ImmutableArray<TargetId> RequiredPreparation { get; } = requiredPreparation;

        public Task<ExecutionResult> ExecuteAsync(
            IEvaluatedBuilding building,
            ImmutableDictionary<TargetId, object?> collectedExecutionResults,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(execute(building, collectedExecutionResults, cancellationToken));
        }
    }
}
