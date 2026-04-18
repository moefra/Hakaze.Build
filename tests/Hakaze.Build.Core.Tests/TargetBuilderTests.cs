using System.Collections.Immutable;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Core;

namespace Hakaze.Build.Core.Tests;

public class TargetBuilderTests
{
    [Test]
    public async Task TargetBuilder_Builds_Target_WithExplicitId()
    {
        var dependencyId = new TargetId(
            new ProjectId("/workspace/app"),
            null,
            new TargetName("Prepare"),
            new TargetSource("prepare"));
        var targetId = new TargetId(
            new ProjectId("/workspace/app"),
            null,
            new TargetName("Build"),
            new TargetSource("build"));

        var target = new TargetBuilder()
                    .WithId(targetId)
                    .AddRequiredPreparation(dependencyId)
                    .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("built")))
                    .Build();

        await Assert.That(target.Id).IsEqualTo(targetId);
        await Assert.That(target.RequiredPreparation.Length).IsEqualTo(1);
        await Assert.That(target.RequiredPreparation[0]).IsEqualTo(dependencyId);
    }

    [Test]
    public async Task TargetBuilder_Builds_Target_FromSegmentedIdentity()
    {
        var config = new ConfigBuilder()
                    .SetProperty("runtime", new StringProperty("native"))
                    .Build();
        var projectId = new ProjectId("/workspace/app");
        var source = new TargetSource("Program.cs");

        var target = new TargetBuilder()
                    .WithProjectId(projectId)
                    .WithConfigId(config.Id)
                    .WithName(new TargetName("Compile"))
                    .WithSource(source)
                    .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("compiled")))
                    .Build();

        await Assert.That(target.Id).IsEqualTo(new TargetId(projectId, config.Id, new TargetName("Compile"), source));
    }

    [Test]
    public async Task TargetBuilder_RecomposesIdentityAfterWithIdAndSegmentUpdate()
    {
        var target = new TargetBuilder()
                    .WithId(new TargetId(
                        new ProjectId("/workspace/app"),
                        null,
                        new TargetName("Build"),
                        new TargetSource("build")))
                    .WithName(new TargetName("Pack"))
                    .WithSource(null)
                    .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("packed")))
                    .Build();

        await Assert.That(target.Id).IsEqualTo(new TargetId(
            new ProjectId("/workspace/app"),
            null,
            new TargetName("Pack"),
            null));
    }

    [Test]
    public async Task TargetBuilder_Defaults_RequiredPreparation_ToEmpty()
    {
        var target = new TargetBuilder()
                    .WithName(new TargetName("Build"))
                    .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("built")))
                    .Build();

        await Assert.That(target.RequiredPreparation.Length).IsEqualTo(0);
    }

    [Test]
    public async Task TargetBuilder_Throws_WhenNameMissing()
    {
        var builder = new TargetBuilder()
                     .WithExecute(static (_, _, _) => Task.FromResult<ExecutionResult>(new SuccessfulExecution("built")));

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Target name is required.");
    }

    [Test]
    public async Task TargetBuilder_Throws_WhenExecuteDelegateMissing()
    {
        var builder = new TargetBuilder()
                     .WithName(new TargetName("Build"));

        Action action = () => _ = builder.Build();

        await Assert.That(action).ThrowsException()
            .WithMessage("Target execute delegate is required.");
    }

    [Test]
    public async Task Target_ExecuteAsync_Forwards_AllArguments_ToDelegate()
    {
        var targetId = new TargetId(null, null, new TargetName("Build"), null);
        var expectedResult = new SuccessfulExecution("built");
        IEvaluatedBuilding? receivedBuilding = null;
        ImmutableDictionary<TargetId, object?>? receivedResults = null;
        var receivedToken = CancellationToken.None;

        var target = new Target(
            targetId,
            [],
            (building, collectedExecutionResults, cancellationToken) =>
            {
                receivedBuilding = building;
                receivedResults = collectedExecutionResults;
                receivedToken = cancellationToken;
                return Task.FromResult<ExecutionResult>(expectedResult);
            });

        var building = new EvaluatedBuildingBuilder()
                      .WithProfile(static profile => profile.WithName("Debug"))
                      .WithConfig(new ConfigBuilder().Build())
                      .AddTarget(target)
                      .Build();
        var collectedResults = ImmutableDictionary<TargetId, object?>.Empty.Add(targetId, "build-value");
        using var cancellationTokenSource = new CancellationTokenSource();

        var result = await target.ExecuteAsync(building, collectedResults, cancellationTokenSource.Token);

        await Assert.That(result).IsSameReferenceAs(expectedResult);
        await Assert.That(receivedBuilding).IsSameReferenceAs(building);
        await Assert.That(receivedResults).IsSameReferenceAs(collectedResults);
        await Assert.That(receivedToken).IsEqualTo(cancellationTokenSource.Token);
    }
}
