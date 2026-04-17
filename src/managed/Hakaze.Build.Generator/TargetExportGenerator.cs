using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hakaze.Build.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class TargetExportGenerator : IIncrementalGenerator
{
    private const string ExportTargetsAttributeMetadataName = "ExportTargetsAttribute";
    private const string PerProjectAttributeName = "PerProjectAttribute";
    private const string TargetAttributeName = "TargetAttribute";
    private const string DependOnAttributeName = "DependOnAttribute";
    private const string RetrievalAttributeName = "RetrievalAttribute";

    private static readonly SymbolDisplayFormat FullyQualifiedTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
                           .WithMiscellaneousOptions(
                               SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
                               SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static postInitializationContext =>
            postInitializationContext.AddSource(
                "Hakaze.Build.TargetAttributes.g.cs",
                SourceText.From(AttributeSource, Encoding.UTF8)));

        var exportedTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
                ExportTargetsAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => BuildExportedTypeModel(syntaxContext))
            .Where(static model => model is not null);

        context.RegisterSourceOutput(exportedTypes, static (productionContext, model) =>
        {
            foreach (var diagnostic in model!.Diagnostics)
            {
                productionContext.ReportDiagnostic(diagnostic);
            }

            if (!model.ShouldGenerate)
            {
                return;
            }

            productionContext.AddSource(
                model.HintName,
                SourceText.From(RenderSource(model), Encoding.UTF8));
        });
    }

    private static ExportedTypeModel? BuildExportedTypeModel(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetNode is not ClassDeclarationSyntax classDeclaration ||
            context.TargetSymbol is not INamedTypeSymbol typeSymbol)
        {
            return null;
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var typeDisplayName = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (typeSymbol.ContainingType is not null || typeSymbol.TypeParameters.Length > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.ExportTargetsTypeMustBeTopLevelNonGeneric,
                classDeclaration.Identifier.GetLocation(),
                typeDisplayName));
            return CreateInvalidModel(typeSymbol, diagnostics.ToImmutable());
        }

        if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.ExportTargetsTypeMustBePartial,
                classDeclaration.Identifier.GetLocation(),
                typeDisplayName));
            return CreateInvalidModel(typeSymbol, diagnostics.ToImmutable());
        }

        var isPerProject = HasAttribute(typeSymbol.GetAttributes(), PerProjectAttributeName);
        var targetMethods = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .Where(static method => HasAttribute(method.GetAttributes(), TargetAttributeName))
            .OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToImmutableArray();

        var methodBuilders = new List<TargetMethodBuilder>(targetMethods.Length);
        foreach (var method in targetMethods)
        {
            methodBuilders.Add(BuildTargetMethod(method, diagnostics));
        }

        var duplicateTargetNames = methodBuilders
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray(), StringComparer.Ordinal);

        foreach (var duplicateTargetName in duplicateTargetNames)
        {
            var targetName = duplicateTargetName.Key;
            var duplicatedMethods = duplicateTargetName.Value;
            foreach (var method in duplicatedMethods)
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.DuplicateTargetName,
                    method.Location,
                    targetName,
                    GetTypeDisplayName(typeSymbol)));
                method.MarkInvalid();
            }
        }

        var validTargetNames = methodBuilders
            .Where(static method => method.CanGenerate)
            .Select(static method => method.Name)
            .ToImmutableHashSet(StringComparer.Ordinal);

        foreach (var method in methodBuilders.Where(static method => method.CanGenerate))
        {
            foreach (var reference in method.UnknownReferenceCandidates)
            {
                if (string.IsNullOrWhiteSpace(reference.TargetName) || !validTargetNames.Contains(reference.TargetName))
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.UnknownTargetReference,
                        reference.Location,
                        reference.TargetName,
                        GetTypeDisplayName(typeSymbol)));
                    method.MarkInvalid();
                }
            }
        }

        var generatedMethods = methodBuilders
            .Where(static method => method.CanGenerate)
            .Select(static method => method.Build())
            .ToImmutableArray();

        return new ExportedTypeModel(
            namespaceName: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeName: typeSymbol.Name,
            typeAccessibility: GetAccessibility(typeSymbol.DeclaredAccessibility),
            factoryName: $"{typeSymbol.Name}Factory",
            fullyQualifiedTypeName: GetFullyQualifiedTypeName(typeSymbol),
            hintName: $"Hakaze.Build.Generator.{GetFullyQualifiedTypeName(typeSymbol).Replace('.', '_')}.g.cs",
            isPerProject: isPerProject,
            methods: generatedMethods,
            diagnostics: diagnostics.ToImmutable(),
            shouldGenerate: true);
    }

    private static ExportedTypeModel CreateInvalidModel(INamedTypeSymbol typeSymbol, ImmutableArray<Diagnostic> diagnostics)
    {
        return new ExportedTypeModel(
            namespaceName: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeName: typeSymbol.Name,
            typeAccessibility: GetAccessibility(typeSymbol.DeclaredAccessibility),
            factoryName: $"{typeSymbol.Name}Factory",
            fullyQualifiedTypeName: GetFullyQualifiedTypeName(typeSymbol),
            hintName: $"Hakaze.Build.Generator.{GetFullyQualifiedTypeName(typeSymbol).Replace('.', '_')}.g.cs",
            isPerProject: false,
            methods: [],
            diagnostics: diagnostics,
            shouldGenerate: false);
    }

    private static TargetMethodBuilder BuildTargetMethod(
        IMethodSymbol methodSymbol,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var builder = new TargetMethodBuilder(methodSymbol.Name, methodSymbol.Name, methodSymbol.Locations[0]);

        if (!methodSymbol.IsStatic)
        {
            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.TargetMethodMustBeStatic,
                methodSymbol.Locations[0],
                methodSymbol.Name));
            builder.MarkInvalid();
        }

        if (!ReturnsTaskOfExecutionResult(methodSymbol.ReturnType))
        {
            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.TargetMethodMustReturnTaskOfExecutionResult,
                methodSymbol.Locations[0],
                methodSymbol.Name));
            builder.MarkInvalid();
        }

        for (var index = 0; index < methodSymbol.Parameters.Length; index++)
        {
            var parameter = methodSymbol.Parameters[index];
            var location = parameter.Locations.FirstOrDefault(static candidate => candidate.IsInSource) ?? methodSymbol.Locations[0];

            if (IsType(parameter.Type, "Hakaze.Build.Abstractions", "IBuilding"))
            {
                if (index != 0)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.BuildingParameterMustBeFirst,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                }

                builder.Parameters.Add(new TargetParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(FullyQualifiedTypeFormat),
                    TargetParameterKind.Building,
                    null));
                continue;
            }

            if (IsType(parameter.Type, "System.Threading", "CancellationToken"))
            {
                if (index != methodSymbol.Parameters.Length - 1)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.CancellationTokenParameterMustBeLast,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                }

                builder.Parameters.Add(new TargetParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(FullyQualifiedTypeFormat),
                    TargetParameterKind.CancellationToken,
                    null));
                continue;
            }

            var retrievalAttributes = parameter.GetAttributes()
                .Where(static attribute => IsAttribute(attribute, RetrievalAttributeName))
                .ToImmutableArray();

            if (retrievalAttributes.Length > 1)
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.DuplicateRetrievalAttribute,
                    location,
                    parameter.Name));
                builder.MarkInvalid();
                continue;
            }

            if (retrievalAttributes.Length == 0)
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.RetrievalParameterRequired,
                    location,
                    parameter.Name));
                builder.MarkInvalid();
                continue;
            }

            var retrievalTargetName = retrievalAttributes[0].ConstructorArguments[0].Value as string;
            builder.Parameters.Add(new TargetParameterModel(
                parameter.Name,
                parameter.Type.ToDisplayString(FullyQualifiedTypeFormat),
                TargetParameterKind.Retrieval,
                retrievalTargetName));
            builder.UnknownReferenceCandidates.Add(new TargetReferenceModel(retrievalTargetName ?? string.Empty, location));
        }

        foreach (var attribute in methodSymbol.GetAttributes().Where(static attribute => IsAttribute(attribute, DependOnAttributeName)))
        {
            foreach (var constructorArgument in attribute.ConstructorArguments)
            {
                if (constructorArgument.Kind == TypedConstantKind.Array)
                {
                    foreach (var value in constructorArgument.Values)
                    {
                        if (value.Value is string dependencyName)
                        {
                            builder.ExplicitDependencies.Add(dependencyName);
                            builder.UnknownReferenceCandidates.Add(new TargetReferenceModel(
                                dependencyName,
                                attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? methodSymbol.Locations[0]));
                        }
                    }
                }
                else if (constructorArgument.Value is string dependencyName)
                {
                    builder.ExplicitDependencies.Add(dependencyName);
                    builder.UnknownReferenceCandidates.Add(new TargetReferenceModel(
                        dependencyName,
                        attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? methodSymbol.Locations[0]));
                }
            }
        }

        return builder;
    }

    private static string RenderSource(ExportedTypeModel model)
    {
        var sourceBuilder = new StringBuilder();
        sourceBuilder.AppendLine("// <auto-generated/>");
        sourceBuilder.AppendLine("#nullable enable");
        sourceBuilder.AppendLine();

        if (!string.IsNullOrEmpty(model.NamespaceName))
        {
            sourceBuilder.Append("namespace ").Append(model.NamespaceName).AppendLine(";");
            sourceBuilder.AppendLine();
        }

        sourceBuilder.Append(model.TypeAccessibility).Append(" partial class ").Append(model.TypeName).AppendLine();
        sourceBuilder.AppendLine("{");

        foreach (var method in model.Methods)
        {
            sourceBuilder.Append("    public const string ").Append(method.Name).Append("Name = nameof(").Append(method.Name).AppendLine(");");
            sourceBuilder.Append("    public static global::Hakaze.Build.Abstractions.TargetName ").Append(method.Name).Append("Id => new(").Append(method.Name).AppendLine("Name);");
            sourceBuilder.AppendLine();
        }

        sourceBuilder.AppendLine("    private static partial string GetTargetSourceId(global::Hakaze.Build.Abstractions.IBuilding building, global::System.Threading.CancellationToken cancellationToken);");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("    private static bool TryGetRetrievedValue<T>(");
        sourceBuilder.AppendLine("        global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?> collectedExecutionResults,");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId dependencyId,");
        sourceBuilder.AppendLine("        out T value,");
        sourceBuilder.AppendLine("        out global::Hakaze.Build.Abstractions.FailedExecution? failure)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        if (!collectedExecutionResults.TryGetValue(dependencyId, out var rawValue))");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            value = default!;");
        sourceBuilder.AppendLine("            failure = new global::Hakaze.Build.Abstractions.FailedExecution($\"Retrieved result for dependency '{dependencyId}' was not found.\", null);");
        sourceBuilder.AppendLine("            return false;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        if (rawValue is null)");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            if (default(T) is null)");
        sourceBuilder.AppendLine("            {");
        sourceBuilder.AppendLine("                value = default!;");
        sourceBuilder.AppendLine("                failure = null;");
        sourceBuilder.AppendLine("                return true;");
        sourceBuilder.AppendLine("            }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("            value = default!;");
        sourceBuilder.AppendLine("            failure = new global::Hakaze.Build.Abstractions.FailedExecution($\"Retrieved result for dependency '{dependencyId}' was null and cannot be assigned to '{typeof(T).FullName}'.\", null);");
        sourceBuilder.AppendLine("            return false;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        if (rawValue is T typedValue)");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            value = typedValue;");
        sourceBuilder.AppendLine("            failure = null;");
        sourceBuilder.AppendLine("            return true;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        value = default!;");
        sourceBuilder.AppendLine("        failure = new global::Hakaze.Build.Abstractions.FailedExecution($\"Retrieved result for dependency '{dependencyId}' could not be assigned to '{typeof(T).FullName}'.\", null);");
        sourceBuilder.AppendLine("        return false;");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("    public static global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.ITarget> GetTargets(");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.IBuilding building,");
        sourceBuilder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
        sourceBuilder.AppendLine("        var targets = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<global::Hakaze.Build.Abstractions.ITarget>();");
        sourceBuilder.AppendLine("        var targetSource = new global::Hakaze.Build.Abstractions.TargetSource(GetTargetSourceId(building, cancellationToken));");

        if (model.IsPerProject)
        {
            sourceBuilder.AppendLine("        foreach (var project in building.Projects)");
            sourceBuilder.AppendLine("        {");
            sourceBuilder.AppendLine("            var projectId = project.Id;");
            AppendTargetRegistrationBlock(sourceBuilder, model, "            ");
            sourceBuilder.AppendLine("        }");
        }
        else
        {
            sourceBuilder.Append("        var projectId = new global::Hakaze.Build.Abstractions.ProjectId(\"//global/")
                .Append(EscapeStringLiteral(model.FullyQualifiedTypeName))
                .AppendLine("\");");
            AppendTargetRegistrationBlock(sourceBuilder, model, "        ");
        }

        sourceBuilder.AppendLine("        return targets.ToImmutable();");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine();

        foreach (var method in model.Methods)
        {
            AppendWrapper(sourceBuilder, method);
            sourceBuilder.AppendLine();
        }

        sourceBuilder.AppendLine("}");
        sourceBuilder.AppendLine();
        sourceBuilder.Append(model.TypeAccessibility).Append(" sealed class ").Append(model.FactoryName).Append(" : global::Hakaze.Build.Abstractions.ITargetFactory").AppendLine();
        sourceBuilder.AppendLine("{");
        sourceBuilder.AppendLine("    public global::System.Threading.Tasks.Task<global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.ITarget>> GetTargetsAsync(");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.IBuilding building,");
        sourceBuilder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.Append("        return global::System.Threading.Tasks.Task.FromResult(").Append(model.TypeName).AppendLine(".GetTargets(building, cancellationToken));");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine("}");

        return sourceBuilder.ToString();
    }

    private static void AppendTargetRegistrationBlock(StringBuilder builder, ExportedTypeModel model, string indentation)
    {
        foreach (var method in model.Methods)
        {
            builder.Append(indentation).Append("var ").Append(method.IdVariableName).Append(" = new global::Hakaze.Build.Abstractions.TargetId(projectId, building.Config.Id, ")
                .Append(method.Name).AppendLine("Id, targetSource);");
        }

        if (model.Methods.Length > 0)
        {
            builder.AppendLine();
        }

        foreach (var method in model.Methods)
        {
            builder.Append(indentation).Append("targets.Add(new ").Append(method.WrapperName).Append('(')
                .Append(method.IdVariableName)
                .Append(", ");

            if (method.AllDependencyNames.Length == 0)
            {
                builder.Append("global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId>.Empty");
            }
            else
            {
                builder.Append("global::System.Collections.Immutable.ImmutableArray.Create(");
                for (var dependencyIndex = 0; dependencyIndex < method.AllDependencyNames.Length; dependencyIndex++)
                {
                    if (dependencyIndex > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(GetIdVariableName(method.AllDependencyNames[dependencyIndex]));
                }

                builder.Append(')');
            }

            foreach (var parameter in method.Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
            {
                builder.Append(", ").Append(GetIdVariableName(parameter.RetrievalTargetName!));
            }

            builder.AppendLine("));");
        }
    }

    private static void AppendWrapper(StringBuilder builder, TargetMethodModel method)
    {
        builder.Append("    private sealed class ").Append(method.WrapperName).Append(" : global::Hakaze.Build.Abstractions.ITarget").AppendLine();
        builder.AppendLine("    {");
        builder.AppendLine("        private readonly global::Hakaze.Build.Abstractions.TargetId _id;");
        builder.AppendLine("        private readonly global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId> _requiredPreparation;");

        foreach (var parameter in method.Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
        {
            builder.Append("        private readonly global::Hakaze.Build.Abstractions.TargetId ").Append(parameter.DependencyFieldName).AppendLine(";");
        }

        builder.AppendLine();
        builder.Append("        public ").Append(method.WrapperName).Append('(');
        builder.Append("global::Hakaze.Build.Abstractions.TargetId id, ");
        builder.Append("global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId> requiredPreparation");
        foreach (var parameter in method.Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
        {
            builder.Append(", global::Hakaze.Build.Abstractions.TargetId ").Append(parameter.DependencyArgumentName);
        }

        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            _id = id;");
        builder.AppendLine("            _requiredPreparation = requiredPreparation;");
        foreach (var parameter in method.Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
        {
            builder.Append("            ").Append(parameter.DependencyFieldName).Append(" = ").Append(parameter.DependencyArgumentName).AppendLine(";");
        }
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public global::Hakaze.Build.Abstractions.TargetId Id => _id;");
        builder.AppendLine();
        builder.AppendLine("        public global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId> RequiredPreparation => _requiredPreparation;");
        builder.AppendLine();
        builder.AppendLine("        public async global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ExecuteAsync(");
        builder.AppendLine("            global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
        builder.AppendLine("            global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?> collectedExecutionResults,");
        builder.AppendLine("            global::System.Threading.CancellationToken cancellationToken = default)");
        builder.AppendLine("        {");

        foreach (var parameter in method.Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
        {
            builder.Append("            if (!TryGetRetrievedValue<").Append(parameter.TypeName).Append(">(collectedExecutionResults, ")
                .Append(parameter.DependencyFieldName)
                .Append(", out var ")
                .Append(parameter.Name)
                .AppendLine(", out var failure))");
            builder.AppendLine("            {");
            builder.AppendLine("                return failure!;");
            builder.AppendLine("            }");
            builder.AppendLine();
        }

        builder.Append("            return await ").Append(method.Name).Append('(');
        for (var parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
        {
            if (parameterIndex > 0)
            {
                builder.Append(", ");
            }

            var parameter = method.Parameters[parameterIndex];
            builder.Append(parameter.Kind switch
            {
                TargetParameterKind.Building => "building",
                TargetParameterKind.CancellationToken => "cancellationToken",
                _ => parameter.Name
            });
        }

        builder.AppendLine(").ConfigureAwait(false);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static bool ReturnsTaskOfExecutionResult(ITypeSymbol returnType)
    {
        return returnType is INamedTypeSymbol namedType &&
               namedType.Name == "Task" &&
               namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
               namedType.TypeArguments.Length == 1 &&
               IsType(namedType.TypeArguments[0], "Hakaze.Build.Abstractions", "ExecutionResult");
    }

    private static bool IsType(ITypeSymbol typeSymbol, string @namespace, string name)
    {
        return typeSymbol.Name == name && typeSymbol.ContainingNamespace.ToDisplayString() == @namespace;
    }

    private static bool HasAttribute(ImmutableArray<AttributeData> attributes, string attributeName)
    {
        return attributes.Any(attribute => IsAttribute(attribute, attributeName));
    }

    private static bool IsAttribute(AttributeData attribute, string attributeName)
    {
        return attribute.AttributeClass?.Name == attributeName &&
               attribute.AttributeClass.ContainingNamespace.IsGlobalNamespace;
    }

    private static string GetAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "internal"
        };
    }

    private static string GetTypeDisplayName(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static string GetFullyQualifiedTypeName(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? typeSymbol.Name
            : $"{typeSymbol.ContainingNamespace.ToDisplayString()}.{typeSymbol.Name}";
    }

    private static string EscapeStringLiteral(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string GetIdVariableName(string methodName)
    {
        return char.ToLowerInvariant(methodName[0]) + methodName.Substring(1) + "TargetId";
    }

    private const string AttributeSource = """
// <auto-generated/>
#nullable enable

[global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ExportTargetsAttribute : global::System.Attribute
{
}

[global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PerProjectAttribute : global::System.Attribute
{
}

[global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TargetAttribute : global::System.Attribute
{
}

[global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class DependOnAttribute : global::System.Attribute
{
    public DependOnAttribute(params string[] targetNames)
    {
        TargetNames = targetNames ?? global::System.Array.Empty<string>();
    }

    public string[] TargetNames { get; }
}

[global::System.AttributeUsage(global::System.AttributeTargets.Parameter, Inherited = false, AllowMultiple = true)]
public sealed class RetrievalAttribute : global::System.Attribute
{
    public RetrievalAttribute(string targetName)
    {
        TargetName = targetName;
    }

    public string TargetName { get; }
}
""";

    private sealed class ExportedTypeModel
    {
        public ExportedTypeModel(
            string namespaceName,
            string typeName,
            string typeAccessibility,
            string factoryName,
            string fullyQualifiedTypeName,
            string hintName,
            bool isPerProject,
            ImmutableArray<TargetMethodModel> methods,
            ImmutableArray<Diagnostic> diagnostics,
            bool shouldGenerate)
        {
            NamespaceName = namespaceName;
            TypeName = typeName;
            TypeAccessibility = typeAccessibility;
            FactoryName = factoryName;
            FullyQualifiedTypeName = fullyQualifiedTypeName;
            HintName = hintName;
            IsPerProject = isPerProject;
            Methods = methods;
            Diagnostics = diagnostics;
            ShouldGenerate = shouldGenerate;
        }

        public string NamespaceName { get; }

        public string TypeName { get; }

        public string TypeAccessibility { get; }

        public string FactoryName { get; }

        public string FullyQualifiedTypeName { get; }

        public string HintName { get; }

        public bool IsPerProject { get; }

        public ImmutableArray<TargetMethodModel> Methods { get; }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public bool ShouldGenerate { get; }
    }

    private sealed class TargetMethodBuilder(string name, string methodName, Location location)
    {
        public string Name { get; } = name;

        public string MethodName { get; } = methodName;

        public Location Location { get; } = location;

        public List<TargetParameterModel> Parameters { get; } = [];

        public List<string> ExplicitDependencies { get; } = [];

        public List<TargetReferenceModel> UnknownReferenceCandidates { get; } = [];

        public bool CanGenerate { get; private set; } = true;

        public void MarkInvalid()
        {
            CanGenerate = false;
        }

        public TargetMethodModel Build()
        {
            var allDependencies = new List<string>();
            var seenDependencies = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dependency in ExplicitDependencies)
            {
                if (seenDependencies.Add(dependency))
                {
                    allDependencies.Add(dependency);
                }
            }

            foreach (var parameter in Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
            {
                if (seenDependencies.Add(parameter.RetrievalTargetName!))
                {
                    allDependencies.Add(parameter.RetrievalTargetName!);
                }
            }

            return new TargetMethodModel(
                Name,
                MethodName,
                $"{MethodName}GeneratedTarget",
                $"{char.ToLowerInvariant(MethodName[0])}{MethodName.Substring(1)}TargetId",
                Parameters.ToImmutableArray(),
                ExplicitDependencies.ToImmutableArray(),
                allDependencies.ToImmutableArray());
        }
    }

    private sealed class TargetMethodModel
    {
        public TargetMethodModel(
            string name,
            string methodName,
            string wrapperName,
            string idVariableName,
            ImmutableArray<TargetParameterModel> parameters,
            ImmutableArray<string> explicitDependencies,
            ImmutableArray<string> allDependencyNames)
        {
            Name = name;
            MethodName = methodName;
            WrapperName = wrapperName;
            IdVariableName = idVariableName;
            Parameters = parameters;
            ExplicitDependencies = explicitDependencies;
            AllDependencyNames = allDependencyNames;
        }

        public string Name { get; }

        public string MethodName { get; }

        public string WrapperName { get; }

        public string IdVariableName { get; }

        public ImmutableArray<TargetParameterModel> Parameters { get; }

        public ImmutableArray<string> ExplicitDependencies { get; }

        public ImmutableArray<string> AllDependencyNames { get; }
    }

    private sealed class TargetParameterModel
    {
        public TargetParameterModel(
            string name,
            string typeName,
            TargetParameterKind kind,
            string? retrievalTargetName)
        {
            Name = name;
            TypeName = typeName;
            Kind = kind;
            RetrievalTargetName = retrievalTargetName;
        }

        public string Name { get; }

        public string TypeName { get; }

        public TargetParameterKind Kind { get; }

        public string? RetrievalTargetName { get; }

        public string DependencyFieldName => $"_{Name}DependencyId";

        public string DependencyArgumentName => $"{Name}DependencyId";
    }

    private sealed class TargetReferenceModel
    {
        public TargetReferenceModel(string targetName, Location location)
        {
            TargetName = targetName;
            Location = location;
        }

        public string TargetName { get; }

        public Location Location { get; }
    }

    private enum TargetParameterKind
    {
        Building,
        Retrieval,
        CancellationToken
    }
}
