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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "sample-source";

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
        AssertGeneratedFiles(result, 2);

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
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "PostCompile");

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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "invoke-source";

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
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "PostCompile");
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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "delegate-source";

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
    public void Generate_GlobalTargets_UseReservedProjectId()
    {
        var compilation = CreateCompilation("""
            using System.Threading;
            using System.Threading.Tasks;
            using Hakaze.Build.Abstractions;

            namespace Sample.Nested;

            [ExportTargets]
            public partial class Targets
            {
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "sample-source";

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
        var getTargets = targetsType.GetMethod("GetTargets", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing GetTargets method.");
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Debug"))
            .WithConfig(new ConfigBuilder().Build())
            .Build();

        var targets = (ImmutableArray<ITarget>)getTargets.Invoke(null, [building, CancellationToken.None])!;
        var target = targets.Single();
        var expected = new BuildProjectId("//global/Sample.Nested.Targets");
        if (target.Id.ProjectId != expected)
        {
            throw new InvalidOperationException($"Expected global project id '{expected}', got '{target.Id.ProjectId}'.");
        }
    }

    [Test]
    public void Generate_PerProject_ExpandsTargetsForEachProject()
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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "per-project-source";

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
        var getTargets = targetsType.GetMethod("GetTargets", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Missing GetTargets method.");
        var building = new BuildingBuilder()
            .WithProfile(static profile => profile.WithName("Release"))
            .WithConfig(new ConfigBuilder().Build())
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app1")))
            .AddProject(static project => project.WithId(new BuildProjectId("/workspace/app2")))
            .Build();

        var targets = (ImmutableArray<ITarget>)getTargets.Invoke(null, [building, CancellationToken.None])!;
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
    public async Task Generate_InvokeMethodForPerProject_UsesExplicitTargetId()
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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "per-project-invoke-source";

                [Target]
                public static Task<ExecutionResult> Build(IEvaluatedBuilding building)
                {
                    TargetId selectedTargetId = default;
                    foreach (var targetId in building.Targets.Keys)
                    {
                        if (targetId.Name == BuildId)
                        {
                            selectedTargetId = targetId;
                            break;
                        }
                    }

                    return Task.FromResult<ExecutionResult>(
                        new SuccessfulExecutionWithResult<string>("built", selectedTargetId.ProjectId.Path));
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

        if (invokeResult is not SuccessfulExecutionWithResult<string> success)
        {
            throw new InvalidOperationException($"Expected SuccessfulExecutionWithResult<string>, got {invokeResult.GetType().Name}.");
        }

        if (success.ExecutionResult != "/workspace/app2")
        {
            throw new InvalidOperationException($"Expected explicit TargetId invocation to use '/workspace/app2', got '{success.ExecutionResult}'.");
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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "per-project-invalid-source";

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
        var otherTargetId = targets.Single(static target => target.Id.Name.Name == "Other").Id;
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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "mismatch-source";

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
        var postCompileTarget = targets.Single(static target => target.Id.Name.Name == "PostCompile");

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
                private static partial string GetTargetSourceId(IBuilding building, CancellationToken cancellationToken) => "source";

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

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
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
}
