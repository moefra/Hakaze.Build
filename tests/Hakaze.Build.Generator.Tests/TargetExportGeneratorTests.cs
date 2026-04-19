using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Core;
using Hakaze.Build.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using BuildProjectId = Hakaze.Build.Abstractions.ProjectId;

namespace Hakaze.Build.Generator.Tests;

public class TargetExportGeneratorTests
{
    [Test]
    public async Task Generate_CompileAndPostCompile_RetrievesDependencyResult()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            public sealed record Foo(string Value);

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Compile(
                    CancellationToken cancellationToken,
                    IEvaluatedBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<Foo>("compiled", new Foo(building.Profile.Name)));
                }

                [Target]
                public static Task<ExecutionResult> PostCompile(
                    ImmutableDictionary<TargetId, object?> collectedExecutionResults,
                    [Retrieval(nameof(Compile))] Foo foo,
                    IEvaluatedBuilding building)
                {
                    Foo? collectedFoo = null;
                    foreach (var value in collectedExecutionResults.Values)
                    {
                        if (value is Foo typedValue)
                        {
                            collectedFoo = typedValue;
                            break;
                        }
                    }

                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>("post", $"{building.Profile.Name}:{foo.Value}:{collectedFoo?.Value}"));
                }
            }
            """);

        var result = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);
        AssertGeneratedFiles(result, 1);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();

        var factoryType = assembly.GetType("Sample.TargetsFactory") ?? throw new InvalidOperationException("Missing generated factory.");
        var factory = (ITargetFactory?)Activator.CreateInstance(factoryType) ?? throw new InvalidOperationException("Missing factory instance.");
        var targets = await factory.GetTargetsAsync(building);
        if (targets.Length != 2)
        {
            throw new InvalidOperationException($"Expected two targets, found {targets.Length}.");
        }

        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "Sample.Targets.PostCompile");

        var executionResult = await evaluatedBuilding.Execute(postCompileTarget.Id);
        if (executionResult is not SuccessfulExecutionWithResult<string> successfulResult)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {executionResult.GetType().Name}.");
        }

        if (successfulResult.ExecutionResult != "Debug:Debug:Debug")
        {
            throw new InvalidOperationException($"Expected retrieved result to be 'Debug', got '{successfulResult.ExecutionResult}'.");
        }
    }

    [Test]
    public async Task Generate_InvokeMethod_RunsDependenciesAndMatchesBuildingExecute()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            public sealed record Foo(string Value);

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Compile(IEvaluatedBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<Foo>("compiled", new Foo(building.Profile.Name)));
                }

                [Target]
                public static Task<ExecutionResult> PostCompile(
                    CancellationToken cancellationToken,
                    [Retrieval(nameof(Compile))] Foo foo,
                    ImmutableDictionary<TargetId, object?> collectedExecutionResults,
                    IEvaluatedBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>(
                            "post",
                            $"{building.Profile.Name}:{foo.Value}:{collectedExecutionResults.Count}:{cancellationToken.CanBeCanceled}"));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();
        var factory = (ITargetFactory?)Activator.CreateInstance(assembly.GetType("Sample.TargetsFactory")!)!;
        var targets = await factory.GetTargetsAsync(building);
        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);

        var invokeResult = await InvokeGeneratedTargetAsync(
            assembly.GetType("Sample.Targets")!,
            "InvokePostCompile",
            [evaluatedBuilding, CancellationToken.None]);
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "Sample.Targets.PostCompile");
        var executeResult = await evaluatedBuilding.Execute(postCompileTarget.Id);

        if (invokeResult is not SuccessfulExecutionWithResult<string> invokeSuccess)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {invokeResult.GetType().Name}.");
        }

        if (executeResult is not SuccessfulExecutionWithResult<string> executeSuccess)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {executeResult.GetType().Name}.");
        }

        if (invokeSuccess.ExecutionResult != executeSuccess.ExecutionResult)
        {
            throw new InvalidOperationException($"Expected InvokePostCompile to match Execute, got '{invokeSuccess.ExecutionResult}' and '{executeSuccess.ExecutionResult}'.");
        }
    }

    [Test]
    public async Task Generate_InvokeMethodDelegate_OverridesRootImplementationOnly()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            public sealed record Foo(string Value);

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Compile(IEvaluatedBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<Foo>("compiled", new Foo(building.Profile.Name)));
                }

                [Target]
                public static Task<ExecutionResult> PostCompile(
                    [Retrieval(nameof(Compile))] Foo foo,
                    IEvaluatedBuilding building,
                    ImmutableDictionary<TargetId, object?> collectedExecutionResults,
                    CancellationToken cancellationToken)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>(
                            "post",
                            $"{building.Profile.Name}:{foo.Value}:{collectedExecutionResults.Count}:{cancellationToken.CanBeCanceled}"));
                }

                public static Task<ExecutionResult> CustomPostCompile(
                    Foo foo,
                    IEvaluatedBuilding building,
                    ImmutableDictionary<TargetId, object?> collectedExecutionResults,
                    CancellationToken cancellationToken)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>(
                            "custom",
                            $"override:{building.Profile.Name}:{foo.Value}:{collectedExecutionResults.Count}:{cancellationToken.CanBeCanceled}"));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();
        var factory = (ITargetFactory?)Activator.CreateInstance(assembly.GetType("Sample.TargetsFactory")!)!;
        var targets = await factory.GetTargetsAsync(building);
        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var targetsType = assembly.GetType("Sample.Targets")!;
        var delegateType = targetsType.GetNestedType("PostCompileInvokerDelegate", BindingFlags.Public) ?? throw new InvalidOperationException("Missing generated delegate type.");
        var customMethod = targetsType.GetMethod("CustomPostCompile", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing custom implementation.");
        var implementation = Delegate.CreateDelegate(delegateType, customMethod);

        var invokeResult = await InvokeGeneratedTargetAsync(
            targetsType,
            "InvokePostCompile",
            [evaluatedBuilding, CancellationToken.None, implementation]);

        if (invokeResult is not SuccessfulExecutionWithResult<string> success)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {invokeResult.GetType().Name}.");
        }

        if (success.ExecutionResult != "override:Debug:Debug:1:False")
        {
            throw new InvalidOperationException($"Expected override result, got '{success.ExecutionResult}'.");
        }
    }

    [Test]
    public async Task Generate_GlobalTargets_UseNullProjectIdAndSourceByDefault()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample.Nested;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("built"));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var targetsType = assembly.GetType("Sample.Nested.Targets") ?? throw new InvalidOperationException("Missing generated target container.");
        var getTargetsAsync = targetsType.GetMethod("GetTargetsAsync", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing GetTargetsAsync method.");
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();

        var targets = await InvokeGeneratedTargetsAsync(getTargetsAsync, building, CancellationToken.None);
        var target = targets.Single();
        if (target.Id.ProjectId is not null)
        {
            throw new InvalidOperationException($"Expected global project id to be null, got '{target.Id.ProjectId}'.");
        }

        if (target.Id.Source is not null)
        {
            throw new InvalidOperationException($"Expected global target source to be null, got '{target.Id.Source}'.");
        }

        if (target.Id.ConfigId is not null)
        {
            throw new InvalidOperationException($"Expected global target config id to be null, got '{target.Id.ConfigId}'.");
        }

        if (target.Id.Name != new TargetName("Sample.Nested.Targets.Build"))
        {
            throw new InvalidOperationException($"Expected global target name to be 'Sample.Nested.Targets.Build', got '{target.Id.Name}'.");
        }
    }

    [Test]
    public async Task Generate_TargetSourceAttribute_OverridesGeneratedSourceAndPreservesRetrieval()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            public sealed record Foo(string Value);

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                [TargetSource(nameof(GetCompileSource))]
                public static Task<ExecutionResult> Compile()
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<Foo>("compiled", new Foo("compile-value")));
                }

                [Target]
                [TargetSource(nameof(GetPostCompileSource))]
                public static Task<ExecutionResult> PostCompile(
                    [Retrieval(nameof(Compile))] Foo foo)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>("post", foo.Value));
                }

                private static string GetCompileSource(IBuilding building, CancellationToken cancellationToken) => "compile-source";

                private static string GetPostCompileSource(IBuilding building, CancellationToken cancellationToken) => "post-source";
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();
        var factory = (ITargetFactory?)Activator.CreateInstance(assembly.GetType("Sample.TargetsFactory")!)!;
        var targets = await factory.GetTargetsAsync(building);
        var compileTarget = targets.Single(static target => target.Id.Name.Name == "Sample.Targets.Compile");
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "Sample.Targets.PostCompile");

        if (compileTarget.Id.Source != new TargetSource("compile-source"))
        {
            throw new InvalidOperationException($"Expected compile source override to be 'compile-source', got '{compileTarget.Id.Source}'.");
        }

        if (postCompileTarget.Id.Source != new TargetSource("post-source"))
        {
            throw new InvalidOperationException($"Expected post source override to be 'post-source', got '{postCompileTarget.Id.Source}'.");
        }

        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var executionResult = await evaluatedBuilding.Execute(postCompileTarget.Id);
        if (executionResult is not SuccessfulExecutionWithResult<string> success)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {executionResult.GetType().Name}.");
        }

        if (success.ExecutionResult != "compile-value")
        {
            throw new InvalidOperationException($"Expected retrieval across different target sources to succeed, got '{success.ExecutionResult}'.");
        }
    }

    [Test]
    public async Task Generate_PerProject_ExpandsTargetsForEachProject()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            [PerProject]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build(IEvaluatedBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution(building.Profile.Name));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var targetsType = assembly.GetType("Sample.Targets") ?? throw new InvalidOperationException("Missing generated target container.");
        var getTargetsAsync = targetsType.GetMethod("GetTargetsAsync", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing GetTargetsAsync method.");
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app1")))
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app2")))
            .Build();

        var targets = await InvokeGeneratedTargetsAsync(getTargetsAsync, building, CancellationToken.None);
        if (targets.Length != 2)
        {
            throw new InvalidOperationException($"Expected two per-project targets, found {targets.Length}.");
        }

        var projectIds = targets.Select(static target => target.Id.ProjectId).ToImmutableHashSet();
        if (!projectIds.SetEquals([new BuildProjectId("/workspace/app1"), new BuildProjectId("/workspace/app2")]))
        {
            throw new InvalidOperationException("Per-project expansion did not preserve project ids.");
        }
    }

    [Test]
    public async Task Generate_InvokeMethodForPerProject_UsesExplicitTargetIdAndMatchesBuildingExecute()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            [PerProject]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>("built", "ok"));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app1")))
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app2")))
            .Build();
        var factory = (ITargetFactory?)Activator.CreateInstance(assembly.GetType("Sample.TargetsFactory")!)!;
        var targets = await factory.GetTargetsAsync(building);
        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var targetId = targets.Single(static target => target.Id.ProjectId == new BuildProjectId("/workspace/app2")).Id;

        var invokeResult = await InvokeGeneratedTargetAsync(
            assembly.GetType("Sample.Targets")!,
            "InvokeBuild",
            [evaluatedBuilding, targetId, CancellationToken.None]);
        var directResult = await evaluatedBuilding.Execute(targetId);

        if (invokeResult is not SuccessfulExecutionWithResult<string> invokeSuccess)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {invokeResult.GetType().Name}.");
        }

        if (directResult is not SuccessfulExecutionWithResult<string> directSuccess)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {directResult.GetType().Name}.");
        }

        if (invokeSuccess.ExecutionResult != directSuccess.ExecutionResult)
        {
            throw new InvalidOperationException(
                $"Expected explicit TargetId invocation to match building.Execute(targetId), got '{invokeSuccess.ExecutionResult}' and '{directSuccess.ExecutionResult}'.");
        }
    }

    [Test]
    public void Generate_InvokeMethodForPerProject_RejectsMismatchedTargetId()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            [PerProject]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("built"));
                }

                [Target]
                public static Task<ExecutionResult> Other()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("other"));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app1")))
            .Build();
        var factory = (ITargetFactory?)Activator.CreateInstance(assembly.GetType("Sample.TargetsFactory")!)!;
        var targets = factory.GetTargetsAsync(building).GetAwaiter().GetResult();
        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var otherTargetId = targets.Single(static target => target.Id.Name.Name == "Sample.Targets.Other").Id;
        var invokeMethod = FindGeneratedMethod(assembly.GetType("Sample.Targets")!, "InvokeBuild", [evaluatedBuilding, otherTargetId, CancellationToken.None]);

        try
        {
            _ = invokeMethod.Invoke(null, [evaluatedBuilding, otherTargetId, CancellationToken.None]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("Expected InvokeBuild to reject a TargetId for a different target.");
    }

    [Test]
    public async Task Generate_TargetFactoryMethods_AreAppendedInDeclarationOrder()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            public sealed class ManualTarget(TargetId id, string value) : ITarget
            {
                public TargetId Id { get; } = id;

                public ImmutableArray<TargetId> RequiredPreparation { get; } = [];

                public Task<ExecutionResult> ExecuteAsync(
                    IEvaluatedBuilding building,
                    ImmutableDictionary<TargetId, object?> collectedExecutionResults,
                    CancellationToken cancellationToken = default)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecutionWithResult<string>("manual", value));
                }
            }

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("built"));
                }

                [TargetFactory("FactoryZero")]
                public static Task<ImmutableArray<ITarget>> CreateZeroAsync()
                {
                    return Task.FromResult<ImmutableArray<ITarget>>([new ManualTarget(
                        new TargetId(
                            null,
                            null,
                            FactoryZeroId,
                            new TargetSource("factory-zero")),
                        "zero")]);
                }

                [TargetFactory]
                public static Task<ImmutableArray<ITarget>> CreateFromContextAsync(IBuilding building, CancellationToken cancellationToken)
                {
                    return Task.FromResult<ImmutableArray<ITarget>>([new ManualTarget(
                        new TargetId(
                            null,
                            null,
                            new TargetName("FactoryContext"),
                            new TargetSource($"factory:{building.Profile.Name}:{cancellationToken.CanBeCanceled}")),
                        $"{building.Profile.Name}:{cancellationToken.CanBeCanceled}")]);
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var targetsType = assembly.GetType("Sample.Targets") ?? throw new InvalidOperationException("Missing generated target container.");
        var getTargetsAsync = targetsType.GetMethod("GetTargetsAsync", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing GetTargetsAsync method.");
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();

        var targets = await InvokeGeneratedTargetsAsync(getTargetsAsync, building, CancellationToken.None);
        var targetNames = targets.Select(static target => target.Id.Name.Name).ToImmutableArray();
        if (!targetNames.SequenceEqual(["Sample.Targets.Build", "Sample.Targets.FactoryZero", "FactoryContext"]))
        {
            throw new InvalidOperationException($"Expected generated targets to be appended before target factory outputs, got '{string.Join(", ", targetNames)}'.");
        }

        var factoryZeroName = targetsType.GetField("FactoryZeroName", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (!Equals(factoryZeroName, "Sample.Targets.FactoryZero"))
        {
            throw new InvalidOperationException($"Expected generated factory helper name to be 'Sample.Targets.FactoryZero', got '{factoryZeroName}'.");
        }

        var factoryZeroId = targetsType.GetProperty("FactoryZeroId", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (!Equals(factoryZeroId, new TargetName("Sample.Targets.FactoryZero")))
        {
            throw new InvalidOperationException($"Expected generated factory helper id to be 'Sample.Targets.FactoryZero', got '{factoryZeroId}'.");
        }

        if (targets[0].Id.ConfigId is not null)
        {
            throw new InvalidOperationException($"Expected generated target config id to be null, got '{targets[0].Id.ConfigId}'.");
        }

        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var factoryContextTarget = targets.Single(static target => target.Id.Name.Name == "FactoryContext");
        var executionResult = await evaluatedBuilding.Execute(factoryContextTarget.Id);
        if (executionResult is not SuccessfulExecutionWithResult<string> success)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {executionResult.GetType().Name}.");
        }

        if (success.ExecutionResult != "Release:False")
        {
            throw new InvalidOperationException($"Expected injected context result 'Release:False', got '{success.ExecutionResult}'.");
        }
    }

    [Test]
    public async Task Generate_TargetFactoryMethods_WorkWithPerProjectExports()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            public sealed class ManualTarget(TargetId id, string value) : ITarget
            {
                public TargetId Id { get; } = id;

                public ImmutableArray<TargetId> RequiredPreparation { get; } = [];

                public Task<ExecutionResult> ExecuteAsync(
                    IEvaluatedBuilding building,
                    ImmutableDictionary<TargetId, object?> collectedExecutionResults,
                    CancellationToken cancellationToken = default)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecutionWithResult<string>("manual", value));
                }
            }

            [ExportTargets]
            [PerProject]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("built"));
                }

                [TargetFactory("FactoryProject")]
                public static Task<ImmutableArray<ITarget>> CreateProjectTargetsAsync(IBuilding building)
                {
                    var builder = ImmutableArray.CreateBuilder<ITarget>();
                    foreach (var project in building.Projects)
                    {
                        builder.Add(new ManualTarget(
                            new TargetId(
                                project.Id,
                                null,
                                FactoryProjectId,
                                new TargetSource("manual-project-target")),
                            project.Id.Path));
                    }

                    return Task.FromResult(builder.ToImmutable());
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var targetsType = assembly.GetType("Sample.Targets") ?? throw new InvalidOperationException("Missing generated target container.");
        var getTargetsAsync = targetsType.GetMethod("GetTargetsAsync", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing GetTargetsAsync method.");
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app1")))
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app2")))
            .Build();

        var targets = await InvokeGeneratedTargetsAsync(getTargetsAsync, building, CancellationToken.None);
        if (targets.Length != 4)
        {
            throw new InvalidOperationException($"Expected four targets after appending per-project factory targets, found {targets.Length}.");
        }

        var factoryProjectId = targetsType.GetProperty("FactoryProjectId", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (!Equals(factoryProjectId, new TargetName("Sample.Targets.FactoryProject")))
        {
            throw new InvalidOperationException($"Expected generated per-project factory helper id to be 'Sample.Targets.FactoryProject', got '{factoryProjectId}'.");
        }

        var generatedTargets = targets.Where(static target => target.Id.Name.Name == "Sample.Targets.Build").ToImmutableArray();
        if (generatedTargets.Any(static target => target.Id.ConfigId is not null))
        {
            throw new InvalidOperationException("Expected generated per-project targets to be config-agnostic.");
        }

        var manualTargets = targets.Where(static target => target.Id.Name.Name == "Sample.Targets.FactoryProject").ToImmutableArray();
        var projectIds = manualTargets.Select(static target => target.Id.ProjectId).ToImmutableHashSet();
        if (!projectIds.SetEquals([new BuildProjectId("/workspace/app1"), new BuildProjectId("/workspace/app2")]))
        {
            throw new InvalidOperationException("Per-project target factory outputs did not preserve project ids.");
        }

        if (manualTargets.Any(static target => target.Id.ConfigId is not null))
        {
            throw new InvalidOperationException("Expected named factory helper sample targets to remain config-agnostic.");
        }
    }

    [Test]
    public void Generate_NamedTargetFactory_OnlyGeneratesHelperForNamedFactory()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            public partial class Targets
            {
                [TargetFactory("FactoryTarget")]
                public static Task<ImmutableArray<ITarget>> CreateNamedAsync()
                {
                    return Task.FromResult(ImmutableArray<ITarget>.Empty);
                }

                [TargetFactory]
                public static Task<ImmutableArray<ITarget>> CreateUnnamedAsync()
                {
                    return Task.FromResult(ImmutableArray<ITarget>.Empty);
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var targetsType = assembly.GetType("Sample.Targets") ?? throw new InvalidOperationException("Missing generated target container.");
        var namedHelper = targetsType.GetField("FactoryTargetName", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (!Equals(namedHelper, "Sample.Targets.FactoryTarget"))
        {
            throw new InvalidOperationException($"Expected generated helper name 'Sample.Targets.FactoryTarget', got '{namedHelper}'.");
        }

        if (targetsType.GetField("CreateUnnamedAsyncName", BindingFlags.Public | BindingFlags.Static) is not null)
        {
            throw new InvalidOperationException("Did not expect unnamed target factory to generate helper members.");
        }
    }

    [Test]
    public async Task Generate_ExportTargetsType_ImplementsInterfaceAndSupportsGenericStaticAccess()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Compile(IEvaluatedBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>("compiled", building.Profile.Name));
                }
            }

            public static class GenericTargets
            {
                public static Task<ImmutableArray<ITarget>> Load<T>(IBuilding building, CancellationToken cancellationToken = default)
                    where T : IExportTargets
                {
                    return T.GetTargetsAsync(building, cancellationToken);
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var targetsType = assembly.GetType("Sample.Targets")!;
        AssertImplementsInterface(targetsType, "Hakaze.Build.Abstractions.Generator.IExportTargets");

        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();

        var helperType = assembly.GetType("Sample.GenericTargets")!;
        var targets = await InvokeGenericTargetsAsync(helperType, "Load", targetsType, building, CancellationToken.None);
        if (targets.Length != 1)
        {
            throw new InvalidOperationException($"Expected a single target from generic interface-based loading, found {targets.Length}.");
        }

        if (targets[0].Id.Name.Name != "Sample.Targets.Compile")
        {
            throw new InvalidOperationException($"Expected generic interface-based loading to return 'Sample.Targets.Compile', got '{targets[0].Id.Name.Name}'.");
        }
    }

    [Test]
    public void Generate_DoesNotDuplicateExplicitIExportTargetsImplementation()
    {
        var compilation = CreateCompilation("""
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            public partial class Targets : IExportTargets
            {
                [Target]
                public static Task<ExecutionResult> Compile()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("compiled"));
                }
            }
            """);

        var result = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var generatedSource = GetGeneratedSourceText(result);
        if (generatedSource.Contains(": global::Hakaze.Build.Abstractions.Generator.IExportTargets", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected generator not to repeat an explicitly implemented IExportTargets interface.");
        }
    }

    [Test]
    public void Generate_RuntimeTypeMismatch_ReturnsFailedExecution()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Compile()
                {
                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>("compiled", "hello"));
                }

                [Target]
                public static Task<ExecutionResult> PostCompile(
                    [Retrieval(nameof(Compile))] int value)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecutionWithResult<int>("post", value));
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();
        var factory = (ITargetFactory?)Activator.CreateInstance(assembly.GetType("Sample.TargetsFactory")!)!;
        var targets = factory.GetTargetsAsync(building).GetAwaiter().GetResult();
        var evaluatedBuilding = CreateEvaluatedBuilding(building, targets);
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "Sample.Targets.PostCompile");

        var executionResult = evaluatedBuilding.Execute(postCompileTarget.Id).GetAwaiter().GetResult();
        if (executionResult is not FailedExecution)
        {
            throw new InvalidOperationException($"Expected FailedExecution, got {executionResult.GetType().Name}.");
        }
    }

    [Test]
    public void Generate_DiagnosticWhenExportTypeIsNotPartial()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public class Targets
            {
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0001");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetMethodIsNotStatic()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0002");
    }

    [Test]
    public void Generate_DiagnosticWhenUnknownTargetIsReferenced()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                [DependOn("Missing")]
                public static Task<ExecutionResult> Build(
                    [Retrieval("AlsoMissing")] string value)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecutionWithResult<string>("ok", value));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0007");
    }

    [Test]
    public void Generate_DiagnosticWhenIBuildingParameterIsUsed()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build(IBuilding building)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0004");
    }

    [Test]
    public void Generate_DiagnosticWhenCancellationTokenParameterIsDuplicated()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build(
                    CancellationToken first,
                    CancellationToken second)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0005");
    }

    [Test]
    public void Generate_DiagnosticWhenEvaluatedBuildingParameterIsDuplicated()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build(
                    IEvaluatedBuilding first,
                    IEvaluatedBuilding second)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0011");
    }

    [Test]
    public void Generate_DiagnosticWhenCollectedExecutionResultsParameterIsDuplicated()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build(
                    ImmutableDictionary<TargetId, object?> first,
                    ImmutableDictionary<TargetId, object?> second)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0012");
    }

    [Test]
    public void Generate_DiagnosticWhenBusinessParameterMissesRetrieval()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                public static Task<ExecutionResult> Build(string value)
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecutionWithResult<string>("ok", value));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0006");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetSourceIsUsedOnNonTargetMethod()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [TargetSource(nameof(GetSource))]
                public static string Helper() => "helper";

                private static string GetSource(IBuilding building, CancellationToken cancellationToken) => "source";
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0018");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetSourceResolverIsMissing()
    {
        var compilation = CreateCompilation("""
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                [TargetSource("Missing")]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0019");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetSourceResolverIsNotStatic()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                [TargetSource(nameof(GetSource))]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }

                private string GetSource(IBuilding building, CancellationToken cancellationToken) => "source";
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0020");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetSourceResolverHasWrongReturnType()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                [TargetSource(nameof(GetSource))]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }

                private static TargetSource GetSource(IBuilding building, CancellationToken cancellationToken) => new("source");
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0021");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetSourceResolverHasWrongParameters()
    {
        var compilation = CreateCompilation("""
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [Target]
                [TargetSource(nameof(GetSource))]
                public static Task<ExecutionResult> Build()
                {
                    return Task.FromResult<ExecutionResult>(new SuccessfulExecution("ok"));
                }

                private static string GetSource(IBuilding building) => "source";
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0022");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetFactoryMethodIsNotStatic()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [TargetFactory]
                public Task<ImmutableArray<ITarget>> CreateTargetsAsync()
                {
                    return Task.FromResult(ImmutableArray<ITarget>.Empty);
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0013");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetFactoryMethodHasWrongReturnType()
    {
        var compilation = CreateCompilation("""
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [TargetFactory]
                public static Task<ITarget[]> CreateTargetsAsync()
                {
                    return Task.FromResult<ITarget[]>([]);
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0014");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetFactoryBuildingParameterIsDuplicated()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [TargetFactory]
                public static Task<ImmutableArray<ITarget>> CreateTargetsAsync(IBuilding first, IBuilding second)
                {
                    return Task.FromResult(ImmutableArray<ITarget>.Empty);
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0015");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetFactoryCancellationTokenParameterIsDuplicated()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [TargetFactory]
                public static Task<ImmutableArray<ITarget>> CreateTargetsAsync(CancellationToken first, CancellationToken second)
                {
                    return Task.FromResult(ImmutableArray<ITarget>.Empty);
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0016");
    }

    [Test]
    public void Generate_DiagnosticWhenTargetFactoryHasUnsupportedParameter()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            [ExportTargets]
            public partial class Targets
            {
                [TargetFactory]
                public static Task<ImmutableArray<ITarget>> CreateTargetsAsync(string value)
                {
                    return Task.FromResult(ImmutableArray<ITarget>.Empty);
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0017");
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            "using Hakaze.Build.Abstractions.Generator;\n" + source,
            new CSharpParseOptions(LanguageVersion.Preview));
        return CSharpCompilation.Create(
            assemblyName: $"HakazeBuildGeneratorTests_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorDriverRunResult RunGenerator(CSharpCompilation compilation, out CSharpCompilation outputCompilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new TargetExportGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out _);
        outputCompilation = (CSharpCompilation)updatedCompilation;
        return driver.GetRunResult();
    }

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>();
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [];

        references.AddRange(trustedPlatformAssemblies.Select(static path => MetadataReference.CreateFromFile(path)));
        references.Add(MetadataReference.CreateFromFile(typeof(IBuilding).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(EvaluatedBuildingBuilder).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(TargetExportGenerator).Assembly.Location));
        return references.ToImmutableArray();
    }

    private static Assembly LoadAssembly(CSharpCompilation compilation)
    {
        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var failures = string.Join(Environment.NewLine, emitResult.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
            throw new InvalidOperationException($"Compilation emit failed:{Environment.NewLine}{failures}");
        }

        peStream.Position = 0;
        var loadContext = new AssemblyLoadContext($"HakazeBuildGeneratorTests_{Guid.NewGuid():N}", isCollectible: true);
        return loadContext.LoadFromStream(peStream);
    }

    private static IEvaluatedBuilding CreateEvaluatedBuilding(IBuilding building, ImmutableArray<ITarget> targets)
    {
        var builder = new EvaluatedBuildingBuilder()
            .WithProfile(building.Profile)
            .WithConfig(building.Config);

        foreach (var project in building.Projects)
        {
            builder.AddProject(project);
        }

        foreach (var target in targets)
        {
            builder.AddTarget(target);
        }

        return builder.Build();
    }

    private static MethodInfo FindGeneratedMethod(Type targetsType, string methodName, object?[] arguments)
    {
        return targetsType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
            {
                if (method.Name != methodName)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != arguments.Length)
                {
                    return false;
                }

                for (var index = 0; index < parameters.Length; index++)
                {
                    var argument = arguments[index];
                    if (argument is null)
                    {
                        return !parameters[index].ParameterType.IsValueType ||
                               Nullable.GetUnderlyingType(parameters[index].ParameterType) is not null;
                    }

                    if (!parameters[index].ParameterType.IsInstanceOfType(argument))
                    {
                        return false;
                    }
                }

                return true;
            });
    }

    private static async Task<ExecutionResult> InvokeGeneratedTargetAsync(Type targetsType, string methodName, object?[] arguments)
    {
        var invokeMethod = FindGeneratedMethod(targetsType, methodName, arguments);
        var task = (Task<ExecutionResult>?)invokeMethod.Invoke(null, arguments);
        if (task is null)
        {
            throw new InvalidOperationException($"Generated method '{methodName}' did not return Task<ExecutionResult>.");
        }

        return await task;
    }

    private static async Task<ImmutableArray<ITarget>> InvokeGeneratedTargetsAsync(MethodInfo getTargetsAsyncMethod, IBuilding building, CancellationToken cancellationToken)
    {
        var task = (Task<ImmutableArray<ITarget>>?)getTargetsAsyncMethod.Invoke(null, [building, cancellationToken]);
        if (task is null)
        {
            throw new InvalidOperationException("Generated method 'GetTargetsAsync' did not return Task<ImmutableArray<ITarget>>.");
        }

        return await task;
    }

    private static async Task<ImmutableArray<ITarget>> InvokeGenericTargetsAsync(
        Type helperType,
        string methodName,
        Type genericTypeArgument,
        IBuilding building,
        CancellationToken cancellationToken)
    {
        var method = helperType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing helper method '{methodName}'.");
        var task = (Task<ImmutableArray<ITarget>>?)method.MakeGenericMethod(genericTypeArgument)
            .Invoke(null, [building, cancellationToken]);
        if (task is null)
        {
            throw new InvalidOperationException($"Helper method '{methodName}' did not return Task<ImmutableArray<ITarget>>.");
        }

        return await task;
    }

    private static void AssertNoErrors(Compilation compilation)
    {
        var diagnostics = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (diagnostics.Length == 0)
        {
            return;
        }

        var message = string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
        throw new InvalidOperationException($"Expected generator output to compile cleanly, but found:{Environment.NewLine}{message}");
    }

    private static void AssertContainsDiagnostic(GeneratorDriverRunResult result, string diagnosticId)
    {
        var diagnostics = result.Results.SelectMany(static runResult => runResult.Diagnostics).ToImmutableArray();
        if (diagnostics.Any(diagnostic => diagnostic.Id == diagnosticId))
        {
            return;
        }

        var actual = string.Join(", ", diagnostics.Select(static diagnostic => diagnostic.Id));
        throw new InvalidOperationException($"Expected diagnostic '{diagnosticId}', but found: {actual}");
    }

    private static void AssertGeneratedFiles(GeneratorDriverRunResult result, int expectedCount)
    {
        var generatedSources = result.Results.SelectMany(static runResult => runResult.GeneratedSources).ToImmutableArray();
        if (generatedSources.Length != expectedCount)
        {
            throw new InvalidOperationException($"Expected {expectedCount} generated source file(s), found {generatedSources.Length}.");
        }
    }

    private static void AssertImplementsInterface(Type type, string interfaceFullName)
    {
        if (type.GetInterfaces().Any(interfaceType => string.Equals(interfaceType.FullName, interfaceFullName, StringComparison.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException($"Expected type '{type.FullName}' to implement '{interfaceFullName}'.");
    }

    private static string GetGeneratedSourceText(GeneratorDriverRunResult result)
    {
        var generatedSource = result.Results.SelectMany(static runResult => runResult.GeneratedSources).SingleOrDefault();
        if (generatedSource.SourceText is not null)
        {
            return generatedSource.SourceText.ToString();
        }

        throw new InvalidOperationException("Expected exactly one generated source file.");
    }
}
