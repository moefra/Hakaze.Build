using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Hakaze.Build.Generator;

internal static class GeneratorRoslynUtilities
{
    internal static readonly SymbolDisplayFormat FullyQualifiedTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static readonly SymbolDisplayFormat ReadableTypeFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static bool HasAttribute(ImmutableArray<AttributeData> attributes, string attributeMetadataName)
    {
        return attributes.Any(attribute => IsAttribute(attribute, attributeMetadataName));
    }

    internal static bool IsAttribute(AttributeData attribute, string attributeMetadataName)
    {
        return attribute.AttributeClass?.ToDisplayString() == attributeMetadataName;
    }

    internal static string GetAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "internal"
        };
    }

    internal static string GetTypeDisplayName(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    internal static string GetFullyQualifiedTypeName(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? typeSymbol.Name
            : $"{typeSymbol.ContainingNamespace.ToDisplayString()}.{typeSymbol.Name}";
    }

    internal static bool ImplementsInterface(INamedTypeSymbol typeSymbol, INamedTypeSymbol interfaceSymbol)
    {
        return typeSymbol.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, interfaceSymbol));
    }
}
