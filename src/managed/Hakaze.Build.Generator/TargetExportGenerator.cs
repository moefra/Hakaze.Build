using System.Collections.Immutable;
using System.Text;
using Hakaze.Build.Utility.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hakaze.Build.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class TargetExportGenerator : IIncrementalGenerator
{
    private const string ExportTargetsAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.ExportTargetsAttribute";
    private const string ExportTargetsInterfaceMetadataName = "Hakaze.Build.Abstractions.Generator.IExportTargets";
    private const string PerProjectAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.PerProjectAttribute";
    private const string TargetAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.TargetAttribute";
    private const string TargetFactoryAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.TargetFactoryAttribute";
    private const string TargetSourceAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.TargetSourceAttribute";
    private const string DependOnAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.DependOnAttribute";
    private const string RetrievalAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.RetrievalAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
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
        var typeDisplayName = GeneratorRoslynUtilities.GetTypeDisplayName(typeSymbol);
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

        var isPerProject = GeneratorRoslynUtilities.HasAttribute(typeSymbol.GetAttributes(), PerProjectAttributeMetadataName);
        foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>().Where(static method => method.MethodKind == MethodKind.Ordinary))
        {
            if (GeneratorRoslynUtilities.HasAttribute(method.GetAttributes(), TargetSourceAttributeMetadataName) &&
                !GeneratorRoslynUtilities.HasAttribute(method.GetAttributes(), TargetAttributeMetadataName))
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.TargetSourceAttributeRequiresTargetMethod,
                    method.Locations[0],
                    method.Name));
            }
        }

        var targetMethods = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .Where(static method => GeneratorRoslynUtilities.HasAttribute(method.GetAttributes(), TargetAttributeMetadataName))
            .OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToImmutableArray();
        var targetFactoryMethods = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .Where(static method => GeneratorRoslynUtilities.HasAttribute(method.GetAttributes(), TargetFactoryAttributeMetadataName))
            .OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToImmutableArray();

        var methodBuilders = new List<TargetMethodBuilder>(targetMethods.Length);
        foreach (var method in targetMethods)
        {
            methodBuilders.Add(BuildTargetMethod(method, diagnostics));
        }

        var targetFactoryBuilders = new List<TargetFactoryMethodBuilder>(targetFactoryMethods.Length);
        foreach (var method in targetFactoryMethods)
        {
            targetFactoryBuilders.Add(BuildTargetFactoryMethod(method, diagnostics));
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
                    GeneratorRoslynUtilities.GetTypeDisplayName(typeSymbol)));
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
                        GeneratorRoslynUtilities.GetTypeDisplayName(typeSymbol)));
                    method.MarkInvalid();
                }
            }
        }

        foreach (var method in methodBuilders.Where(static method => method.CanGenerate))
        {
            if (method.TargetSourceResolverName is null)
            {
                continue;
            }

            var resolverMethods = typeSymbol.GetMembers(method.TargetSourceResolverName)
                .OfType<IMethodSymbol>()
                .Where(static candidate => candidate.MethodKind == MethodKind.Ordinary)
                .ToImmutableArray();

            if (resolverMethods.Length == 0)
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.UnknownTargetSourceResolver,
                    method.TargetSourceResolverLocation ?? method.Location,
                    method.TargetSourceResolverName,
                    method.MethodName));
                method.MarkInvalid();
                continue;
            }

            if (!resolverMethods.Any(static candidate => candidate.IsStatic))
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.TargetSourceResolverMustBeStatic,
                    method.TargetSourceResolverLocation ?? method.Location,
                    method.TargetSourceResolverName,
                    method.MethodName));
                method.MarkInvalid();
                continue;
            }

            var staticResolvers = resolverMethods.Where(static candidate => candidate.IsStatic).ToImmutableArray();
            if (!staticResolvers.Any(static candidate => IsType(candidate.ReturnType, "System", "String")))
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.TargetSourceResolverMustReturnString,
                    method.TargetSourceResolverLocation ?? method.Location,
                    method.TargetSourceResolverName,
                    method.MethodName));
                method.MarkInvalid();
                continue;
            }

            var validResolver = staticResolvers.FirstOrDefault(IsTargetSourceResolverSignatureValid);
            if (validResolver is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.TargetSourceResolverHasInvalidParameters,
                    method.TargetSourceResolverLocation ?? method.Location,
                    method.TargetSourceResolverName,
                    method.MethodName));
                method.MarkInvalid();
                continue;
            }
        }

        var generatedMethods = methodBuilders
            .Where(static method => method.CanGenerate)
            .Select(static method => method.Build())
            .ToImmutableArray();
        var generatedTargetFactories = targetFactoryBuilders
            .Where(static method => method.CanGenerate)
            .Select(static method => method.Build())
            .ToImmutableArray();
        var exportTargetsInterfaceSymbol = context.SemanticModel.Compilation.GetTypeByMetadataName(ExportTargetsInterfaceMetadataName);
        var shouldImplementExportTargetsInterface = exportTargetsInterfaceSymbol is not null &&
                                                    !GeneratorRoslynUtilities.ImplementsInterface(typeSymbol, exportTargetsInterfaceSymbol);

        return new ExportedTypeModel(
            namespaceName: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeName: typeSymbol.Name,
            typeAccessibility: GeneratorRoslynUtilities.GetAccessibility(typeSymbol.DeclaredAccessibility),
            factoryName: $"{typeSymbol.Name}Factory",
            fullyQualifiedTypeName: GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol),
            hintName: $"Hakaze.Build.Generator.{GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol).Replace('.', '_')}.g.cs",
            isPerProject: isPerProject,
            methods: generatedMethods,
            targetFactoryMethods: generatedTargetFactories,
            diagnostics: diagnostics.ToImmutable(),
            shouldImplementExportTargetsInterface: shouldImplementExportTargetsInterface,
            shouldGenerate: true);
    }

    private static ExportedTypeModel CreateInvalidModel(INamedTypeSymbol typeSymbol, ImmutableArray<Diagnostic> diagnostics)
    {
        return new ExportedTypeModel(
            namespaceName: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeName: typeSymbol.Name,
            typeAccessibility: GeneratorRoslynUtilities.GetAccessibility(typeSymbol.DeclaredAccessibility),
            factoryName: $"{typeSymbol.Name}Factory",
            fullyQualifiedTypeName: GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol),
            hintName: $"Hakaze.Build.Generator.{GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol).Replace('.', '_')}.g.cs",
            isPerProject: false,
            methods: [],
            targetFactoryMethods: [],
            diagnostics: diagnostics,
            shouldImplementExportTargetsInterface: false,
            shouldGenerate: false);
    }

    private static TargetMethodBuilder BuildTargetMethod(
        IMethodSymbol methodSymbol,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var builder = new TargetMethodBuilder(methodSymbol.Name, methodSymbol.Name, methodSymbol.Locations[0]);
        var hasEvaluatedBuildingParameter = false;
        var hasCollectedExecutionResultsParameter = false;
        var hasCancellationTokenParameter = false;

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

        var targetSourceAttribute = methodSymbol.GetAttributes()
            .FirstOrDefault(static attribute => GeneratorRoslynUtilities.IsAttribute(attribute, TargetSourceAttributeMetadataName));
        if (targetSourceAttribute is not null)
        {
            builder.TargetSourceResolverName = targetSourceAttribute.ConstructorArguments.FirstOrDefault().Value as string;
            builder.TargetSourceResolverLocation = targetSourceAttribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? methodSymbol.Locations[0];
        }

        for (var index = 0; index < methodSymbol.Parameters.Length; index++)
        {
            var parameter = methodSymbol.Parameters[index];
            var location = parameter.Locations.FirstOrDefault(static candidate => candidate.IsInSource) ?? methodSymbol.Locations[0];

            if (IsType(parameter.Type, "Hakaze.Build.Abstractions", "IBuilding"))
            {
                diagnostics.Add(Diagnostic.Create(
                    TargetExportDiagnostics.BuildingParameterIsNotSupported,
                    location,
                    methodSymbol.Name));
                builder.MarkInvalid();
                continue;
            }

            if (IsType(parameter.Type, "Hakaze.Build.Abstractions", "IEvaluatedBuilding"))
            {
                if (hasEvaluatedBuildingParameter)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.DuplicateEvaluatedBuildingParameter,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                    continue;
                }

                hasEvaluatedBuildingParameter = true;
                builder.Parameters.Add(new TargetParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
                    TargetParameterKind.EvaluatedBuilding,
                    null));
                continue;
            }

            if (IsCollectedExecutionResultsType(parameter.Type))
            {
                if (hasCollectedExecutionResultsParameter)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.DuplicateCollectedExecutionResultsParameter,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                    continue;
                }

                hasCollectedExecutionResultsParameter = true;
                builder.Parameters.Add(new TargetParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
                    TargetParameterKind.CollectedExecutionResults,
                    null));
                continue;
            }

            if (IsType(parameter.Type, "System.Threading", "CancellationToken"))
            {
                if (hasCancellationTokenParameter)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.DuplicateCancellationTokenParameter,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                    continue;
                }

                hasCancellationTokenParameter = true;
                builder.Parameters.Add(new TargetParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
                    TargetParameterKind.CancellationToken,
                    null));
                continue;
            }

            var retrievalAttributes = parameter.GetAttributes()
                .Where(static attribute => GeneratorRoslynUtilities.IsAttribute(attribute, RetrievalAttributeMetadataName))
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
                parameter.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
                TargetParameterKind.Retrieval,
                retrievalTargetName));
            builder.UnknownReferenceCandidates.Add(new TargetReferenceModel(retrievalTargetName ?? string.Empty, location));
        }

        foreach (var attribute in methodSymbol.GetAttributes().Where(static attribute => GeneratorRoslynUtilities.IsAttribute(attribute, DependOnAttributeMetadataName)))
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

    private static TargetFactoryMethodBuilder BuildTargetFactoryMethod(
        IMethodSymbol methodSymbol,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var builder = new TargetFactoryMethodBuilder(methodSymbol.Name, methodSymbol.Locations[0]);
        var targetFactoryAttribute = methodSymbol.GetAttributes()
            .FirstOrDefault(static attribute => GeneratorRoslynUtilities.IsAttribute(attribute, TargetFactoryAttributeMetadataName));
        builder.HelperTargetName = targetFactoryAttribute?.ConstructorArguments.FirstOrDefault().Value as string;
        var hasBuildingParameter = false;
        var hasCancellationTokenParameter = false;

        if (!methodSymbol.IsStatic)
        {
            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.TargetFactoryMethodMustBeStatic,
                methodSymbol.Locations[0],
                methodSymbol.Name));
            builder.MarkInvalid();
        }

        if (!ReturnsTaskOfImmutableArrayOfITarget(methodSymbol.ReturnType))
        {
            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.TargetFactoryMethodMustReturnTaskOfImmutableArrayOfITarget,
                methodSymbol.Locations[0],
                methodSymbol.Name));
            builder.MarkInvalid();
        }

        foreach (var parameter in methodSymbol.Parameters)
        {
            var location = parameter.Locations.FirstOrDefault(static candidate => candidate.IsInSource) ?? methodSymbol.Locations[0];

            if (IsType(parameter.Type, "Hakaze.Build.Abstractions", "IBuilding"))
            {
                if (hasBuildingParameter)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.DuplicateFactoryBuildingParameter,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                    continue;
                }

                hasBuildingParameter = true;
                builder.Parameters.Add(new TargetFactoryParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
                    TargetFactoryParameterKind.Building));
                continue;
            }

            if (IsType(parameter.Type, "System.Threading", "CancellationToken"))
            {
                if (hasCancellationTokenParameter)
                {
                    diagnostics.Add(Diagnostic.Create(
                        TargetExportDiagnostics.DuplicateFactoryCancellationTokenParameter,
                        location,
                        methodSymbol.Name));
                    builder.MarkInvalid();
                    continue;
                }

                hasCancellationTokenParameter = true;
                builder.Parameters.Add(new TargetFactoryParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
                    TargetFactoryParameterKind.CancellationToken));
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                TargetExportDiagnostics.UnsupportedTargetFactoryParameter,
                location,
                parameter.Name,
                methodSymbol.Name));
            builder.MarkInvalid();
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

        sourceBuilder.Append(model.TypeAccessibility).Append(" partial class ").Append(model.TypeName);
        if (model.ShouldImplementExportTargetsInterface)
        {
            sourceBuilder.Append(" : global::Hakaze.Build.Abstractions.Generator.IExportTargets");
        }

        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("{");

        foreach (var method in model.Methods)
        {
            sourceBuilder.Append("    public const string ").Append(method.Name).Append("Name = \"")
                .Append(CSharpStringLiteral.Escape(model.FullyQualifiedTypeName))
                .Append('.')
                .Append(CSharpStringLiteral.Escape(method.MethodName))
                .AppendLine("\";");
            sourceBuilder.Append("    public static global::Hakaze.Build.Abstractions.TargetName ").Append(method.Name).Append("Id => new(").Append(method.Name).AppendLine("Name);");
            sourceBuilder.AppendLine();
        }

        foreach (var targetFactoryMethod in model.TargetFactoryMethods)
        {
            if (!targetFactoryMethod.HasNamedHelper)
            {
                continue;
            }

            var helperTargetName = targetFactoryMethod.HelperTargetName!;

            sourceBuilder.Append("    public const string ").Append(helperTargetName).Append("Name = \"")
                .Append(CSharpStringLiteral.Escape(model.FullyQualifiedTypeName))
                .Append('.')
                .Append(CSharpStringLiteral.Escape(helperTargetName))
                .AppendLine("\";");
            sourceBuilder.Append("    public static global::Hakaze.Build.Abstractions.TargetName ").Append(helperTargetName).Append("Id => new(")
                .Append(helperTargetName).AppendLine("Name);");
            sourceBuilder.AppendLine();
        }

        foreach (var method in model.Methods)
        {
            sourceBuilder.Append("    public delegate global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokerDelegateName)
                .Append('(');
            AppendParameterSignature(sourceBuilder, method.Parameters);
            sourceBuilder.AppendLine(");");
            sourceBuilder.AppendLine();
        }

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
        sourceBuilder.AppendLine("    private static global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?> PublishExecutionResult(");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId targetId,");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.ExecutionResult result,");
        sourceBuilder.AppendLine("        global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?> dependencyResults)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        var builder = dependencyResults.ToBuilder();");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        if (TryExtractExecutionResultValue(result, out var value))");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            builder[targetId] = value;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        return builder.ToImmutable();");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("    private static bool TryExtractExecutionResultValue(");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.ExecutionResult result,");
        sourceBuilder.AppendLine("        out object? value)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        var resultType = result.GetType();");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        if (resultType.IsGenericType &&");
        sourceBuilder.AppendLine("            resultType.GetGenericTypeDefinition() == typeof(global::Hakaze.Build.Abstractions.SuccessfulExecutionWithResult<>))");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            value = resultType");
        sourceBuilder.AppendLine("                .GetProperty(nameof(global::Hakaze.Build.Abstractions.SuccessfulExecutionWithResult<object>.ExecutionResult))!");
        sourceBuilder.AppendLine("                .GetValue(result);");
        sourceBuilder.AppendLine("            return true;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        if (resultType.IsGenericType &&");
        sourceBuilder.AppendLine("            resultType.GetGenericTypeDefinition() == typeof(global::Hakaze.Build.Abstractions.CachedExecution<>))");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            value = resultType");
        sourceBuilder.AppendLine("                .GetProperty(nameof(global::Hakaze.Build.Abstractions.CachedExecution<object>.CachedResult))!");
        sourceBuilder.AppendLine("                .GetValue(result);");
        sourceBuilder.AppendLine("            return true;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        if (result is global::Hakaze.Build.Abstractions.SkippedExecution or global::Hakaze.Build.Abstractions.SuccessfulExecution)");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            value = null;");
        sourceBuilder.AppendLine("            return true;");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        value = null;");
        sourceBuilder.AppendLine("        return false;");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("    private static async global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ExecuteGeneratedTargetAsync(");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId rootId,");
        sourceBuilder.AppendLine("        global::System.Threading.CancellationToken cancellationToken,");
        sourceBuilder.AppendLine("        global::System.Func<global::Hakaze.Build.Abstractions.TargetId, global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?>, global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult>> rootExecutor)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
        sourceBuilder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(rootExecutor);");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        var executionResults = new global::System.Collections.Generic.Dictionary<global::Hakaze.Build.Abstractions.TargetId, global::Hakaze.Build.Abstractions.ExecutionResult>();");
        sourceBuilder.AppendLine("        var publishedResults = new global::System.Collections.Generic.Dictionary<global::Hakaze.Build.Abstractions.TargetId, global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?>>();");
        sourceBuilder.AppendLine("        return await ExecuteCore(rootId, new global::System.Collections.Generic.HashSet<global::Hakaze.Build.Abstractions.TargetId>()).ConfigureAwait(false);");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("        async global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ExecuteCore(");
        sourceBuilder.AppendLine("            global::Hakaze.Build.Abstractions.TargetId currentId,");
        sourceBuilder.AppendLine("            global::System.Collections.Generic.HashSet<global::Hakaze.Build.Abstractions.TargetId> stack)");
        sourceBuilder.AppendLine("        {");
        sourceBuilder.AppendLine("            if (executionResults.TryGetValue(currentId, out var cached))");
        sourceBuilder.AppendLine("            {");
        sourceBuilder.AppendLine("                return cached;");
        sourceBuilder.AppendLine("            }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("            if (!building.Targets.TryGetValue(currentId, out var target))");
        sourceBuilder.AppendLine("            {");
        sourceBuilder.AppendLine("                throw new global::System.InvalidOperationException($\"Target '{currentId}' was not found.\");");
        sourceBuilder.AppendLine("            }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("            if (!stack.Add(currentId))");
        sourceBuilder.AppendLine("            {");
        sourceBuilder.AppendLine("                throw new global::System.InvalidOperationException($\"Cyclic target dependency detected for '{currentId}'.\");");
        sourceBuilder.AppendLine("            }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("            try");
        sourceBuilder.AppendLine("            {");
        sourceBuilder.AppendLine("                var collectedExecutionResults = global::System.Collections.Immutable.ImmutableDictionary.CreateBuilder<global::Hakaze.Build.Abstractions.TargetId, object?>();");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("                foreach (var dependencyId in target.RequiredPreparation)");
        sourceBuilder.AppendLine("                {");
        sourceBuilder.AppendLine("                    var dependencyResult = await ExecuteCore(dependencyId, stack).ConfigureAwait(false);");
        sourceBuilder.AppendLine("                    if (dependencyResult is global::Hakaze.Build.Abstractions.FailedExecution)");
        sourceBuilder.AppendLine("                    {");
        sourceBuilder.AppendLine("                        executionResults[currentId] = dependencyResult;");
        sourceBuilder.AppendLine("                        return dependencyResult;");
        sourceBuilder.AppendLine("                    }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("                    if (publishedResults.TryGetValue(dependencyId, out var dependencyValues))");
        sourceBuilder.AppendLine("                    {");
        sourceBuilder.AppendLine("                        foreach (var (resultId, resultValue) in dependencyValues)");
        sourceBuilder.AppendLine("                        {");
        sourceBuilder.AppendLine("                            collectedExecutionResults[resultId] = resultValue;");
        sourceBuilder.AppendLine("                        }");
        sourceBuilder.AppendLine("                    }");
        sourceBuilder.AppendLine("                }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("                var collectedResults = collectedExecutionResults.ToImmutable();");
        sourceBuilder.AppendLine("                var result = currentId == rootId");
        sourceBuilder.AppendLine("                    ? await rootExecutor(currentId, collectedResults, cancellationToken).ConfigureAwait(false)");
        sourceBuilder.AppendLine("                    : await target.ExecuteAsync(building, collectedResults, cancellationToken).ConfigureAwait(false);");
        sourceBuilder.AppendLine("                executionResults[currentId] = result;");
        sourceBuilder.AppendLine("                publishedResults[currentId] = PublishExecutionResult(currentId, result, collectedResults);");
        sourceBuilder.AppendLine("                return result;");
        sourceBuilder.AppendLine("            }");
        sourceBuilder.AppendLine("            finally");
        sourceBuilder.AppendLine("            {");
        sourceBuilder.AppendLine("                stack.Remove(currentId);");
        sourceBuilder.AppendLine("            }");
        sourceBuilder.AppendLine("        }");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine("    public static async global::System.Threading.Tasks.Task<global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.ITarget>> GetTargetsAsync(");
        sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.IBuilding building,");
        sourceBuilder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
        sourceBuilder.AppendLine("    {");
        sourceBuilder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
        sourceBuilder.AppendLine("        var targets = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<global::Hakaze.Build.Abstractions.ITarget>();");

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
            sourceBuilder.AppendLine("        global::Hakaze.Build.Abstractions.ProjectId? projectId = null;");
            AppendTargetRegistrationBlock(sourceBuilder, model, "        ");
        }

        AppendTargetFactoryRegistrationBlock(sourceBuilder, model, "        ");

        sourceBuilder.AppendLine("        return targets.ToImmutable();");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine();

        foreach (var method in model.Methods)
        {
            AppendInvokeApi(sourceBuilder, model, method);
            sourceBuilder.AppendLine();
            AppendInvokeCore(sourceBuilder, method);
            sourceBuilder.AppendLine();
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
        sourceBuilder.Append("        return ").Append(model.TypeName).AppendLine(".GetTargetsAsync(building, cancellationToken);");
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine("}");

        return sourceBuilder.ToString();
    }

    private static void AppendTargetRegistrationBlock(StringBuilder builder, ExportedTypeModel model, string indentation)
    {
        foreach (var method in model.Methods)
        {
            builder.Append(indentation).Append("var ").Append(method.IdVariableName).Append(" = ")
                .Append(method.CreateTargetIdMethodName)
                .AppendLine("(projectId, building, cancellationToken);");
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

            builder.AppendLine("));");
        }

    }

    private static void AppendTargetFactoryRegistrationBlock(StringBuilder builder, ExportedTypeModel model, string indentation)
    {
        foreach (var factoryMethod in model.TargetFactoryMethods)
        {
            builder.Append(indentation).Append("targets.AddRange(await ").Append(factoryMethod.MethodName).Append('(');
            AppendTargetFactoryInvocationArguments(builder, factoryMethod.Parameters);
            builder.AppendLine(").ConfigureAwait(false));");
        }
    }

    private static void AppendInvokeApi(StringBuilder builder, ExportedTypeModel model, TargetMethodModel method)
    {
        if (model.IsPerProject)
        {
            builder.Append("    public static global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokeMethodName)
                .AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
            builder.Append("        return ").Append(method.InvokeMethodName).Append("(building, ")
                .Append(method.ResolveTargetIdMethodName)
                .AppendLine("(building, cancellationToken), cancellationToken, ")
                .Append(method.MethodName)
                .AppendLine(");");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    public static global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokeMethodName)
                .AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken,");
            builder.Append("        ").Append(method.InvokerDelegateName).AppendLine(" implementation)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(implementation);");
            builder.Append("        return ").Append(method.InvokeMethodName).Append("(building, ")
                .Append(method.ResolveTargetIdMethodName)
                .AppendLine("(building, cancellationToken), cancellationToken, implementation);");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    public static global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokeMethodName)
                .AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId targetId,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
            builder.Append("        return ").Append(method.InvokeMethodName).Append("(building, targetId, cancellationToken, ")
                .Append(method.MethodName)
                .AppendLine(");");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    public static global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokeMethodName)
                .AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId targetId,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken,");
            builder.Append("        ").Append(method.InvokerDelegateName).AppendLine(" implementation)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(implementation);");
            builder.Append("        ").Append(method.ValidateTargetIdMethodName).AppendLine("(building, targetId, cancellationToken);");
            builder.Append("        return ExecuteGeneratedTargetAsync(building, targetId, cancellationToken, (rootId, collectedExecutionResults, token) => ")
                .Append(method.InvokeCoreMethodName)
                .AppendLine("(rootId, building, collectedExecutionResults, token, implementation));");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    private static global::Hakaze.Build.Abstractions.TargetId ").Append(method.ResolveTargetIdMethodName).AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.Append("        global::Hakaze.Build.Abstractions.TargetId? matchedTargetId = null;").AppendLine();
            builder.AppendLine();
            builder.AppendLine("        foreach (var targetId in building.Targets.Keys)");
            builder.AppendLine("        {");
            builder.Append("            if (targetId != ").Append(method.CreateTargetIdMethodName).AppendLine("(targetId.ProjectId, building, cancellationToken))");
            builder.AppendLine("            {");
                builder.AppendLine("                continue;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (matchedTargetId is not null)");
            builder.AppendLine("            {");
            builder.Append("                throw new global::System.InvalidOperationException(\"Target '").Append(method.Name).Append("' is exported per project; use ")
                .Append(method.InvokeMethodName).AppendLine("(building, targetId, ...) to select a concrete target.\");");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            matchedTargetId = targetId;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (matchedTargetId is null)");
            builder.AppendLine("        {");
            builder.Append("            throw new global::System.InvalidOperationException(\"Target '").Append(method.Name).AppendLine("' was not found.\");");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        return matchedTargetId.Value;");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    private static void ").Append(method.ValidateTargetIdMethodName).AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId targetId,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.Append("        if (targetId != ").Append(method.CreateTargetIdMethodName).AppendLine("(targetId.ProjectId, building, cancellationToken))");
            builder.AppendLine("        {");
            builder.Append("            throw new global::System.ArgumentException(\"TargetId does not identify target '").Append(method.Name).AppendLine("' for the current build.\", nameof(targetId));");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    private static global::Hakaze.Build.Abstractions.TargetId ").Append(method.CreateTargetIdMethodName).AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.ProjectId? projectId,");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.Append("        return new global::Hakaze.Build.Abstractions.TargetId(projectId, null, ")
                .Append(method.Name)
                .Append("Id, ")
                .Append(GetTargetSourceExpression(method))
                .AppendLine(");");
            builder.AppendLine("    }");
        }
        else
        {
            builder.Append("    public static global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokeMethodName)
                .AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.Append("        return ").Append(method.InvokeMethodName).Append("(building, cancellationToken, ").Append(method.MethodName).AppendLine(");");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    public static global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
                .Append(method.InvokeMethodName)
                .AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken,");
            builder.Append("        ").Append(method.InvokerDelegateName).AppendLine(" implementation)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(implementation);");
            builder.Append("        var targetId = ").Append(method.CreateTargetIdMethodName).AppendLine("(null, building, cancellationToken);");
            builder.Append("        return ExecuteGeneratedTargetAsync(building, targetId, cancellationToken, (rootId, collectedExecutionResults, token) => ")
                .Append(method.InvokeCoreMethodName)
                .AppendLine("(rootId, building, collectedExecutionResults, token, implementation));");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    private static global::Hakaze.Build.Abstractions.TargetId ").Append(method.CreateTargetIdMethodName).AppendLine("(");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.ProjectId? projectId,");
            builder.AppendLine("        global::Hakaze.Build.Abstractions.IBuilding building,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.Append("        return new global::Hakaze.Build.Abstractions.TargetId(projectId, null, ")
                .Append(method.Name)
                .Append("Id, ")
                .Append(GetTargetSourceExpression(method))
                .AppendLine(");");
            builder.AppendLine("    }");
        }
    }

    private static void AppendInvokeCore(StringBuilder builder, TargetMethodModel method)
    {
        builder.Append("    private static async global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ")
            .Append(method.InvokeCoreMethodName)
            .AppendLine("(");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.TargetId targetId,");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
        builder.AppendLine("        global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?> collectedExecutionResults,");
        builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken,");
        builder.Append("        ").Append(method.InvokerDelegateName).AppendLine(" implementation)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(building);");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(implementation);");
        builder.AppendLine();

        foreach (var parameter in method.Parameters.Where(static parameter => parameter.Kind == TargetParameterKind.Retrieval))
        {
            builder.Append("        if (!TryGetRetrievedValue<").Append(parameter.TypeName).Append(">(collectedExecutionResults, ")
                .Append(GetCreateTargetIdMethodName(parameter.RetrievalTargetName!))
                .AppendLine("(targetId.ProjectId, building, cancellationToken), out var ")
                .Append(parameter.Name)
                .AppendLine(", out var failure))");
            builder.AppendLine("        {");
            builder.AppendLine("            return failure!;");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        builder.Append("        return await implementation(");
        AppendInvocationArguments(builder, method.Parameters);
        builder.AppendLine(").ConfigureAwait(false);");
        builder.AppendLine("    }");
    }

    private static void AppendWrapper(StringBuilder builder, TargetMethodModel method)
    {
        builder.Append("    private sealed class ").Append(method.WrapperName).Append(" : global::Hakaze.Build.Abstractions.ITarget").AppendLine();
        builder.AppendLine("    {");
        builder.AppendLine("        private readonly global::Hakaze.Build.Abstractions.TargetId _id;");
        builder.AppendLine("        private readonly global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId> _requiredPreparation;");

        builder.AppendLine();
        builder.Append("        public ").Append(method.WrapperName).Append('(');
        builder.Append("global::Hakaze.Build.Abstractions.TargetId id, ");
        builder.Append("global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId> requiredPreparation");
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            _id = id;");
        builder.AppendLine("            _requiredPreparation = requiredPreparation;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public global::Hakaze.Build.Abstractions.TargetId Id => _id;");
        builder.AppendLine();
        builder.AppendLine("        public global::System.Collections.Immutable.ImmutableArray<global::Hakaze.Build.Abstractions.TargetId> RequiredPreparation => _requiredPreparation;");
        builder.AppendLine();
        builder.AppendLine("        public global::System.Threading.Tasks.Task<global::Hakaze.Build.Abstractions.ExecutionResult> ExecuteAsync(");
        builder.AppendLine("            global::Hakaze.Build.Abstractions.IEvaluatedBuilding building,");
        builder.AppendLine("            global::System.Collections.Immutable.ImmutableDictionary<global::Hakaze.Build.Abstractions.TargetId, object?> collectedExecutionResults,");
        builder.AppendLine("            global::System.Threading.CancellationToken cancellationToken = default)");
        builder.AppendLine("        {");
        builder.Append("            return ").Append(method.InvokeCoreMethodName).AppendLine("(_id, building, collectedExecutionResults, cancellationToken, " + method.MethodName + ");");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static void AppendParameterSignature(StringBuilder builder, ImmutableArray<TargetParameterModel> parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(parameters[index].TypeName).Append(' ').Append(parameters[index].Name);
        }
    }

    private static void AppendInvocationArguments(StringBuilder builder, ImmutableArray<TargetParameterModel> parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var parameter = parameters[index];
            builder.Append(parameter.Kind switch
            {
                TargetParameterKind.EvaluatedBuilding => "building",
                TargetParameterKind.CollectedExecutionResults => "collectedExecutionResults",
                TargetParameterKind.CancellationToken => "cancellationToken",
                _ => parameter.Name
            });
        }
    }

    private static void AppendTargetFactoryInvocationArguments(StringBuilder builder, ImmutableArray<TargetFactoryParameterModel> parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(parameters[index].Kind switch
            {
                TargetFactoryParameterKind.Building => "building",
                TargetFactoryParameterKind.CancellationToken => "cancellationToken",
                _ => throw new InvalidOperationException("Unknown target factory parameter kind.")
            });
        }
    }

    private static bool ReturnsTaskOfExecutionResult(ITypeSymbol returnType)
    {
        return returnType is INamedTypeSymbol namedType &&
               namedType.Name == "Task" &&
               namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
               namedType.TypeArguments.Length == 1 &&
               IsType(namedType.TypeArguments[0], "Hakaze.Build.Abstractions", "ExecutionResult");
    }

    private static bool ReturnsTaskOfImmutableArrayOfITarget(ITypeSymbol returnType)
    {
        return returnType is INamedTypeSymbol namedType &&
               namedType.Name == "Task" &&
               namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
               namedType.TypeArguments.Length == 1 &&
               namedType.TypeArguments[0] is INamedTypeSymbol immutableArrayType &&
               immutableArrayType.Name == "ImmutableArray" &&
               immutableArrayType.ContainingNamespace.ToDisplayString() == "System.Collections.Immutable" &&
               immutableArrayType.TypeArguments.Length == 1 &&
               IsType(immutableArrayType.TypeArguments[0], "Hakaze.Build.Abstractions", "ITarget");
    }

    private static bool IsTargetSourceResolverSignatureValid(IMethodSymbol methodSymbol)
    {
        return IsType(methodSymbol.ReturnType, "System", "String") &&
               methodSymbol.Parameters.Length == 2 &&
               IsType(methodSymbol.Parameters[0].Type, "Hakaze.Build.Abstractions", "IBuilding") &&
               IsType(methodSymbol.Parameters[1].Type, "System.Threading", "CancellationToken");
    }

    private static bool IsType(ITypeSymbol typeSymbol, string @namespace, string name)
    {
        return typeSymbol.Name == name && typeSymbol.ContainingNamespace.ToDisplayString() == @namespace;
    }

    private static bool IsCollectedExecutionResultsType(ITypeSymbol typeSymbol)
    {
        return typeSymbol is INamedTypeSymbol namedType &&
               namedType.Name == "ImmutableDictionary" &&
               namedType.ContainingNamespace.ToDisplayString() == "System.Collections.Immutable" &&
               namedType.TypeArguments.Length == 2 &&
               IsType(namedType.TypeArguments[0], "Hakaze.Build.Abstractions", "TargetId") &&
               namedType.TypeArguments[1].SpecialType == SpecialType.System_Object;
    }

    private static string GetTargetSourceExpression(TargetMethodModel method)
    {
        return method.TargetSourceResolverName is null
            ? "null"
            : $"new global::Hakaze.Build.Abstractions.TargetSource({method.TargetSourceResolverName}(building, cancellationToken))";
    }

    private static string GetIdVariableName(string methodName)
    {
        return char.ToLowerInvariant(methodName[0]) + methodName.Substring(1) + "TargetId";
    }

    private static string GetCreateTargetIdMethodName(string methodName)
    {
        return $"Create{methodName}TargetId";
    }

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
            ImmutableArray<TargetFactoryMethodModel> targetFactoryMethods,
            ImmutableArray<Diagnostic> diagnostics,
            bool shouldImplementExportTargetsInterface,
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
            TargetFactoryMethods = targetFactoryMethods;
            Diagnostics = diagnostics;
            ShouldImplementExportTargetsInterface = shouldImplementExportTargetsInterface;
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

        public ImmutableArray<TargetFactoryMethodModel> TargetFactoryMethods { get; }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public bool ShouldImplementExportTargetsInterface { get; }

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

        public string? TargetSourceResolverName { get; set; }

        public Location? TargetSourceResolverLocation { get; set; }

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
                TargetSourceResolverName,
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
            string? targetSourceResolverName,
            ImmutableArray<TargetParameterModel> parameters,
            ImmutableArray<string> explicitDependencies,
            ImmutableArray<string> allDependencyNames)
        {
            Name = name;
            MethodName = methodName;
            WrapperName = wrapperName;
            IdVariableName = idVariableName;
            TargetSourceResolverName = targetSourceResolverName;
            Parameters = parameters;
            ExplicitDependencies = explicitDependencies;
            AllDependencyNames = allDependencyNames;
        }

        public string Name { get; }

        public string MethodName { get; }

        public string WrapperName { get; }

        public string IdVariableName { get; }

        public string? TargetSourceResolverName { get; }

        public string InvokerDelegateName => $"{MethodName}InvokerDelegate";

        public string InvokeMethodName => $"Invoke{MethodName}";

        public string InvokeCoreMethodName => $"Invoke{MethodName}Core";

        public string CreateTargetIdMethodName => GetCreateTargetIdMethodName(MethodName);

        public string ResolveTargetIdMethodName => $"Resolve{MethodName}TargetId";

        public string ValidateTargetIdMethodName => $"Validate{MethodName}TargetId";

        public ImmutableArray<TargetParameterModel> Parameters { get; }

        public ImmutableArray<string> ExplicitDependencies { get; }

        public ImmutableArray<string> AllDependencyNames { get; }
    }

    private sealed class TargetFactoryMethodBuilder(string methodName, Location location)
    {
        public string MethodName { get; } = methodName;

        public Location Location { get; } = location;

        public string? HelperTargetName { get; set; }

        public List<TargetFactoryParameterModel> Parameters { get; } = [];

        public bool CanGenerate { get; private set; } = true;

        public void MarkInvalid()
        {
            CanGenerate = false;
        }

        public TargetFactoryMethodModel Build()
        {
            return new TargetFactoryMethodModel(
                MethodName,
                HelperTargetName,
                Parameters.ToImmutableArray());
        }
    }

    private sealed class TargetFactoryMethodModel
    {
        public TargetFactoryMethodModel(
            string methodName,
            string? helperTargetName,
            ImmutableArray<TargetFactoryParameterModel> parameters)
        {
            MethodName = methodName;
            HelperTargetName = string.IsNullOrWhiteSpace(helperTargetName) ? null : helperTargetName;
            Parameters = parameters;
        }

        public string MethodName { get; }

        public string? HelperTargetName { get; }

        public bool HasNamedHelper => HelperTargetName is not null;

        public ImmutableArray<TargetFactoryParameterModel> Parameters { get; }
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
    }

    private sealed class TargetFactoryParameterModel
    {
        public TargetFactoryParameterModel(
            string name,
            string typeName,
            TargetFactoryParameterKind kind)
        {
            Name = name;
            TypeName = typeName;
            Kind = kind;
        }

        public string Name { get; }

        public string TypeName { get; }

        public TargetFactoryParameterKind Kind { get; }
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
        EvaluatedBuilding,
        CollectedExecutionResults,
        Retrieval,
        CancellationToken
    }

    private enum TargetFactoryParameterKind
    {
        Building,
        CancellationToken
    }
}
