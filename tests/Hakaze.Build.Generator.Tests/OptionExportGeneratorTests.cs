using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Hakaze.Build.Abstractions;
using Hakaze.Build.Core;
using Hakaze.Build.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Hakaze.Build.Generator.Tests;

public class OptionExportGeneratorTests
{
    [Test]
    public void Generate_BindAsync_BindsSupportedTypesAndRunsValidators()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public string CMakePath { get; init; }

                [Option("cmake-ver")]
                public int CMakeVersion { get; init; }

                [Option]
                public ImmutableArray<string> SourceFile { get; init; }

                [Option]
                public required ImmutableArray<Property> CMakeConfig { get; init; }

                public bool WasValidated { get; private set; }

                public bool WasAlsoValidated { get; private set; }

                [OptionValidator]
                public void Validate()
                {
                    WasValidated = true;
                }

                [OptionValidator]
                public void ValidateAgain()
                {
                    WasAlsoValidated = true;
                }
            }
            """);

        var result = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);
        AssertGeneratedFiles(result, 1);

        var assembly = LoadAssembly(outputCompilation);
        var config = new ConfigBuilder()
            .SetProperty("cmake-path", new StringProperty("cmake"))
            .SetProperty("cmake-ver", new IntegerProperty(3))
            .SetProperty("source-file", new ListProperty([new StringProperty("a.c"), new StringProperty("b.c")]))
            .SetProperty("cmake-config", new ListProperty([new StringProperty("Debug"), new BooleanProperty(true)]))
            .Build();

        var bound = InvokeBindAsync(assembly.GetType("Sample.SampleProperties")!, config);
        var boundType = bound.GetType();

        if ((string?)boundType.GetProperty("CMakePath")!.GetValue(bound) != "cmake")
        {
            throw new InvalidOperationException("Expected CMakePath to bind from cmake-path.");
        }

        if ((int)boundType.GetProperty("CMakeVersion")!.GetValue(bound)! != 3)
        {
            throw new InvalidOperationException("Expected CMakeVersion to bind from cmake-ver.");
        }

        var sourceFiles = (ImmutableArray<string>)boundType.GetProperty("SourceFile")!.GetValue(bound)!;
        if (!sourceFiles.SequenceEqual(["a.c", "b.c"]))
        {
            throw new InvalidOperationException($"Expected source files to bind correctly, got '{string.Join(",", sourceFiles)}'.");
        }

        var cmakeConfig = (ImmutableArray<Property>)boundType.GetProperty("CMakeConfig")!.GetValue(bound)!;
        if (cmakeConfig.Length != 2 ||
            cmakeConfig[0] is not StringProperty("Debug") ||
            cmakeConfig[1] is not BooleanProperty(true))
        {
            throw new InvalidOperationException("Expected CMakeConfig to preserve raw Property values.");
        }

        if ((bool)boundType.GetProperty("WasValidated")!.GetValue(bound)! is false ||
            (bool)boundType.GetProperty("WasAlsoValidated")!.GetValue(bound)! is false)
        {
            throw new InvalidOperationException("Expected validators to run after binding.");
        }
    }

    [Test]
    public void Generate_MetadataMethods_ReturnExpectedDocumentsAndTypes()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                /// <summary>
                /// path to the cmake executable
                /// </summary>
                [Option]
                public string CMakePath { get; init; }

                /// <summary>
                /// explicit version selector
                /// </summary>
                [Option("cmake-ver")]
                public int CMakeVersion { get; init; }

                /// <summary>
                /// source files
                /// spread across lines
                /// </summary>
                [Option]
                public ImmutableArray<string> SourceFile { get; init; }

                /// <summary>
                /// raw build properties
                /// </summary>
                [Option]
                public required ImmutableArray<Property> CMakeConfig { get; init; }

                /// <summary>
                /// optional feature toggle
                /// </summary>
                [Option]
                public bool EnableFeature { get; init; }

                /// <summary>
                /// retry count value
                /// </summary>
                [Option]
                public long RetryCount { get; init; }

                /// <summary>
                /// payload property
                /// </summary>
                [Option]
                public Property Payload { get; init; }

                /// <summary>
                /// validates option state
                /// </summary>
                [OptionValidator]
                public void Validate()
                {
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var optionsType = assembly.GetType("Sample.SampleProperties")!;
        var optionDocuments = InvokeStringDictionaryMethod(optionsType, "GetOptionDocuments");
        var validatorDocuments = InvokeStringDictionaryMethod(optionsType, "GetValidatorDocuments");
        var optionTypes = InvokeStringDictionaryMethod(optionsType, "GetOptionType");

        AssertDictionaryValue(optionDocuments, "cmake-path", "path to the cmake executable");
        AssertDictionaryValue(optionDocuments, "cmake-ver", "explicit version selector");
        AssertDictionaryValue(optionDocuments, "source-file", "source files spread across lines");
        AssertDictionaryValue(optionDocuments, "cmake-config", "raw build properties");
        AssertDictionaryValue(optionDocuments, "enable-feature", "optional feature toggle");
        AssertDictionaryValue(optionDocuments, "retry-count", "retry count value");
        AssertDictionaryValue(optionDocuments, "payload", "payload property");

        AssertDictionaryValue(validatorDocuments, "Validate", "validates option state");

        AssertDictionaryValue(optionTypes, "cmake-path", "string");
        AssertDictionaryValue(optionTypes, "cmake-ver", "int");
        AssertDictionaryValue(optionTypes, "source-file", "ImmutableArray<string>");
        AssertDictionaryValue(optionTypes, "cmake-config", "ImmutableArray<Property>");
        AssertDictionaryValue(optionTypes, "enable-feature", "bool");
        AssertDictionaryValue(optionTypes, "retry-count", "long");
        AssertDictionaryValue(optionTypes, "payload", "Property");
    }

    [Test]
    public void Generate_MetadataMethods_OmitUndocumentedMembersAndReportWarnings()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public required string CMakePath { get; init; }

                /// <summary>
                ///     
                /// </summary>
                [Option]
                public int RetryCount { get; init; }

                /// <summary>
                /// keeps its documentation
                /// </summary>
                [Option]
                public bool EnableFeature { get; init; }

                [OptionValidator]
                public void Validate()
                {
                }

                /// <summary>
                ///  	
                /// </summary>
                [OptionValidator]
                public void ValidateAgain()
                {
                }

                /// <summary>
                /// final validator documentation
                /// </summary>
                [OptionValidator]
                public void ValidateWithDocs()
                {
                }
            }
            """);

        var result = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);
        AssertDiagnosticCount(result, "HBG0029", 2);
        AssertDiagnosticCount(result, "HBG0030", 2);

        var assembly = LoadAssembly(outputCompilation);
        var optionsType = assembly.GetType("Sample.SampleProperties")!;
        var optionDocuments = InvokeStringDictionaryMethod(optionsType, "GetOptionDocuments");
        var validatorDocuments = InvokeStringDictionaryMethod(optionsType, "GetValidatorDocuments");
        var optionTypes = InvokeStringDictionaryMethod(optionsType, "GetOptionType");

        var config = new ConfigBuilder()
            .SetProperty("cmake-path", new StringProperty("ninja"))
            .SetProperty("retry-count", new IntegerProperty(3))
            .SetProperty("enable-feature", new BooleanProperty(true))
            .Build();

        var bound = InvokeBindAsync(optionsType, config);
        var boundType = bound.GetType();

        if ((string?)boundType.GetProperty("CMakePath")!.GetValue(bound) != "ninja")
        {
            throw new InvalidOperationException("Expected missing property documentation not to affect binding.");
        }

        if ((int)boundType.GetProperty("RetryCount")!.GetValue(bound)! != 3)
        {
            throw new InvalidOperationException("Expected blank summary documentation not to affect int binding.");
        }

        if ((bool)boundType.GetProperty("EnableFeature")!.GetValue(bound)! is false)
        {
            throw new InvalidOperationException("Expected documented property to bind normally.");
        }

        AssertDictionaryDoesNotContain(optionDocuments, "cmake-path");
        AssertDictionaryDoesNotContain(optionDocuments, "retry-count");
        AssertDictionaryValue(optionDocuments, "enable-feature", "keeps its documentation");

        AssertDictionaryDoesNotContain(validatorDocuments, "Validate");
        AssertDictionaryDoesNotContain(validatorDocuments, "ValidateAgain");
        AssertDictionaryValue(validatorDocuments, "ValidateWithDocs", "final validator documentation");

        AssertDictionaryValue(optionTypes, "cmake-path", "string");
        AssertDictionaryValue(optionTypes, "retry-count", "int");
        AssertDictionaryValue(optionTypes, "enable-feature", "bool");
    }

    [Test]
    public void Generate_ExportOptionsType_ImplementsInterfaceAndSupportsGenericStaticAccess()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                /// <summary>
                /// path to the cmake executable
                /// </summary>
                [Option]
                public required string CMakePath { get; init; }

                /// <summary>
                /// validates option state
                /// </summary>
                [OptionValidator]
                public void Validate()
                {
                }
            }

            public static class GenericOptions
            {
                public static T Bind<T>(IConfig config)
                    where T : IExportOptions<T>
                {
                    return T.BindAsync(config);
                }

                public static ImmutableDictionary<string, string> GetOptionDocuments<T>()
                    where T : IExportOptions<T>
                {
                    return T.GetOptionDocuments();
                }

                public static ImmutableDictionary<string, string> GetValidatorDocuments<T>()
                    where T : IExportOptions<T>
                {
                    return T.GetValidatorDocuments();
                }

                public static ImmutableDictionary<string, string> GetOptionType<T>()
                    where T : IExportOptions<T>
                {
                    return T.GetOptionType();
                }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var optionsType = assembly.GetType("Sample.SampleProperties")!;
        AssertImplementsClosedGenericInterface(optionsType, "Hakaze.Build.Abstractions.Generator.IExportOptions`1");

        var config = new ConfigBuilder()
            .SetProperty("cmake-path", new StringProperty("ninja"))
            .Build();

        var genericHelperType = assembly.GetType("Sample.GenericOptions")!;
        var bound = InvokeGenericStaticMethod(genericHelperType, "Bind", optionsType, [config]);
        if ((string?)bound.GetType().GetProperty("CMakePath")!.GetValue(bound) != "ninja")
        {
            throw new InvalidOperationException("Expected generic interface-based Bind to return bound options.");
        }

        var optionDocuments = (ImmutableDictionary<string, string>)InvokeGenericStaticMethod(genericHelperType, "GetOptionDocuments", optionsType, []);
        var validatorDocuments = (ImmutableDictionary<string, string>)InvokeGenericStaticMethod(genericHelperType, "GetValidatorDocuments", optionsType, []);
        var optionTypes = (ImmutableDictionary<string, string>)InvokeGenericStaticMethod(genericHelperType, "GetOptionType", optionsType, []);

        AssertDictionaryValue(optionDocuments, "cmake-path", "path to the cmake executable");
        AssertDictionaryValue(validatorDocuments, "Validate", "validates option state");
        AssertDictionaryValue(optionTypes, "cmake-path", "string");
    }

    [Test]
    public void Generate_DoesNotDuplicateExplicitIExportOptionsImplementation()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties : IExportOptions<SampleProperties>
            {
                /// <summary>
                /// path to the cmake executable
                /// </summary>
                [Option]
                public required string CMakePath { get; init; }
            }
            """);

        var result = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var generatedSource = GetGeneratedSourceText(result);
        if (generatedSource.Contains(": global::Hakaze.Build.Abstractions.Generator.IExportOptions<global::Sample.SampleProperties>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected generator not to repeat an explicitly implemented IExportOptions interface.");
        }
    }

    [Test]
    public void Generate_BindAsync_UsesExplicitKeysAndLeavesOptionalDefaults()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public required string CMakePath { get; init; }

                [Option("custom-name")]
                public long CustomValue { get; init; }

                [Option]
                public bool EnableFeature { get; init; }

                [Option]
                public string Toolchain { get; init; } = "msvc";
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var config = new ConfigBuilder()
            .SetProperty("cmake-path", new StringProperty("ninja"))
            .SetProperty("custom-name", new IntegerProperty(42))
            .Build();

        var bound = InvokeBindAsync(assembly.GetType("Sample.SampleProperties")!, config);
        var boundType = bound.GetType();

        if ((string?)boundType.GetProperty("CMakePath")!.GetValue(bound) != "ninja")
        {
            throw new InvalidOperationException("Expected kebab-case option key to be used.");
        }

        if ((long)boundType.GetProperty("CustomValue")!.GetValue(bound)! != 42)
        {
            throw new InvalidOperationException("Expected explicit option key to be used.");
        }

        if ((bool)boundType.GetProperty("EnableFeature")!.GetValue(bound)! != false)
        {
            throw new InvalidOperationException("Expected missing optional bool to keep its default value.");
        }

        if ((string?)boundType.GetProperty("Toolchain")!.GetValue(bound) != "msvc")
        {
            throw new InvalidOperationException("Expected missing optional property initializer to be preserved.");
        }
    }

    [Test]
    public void Generate_BindAsync_BindsBooleanAndLongArrays()
    {
        var compilation = CreateCompilation("""
            using System.Collections.Immutable;
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public ImmutableArray<bool> Enabled { get; init; }

                [Option]
                public ImmutableArray<long> RetryCount { get; init; }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var config = new ConfigBuilder()
            .SetProperty("enabled", new ListProperty([new BooleanProperty(true), new BooleanProperty(false)]))
            .SetProperty("retry-count", new ListProperty([new IntegerProperty(1), new IntegerProperty(3)]))
            .Build();

        var bound = InvokeBindAsync(assembly.GetType("Sample.SampleProperties")!, config);
        var boundType = bound.GetType();

        if (!((ImmutableArray<bool>)boundType.GetProperty("Enabled")!.GetValue(bound)!).SequenceEqual([true, false]))
        {
            throw new InvalidOperationException("Expected bool array binding to succeed.");
        }

        if (!((ImmutableArray<long>)boundType.GetProperty("RetryCount")!.GetValue(bound)!).SequenceEqual([1L, 3L]))
        {
            throw new InvalidOperationException("Expected long array binding to succeed.");
        }
    }

    [Test]
    public void Generate_BindAsync_ThrowsWhenRequiredOptionIsMissing()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public required string CMakePath { get; init; }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var config = new ConfigBuilder().Build();

        var exception = AssertThrows<KeyNotFoundException>(() => InvokeBindAsync(assembly.GetType("Sample.SampleProperties")!, config));
        if (!exception.Message.Contains("cmake-path", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected missing key message to mention 'cmake-path', got '{exception.Message}'.");
        }
    }

    [Test]
    public void Generate_BindAsync_ThrowsWhenPropertyTypeDoesNotMatch()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public required string CMakePath { get; init; }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var config = new ConfigBuilder()
            .SetProperty("cmake-path", new IntegerProperty(7))
            .Build();

        var exception = AssertThrows<InvalidOperationException>(() => InvokeBindAsync(assembly.GetType("Sample.SampleProperties")!, config));
        if (!exception.Message.Contains("cmake-path", StringComparison.Ordinal) ||
            !exception.Message.Contains("StringProperty", StringComparison.Ordinal) ||
            !exception.Message.Contains("IntegerProperty", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected mismatch message to include key and type details, got '{exception.Message}'.");
        }
    }

    [Test]
    public void Generate_BindAsync_ThrowsWhenIntegerOverflowsInt32()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public required int CMakeVersion { get; init; }
            }
            """);

        _ = RunGenerator(compilation, out var outputCompilation);
        AssertNoErrors(outputCompilation);

        var assembly = LoadAssembly(outputCompilation);
        var config = new ConfigBuilder()
            .SetProperty("cmake-version", new IntegerProperty((long)int.MaxValue + 1L))
            .Build();

        _ = AssertThrows<OverflowException>(() => InvokeBindAsync(assembly.GetType("Sample.SampleProperties")!, config));
    }

    [Test]
    public void Generate_DiagnosticWhenExportOptionsTypeIsNotPartial()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            [ExportOptions]
            public class SampleProperties
            {
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0023");
    }

    [Test]
    public void Generate_DiagnosticWhenExportOptionsTypeIsNestedGeneric()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            public static class Outer
            {
                [ExportOptions]
                public partial class SampleProperties<T>
                {
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0024");
    }

    [Test]
    public void Generate_DiagnosticsForInvalidMembers()
    {
        var compilation = CreateCompilation("""
            using Hakaze.Build.Abstractions;

            namespace Sample;

            [ExportOptions]
            public partial class SampleProperties
            {
                [Option]
                public string Name { get; }

                [Option]
                public object Payload { get; init; }

                [Option("dup")]
                public int First { get; init; }

                [Option("dup")]
                public int Second { get; init; }

                [OptionValidator]
                public static int Validate(int value)
                {
                    return value;
                }
            }
            """);

        var result = RunGenerator(compilation, out _);
        AssertContainsDiagnostic(result, "HBG0025");
        AssertContainsDiagnostic(result, "HBG0026");
        AssertContainsDiagnostic(result, "HBG0027");
        AssertContainsDiagnostic(result, "HBG0028");
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            "using Hakaze.Build.Abstractions.Generator;\n" + source,
            new CSharpParseOptions(LanguageVersion.Preview, documentationMode: DocumentationMode.Diagnose));
        return CSharpCompilation.Create(
            assemblyName: $"HakazeBuildOptionGeneratorTests_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorDriverRunResult RunGenerator(CSharpCompilation compilation, out CSharpCompilation outputCompilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OptionExportGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, documentationMode: DocumentationMode.Diagnose));

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
        references.Add(MetadataReference.CreateFromFile(typeof(IConfig).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(ConfigBuilder).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(OptionExportGenerator).Assembly.Location));
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
        var loadContext = new AssemblyLoadContext($"HakazeBuildOptionGeneratorTests_{Guid.NewGuid():N}", isCollectible: true);
        return loadContext.LoadFromStream(peStream);
    }

    private static object InvokeBindAsync(Type optionsType, IConfig config)
    {
        var method = optionsType.GetMethod("BindAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing generated BindAsync method.");

        try
        {
            return method.Invoke(null, [config]) ?? throw new InvalidOperationException("BindAsync returned null.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static ImmutableDictionary<string, string> InvokeStringDictionaryMethod(Type optionsType, string methodName)
    {
        var method = optionsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing generated {methodName} method.");

        try
        {
            return (ImmutableDictionary<string, string>)(method.Invoke(null, []) ??
                throw new InvalidOperationException($"{methodName} returned null."));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static object InvokeGenericStaticMethod(Type helperType, string methodName, Type genericTypeArgument, object?[] arguments)
    {
        var method = helperType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing helper method '{methodName}'.");

        try
        {
            return method.MakeGenericMethod(genericTypeArgument).Invoke(null, arguments)
                   ?? throw new InvalidOperationException($"Helper method '{methodName}' returned null.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected exception '{typeof(TException).Name}' was not thrown.");
    }

    private static void AssertGeneratedFiles(GeneratorDriverRunResult result, int expectedCount)
    {
        var generatedSources = result.Results.SelectMany(static runResult => runResult.GeneratedSources).ToImmutableArray();
        if (generatedSources.Length == expectedCount)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {expectedCount} generated files, but found {generatedSources.Length}.");
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

    private static void AssertDiagnosticCount(GeneratorDriverRunResult result, string diagnosticId, int expectedCount)
    {
        var diagnostics = result.Results.SelectMany(static runResult => runResult.Diagnostics)
            .Where(diagnostic => diagnostic.Id == diagnosticId)
            .ToImmutableArray();
        if (diagnostics.Length == expectedCount)
        {
            return;
        }

        throw new InvalidOperationException($"Expected diagnostic '{diagnosticId}' to appear {expectedCount} times, but found {diagnostics.Length}.");
    }

    private static void AssertImplementsClosedGenericInterface(Type type, string genericInterfaceName)
    {
        var implemented = type.GetInterfaces().Any(interfaceType =>
            interfaceType.IsGenericType &&
            string.Equals(interfaceType.GetGenericTypeDefinition().FullName, genericInterfaceName, StringComparison.Ordinal) &&
            string.Equals(interfaceType.GenericTypeArguments[0].FullName, type.FullName, StringComparison.Ordinal));
        if (implemented)
        {
            return;
        }

        throw new InvalidOperationException($"Expected type '{type.FullName}' to implement '{genericInterfaceName}'.");
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

    private static void AssertDictionaryValue(ImmutableDictionary<string, string> dictionary, string key, string expectedValue)
    {
        if (!dictionary.TryGetValue(key, out var actualValue))
        {
            throw new InvalidOperationException($"Expected dictionary to contain key '{key}'.");
        }

        if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected key '{key}' to have value '{expectedValue}', but got '{actualValue}'.");
        }
    }

    private static void AssertDictionaryDoesNotContain(ImmutableDictionary<string, string> dictionary, string key)
    {
        if (dictionary.ContainsKey(key))
        {
            throw new InvalidOperationException($"Expected dictionary not to contain key '{key}'.");
        }
    }
}
