using Microsoft.CodeAnalysis;

namespace Hakaze.Build.Generator;

internal static class OptionExportDiagnostics
{
    private const string Category = "Hakaze.Build.Generator";

    public static readonly DiagnosticDescriptor ExportOptionsTypeMustBePartial = new(
        id: "HBG0023",
        title: "Export option containers must be partial",
        messageFormat: "Type '{0}' must be declared as a partial class to use [ExportOptions].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ExportOptionsTypeMustBeTopLevelNonGeneric = new(
        id: "HBG0024",
        title: "Export option containers must be top-level non-generic classes",
        messageFormat: "Type '{0}' must be a top-level non-generic partial class to use [ExportOptions].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OptionPropertyMustBeWritableInstanceProperty = new(
        id: "HBG0025",
        title: "Option members must be writable instance properties",
        messageFormat: "Property '{0}' must be an instance property with an init or set accessor to use [Option].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedOptionPropertyType = new(
        id: "HBG0026",
        title: "Unsupported option property type",
        messageFormat: "Property '{0}' has unsupported option type '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OptionValidatorMustBeInstanceParameterlessVoidMethod = new(
        id: "HBG0027",
        title: "Option validators must be instance parameterless void methods",
        messageFormat: "Method '{0}' must be an instance method with no parameters and return void to use [OptionValidator].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateOptionKey = new(
        id: "HBG0028",
        title: "Duplicate option key",
        messageFormat: "Option key '{0}' is declared more than once in export type '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
