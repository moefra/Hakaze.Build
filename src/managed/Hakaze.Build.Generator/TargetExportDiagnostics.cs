using Microsoft.CodeAnalysis;

namespace Hakaze.Build.Generator;

internal static class TargetExportDiagnostics
{
    private const string Category = "Hakaze.Build.Generator";

    public static readonly DiagnosticDescriptor ExportTargetsTypeMustBePartial = new(
        id: "HBG0001",
        title: "Export target containers must be partial",
        messageFormat: "Type '{0}' must be declared as a partial class to use [ExportTargets].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetMethodMustBeStatic = new(
        id: "HBG0002",
        title: "Target methods must be static",
        messageFormat: "Method '{0}' must be static to use [Target].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetMethodMustReturnTaskOfExecutionResult = new(
        id: "HBG0003",
        title: "Target methods must return Task<ExecutionResult>",
        messageFormat: "Method '{0}' must return Task<ExecutionResult>.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BuildingParameterIsNotSupported = new(
        id: "HBG0004",
        title: "IBuilding parameter is not supported",
        messageFormat: "Method '{0}' must use IEvaluatedBuilding instead of IBuilding for execution context.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateCancellationTokenParameter = new(
        id: "HBG0005",
        title: "CancellationToken parameter must be unique",
        messageFormat: "Method '{0}' declares CancellationToken more than once.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RetrievalParameterRequired = new(
        id: "HBG0006",
        title: "Business parameters must declare retrieval",
        messageFormat: "Parameter '{0}' must declare exactly one [Retrieval] attribute.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnknownTargetReference = new(
        id: "HBG0007",
        title: "Unknown target reference",
        messageFormat: "Referenced target '{0}' was not found in export type '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateTargetName = new(
        id: "HBG0008",
        title: "Duplicate target name",
        messageFormat: "Target name '{0}' is declared more than once in export type '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateRetrievalAttribute = new(
        id: "HBG0009",
        title: "Duplicate retrieval attribute",
        messageFormat: "Parameter '{0}' declares [Retrieval] more than once.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ExportTargetsTypeMustBeTopLevelNonGeneric = new(
        id: "HBG0010",
        title: "Export target containers must be top-level non-generic classes",
        messageFormat: "Type '{0}' must be a top-level non-generic partial class to use [ExportTargets].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateEvaluatedBuildingParameter = new(
        id: "HBG0011",
        title: "IEvaluatedBuilding parameter must be unique",
        messageFormat: "Method '{0}' declares IEvaluatedBuilding more than once.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateCollectedExecutionResultsParameter = new(
        id: "HBG0012",
        title: "collectedExecutionResults parameter must be unique",
        messageFormat: "Method '{0}' declares ImmutableDictionary<TargetId, object?> more than once.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
