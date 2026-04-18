using System.Collections.Immutable;
using System.Text;
using Hakaze.Build.Utility.Naming;
using Hakaze.Build.Utility.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hakaze.Build.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class OptionExportGenerator : IIncrementalGenerator
{
    private const string ExportOptionsAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.ExportOptionsAttribute";
    private const string OptionAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.OptionAttribute";
    private const string OptionValidatorAttributeMetadataName = "Hakaze.Build.Abstractions.Generator.OptionValidatorAttribute";
    private const string PropertyMetadataName = "Hakaze.Build.Abstractions.Property";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var exportedTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
                ExportOptionsAttributeMetadataName,
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

    private static ExportedOptionsModel? BuildExportedTypeModel(GeneratorAttributeSyntaxContext context)
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
                OptionExportDiagnostics.ExportOptionsTypeMustBeTopLevelNonGeneric,
                classDeclaration.Identifier.GetLocation(),
                typeDisplayName));
            return CreateInvalidModel(typeSymbol, diagnostics.ToImmutable());
        }

        if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(Diagnostic.Create(
                OptionExportDiagnostics.ExportOptionsTypeMustBePartial,
                classDeclaration.Identifier.GetLocation(),
                typeDisplayName));
            return CreateInvalidModel(typeSymbol, diagnostics.ToImmutable());
        }

        var optionBuilders = new List<OptionPropertyBuilder>();
        foreach (var property in typeSymbol.GetMembers()
                     .OfType<IPropertySymbol>()
                     .Where(static property => GeneratorRoslynUtilities.HasAttribute(property.GetAttributes(), OptionAttributeMetadataName))
                     .OrderBy(static property => property.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue))
        {
            optionBuilders.Add(BuildOptionProperty(property, diagnostics));
        }

        var duplicateKeys = optionBuilders
            .Where(static property => property.CanGenerate)
            .GroupBy(static property => property.OptionKey, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .ToImmutableArray();

        foreach (var duplicateGroup in duplicateKeys)
        {
            foreach (var property in duplicateGroup)
            {
                diagnostics.Add(Diagnostic.Create(
                    OptionExportDiagnostics.DuplicateOptionKey,
                    property.Location,
                    property.OptionKey,
                    GeneratorRoslynUtilities.GetTypeDisplayName(typeSymbol)));
                property.MarkInvalid();
            }
        }

        var validatorBuilders = new List<OptionValidatorBuilder>();
        foreach (var method in typeSymbol.GetMembers()
                     .OfType<IMethodSymbol>()
                     .Where(static method => method.MethodKind == MethodKind.Ordinary)
                     .Where(static method => GeneratorRoslynUtilities.HasAttribute(method.GetAttributes(), OptionValidatorAttributeMetadataName))
                     .OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue))
        {
            validatorBuilders.Add(BuildValidator(method, diagnostics));
        }

        return new ExportedOptionsModel(
            namespaceName: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeName: typeSymbol.Name,
            typeAccessibility: GeneratorRoslynUtilities.GetAccessibility(typeSymbol.DeclaredAccessibility),
            fullyQualifiedTypeName: GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol),
            hintName: $"Hakaze.Build.Generator.Options.{GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol).Replace('.', '_')}.g.cs",
            properties: optionBuilders.Where(static property => property.CanGenerate).Select(static property => property.Build()).ToImmutableArray(),
            validators: validatorBuilders.Where(static validator => validator.CanGenerate).Select(static validator => validator.Build()).ToImmutableArray(),
            diagnostics: diagnostics.ToImmutable(),
            shouldGenerate: true);
    }

    private static ExportedOptionsModel CreateInvalidModel(INamedTypeSymbol typeSymbol, ImmutableArray<Diagnostic> diagnostics)
    {
        return new ExportedOptionsModel(
            namespaceName: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            typeName: typeSymbol.Name,
            typeAccessibility: GeneratorRoslynUtilities.GetAccessibility(typeSymbol.DeclaredAccessibility),
            fullyQualifiedTypeName: GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol),
            hintName: $"Hakaze.Build.Generator.Options.{GeneratorRoslynUtilities.GetFullyQualifiedTypeName(typeSymbol).Replace('.', '_')}.g.cs",
            properties: [],
            validators: [],
            diagnostics: diagnostics,
            shouldGenerate: false);
    }

    private static OptionPropertyBuilder BuildOptionProperty(
        IPropertySymbol propertySymbol,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var optionAttribute = propertySymbol.GetAttributes()
            .First(static attribute => GeneratorRoslynUtilities.IsAttribute(attribute, OptionAttributeMetadataName));
        var location = propertySymbol.Locations.FirstOrDefault(static candidate => candidate.IsInSource) ?? Location.None;
        var optionKey = optionAttribute.ConstructorArguments.Length == 0 ||
                        optionAttribute.ConstructorArguments[0].Value is not string name ||
                        string.IsNullOrWhiteSpace(name)
            ? NamingConvention.ToKebabCase(propertySymbol.Name)
            : name;

        if (propertySymbol.IsStatic ||
            propertySymbol.IsIndexer ||
            propertySymbol.SetMethod is null)
        {
            diagnostics.Add(Diagnostic.Create(
                OptionExportDiagnostics.OptionPropertyMustBeWritableInstanceProperty,
                location,
                propertySymbol.Name));
            return OptionPropertyBuilder.Invalid(optionKey, location);
        }

        if (!TryCreateBindingKind(propertySymbol.Type, out var bindingKind, out var elementBindingKind))
        {
            diagnostics.Add(Diagnostic.Create(
                OptionExportDiagnostics.UnsupportedOptionPropertyType,
                location,
                propertySymbol.Name,
                propertySymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            return OptionPropertyBuilder.Invalid(optionKey, location);
        }

        return new OptionPropertyBuilder(
            propertySymbol.Name,
            optionKey,
            propertySymbol.Type.ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
            bindingKind,
            elementBindingKind,
            elementBindingKind is null
                ? null
                : ((INamedTypeSymbol)propertySymbol.Type).TypeArguments[0].ToDisplayString(GeneratorRoslynUtilities.FullyQualifiedTypeFormat),
            propertySymbol.IsRequired,
            location);
    }

    private static OptionValidatorBuilder BuildValidator(
        IMethodSymbol methodSymbol,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var location = methodSymbol.Locations.FirstOrDefault(static candidate => candidate.IsInSource) ?? Location.None;
        var builder = new OptionValidatorBuilder(methodSymbol.Name, location);

        if (methodSymbol.IsStatic ||
            methodSymbol.Parameters.Length != 0 ||
            methodSymbol.ReturnsVoid is false)
        {
            diagnostics.Add(Diagnostic.Create(
                OptionExportDiagnostics.OptionValidatorMustBeInstanceParameterlessVoidMethod,
                location,
                methodSymbol.Name));
            builder.MarkInvalid();
        }

        return builder;
    }

    private static string RenderSource(ExportedOptionsModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        if (!string.IsNullOrEmpty(model.NamespaceName))
        {
            builder.Append("namespace ").Append(model.NamespaceName).AppendLine(";");
            builder.AppendLine();
        }

        builder.Append(model.TypeAccessibility).Append(" partial class ").Append(model.TypeName).AppendLine();
        builder.AppendLine("{");
        builder.Append("    public static ").Append(model.FullyQualifiedTypeName).AppendLine(" BindAsync(");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.IConfig config)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(config);");
        builder.AppendLine();
        builder.Append("        var options = new ").Append(model.FullyQualifiedTypeName).AppendLine();
        builder.AppendLine("        {");

        foreach (var property in model.Properties)
        {
            if (!property.IsRequired)
            {
                continue;
            }

            builder.Append("            ").Append(property.PropertyName).Append(" = ")
                .Append("ReadRequiredOption(config, \"").Append(CSharpStringLiteral.Escape(property.OptionKey)).Append("\", ")
                .Append(property.ReadExpression)
                .AppendLine("),");
        }

        builder.AppendLine("        };");
        builder.AppendLine();

        foreach (var property in model.Properties)
        {
            if (property.IsRequired)
            {
                continue;
            }

            builder.Append("        if (TryReadOption(config, \"").Append(CSharpStringLiteral.Escape(property.OptionKey)).Append("\", ")
                .Append(property.ReadExpression)
                .Append(", out var ").Append(property.LocalName).AppendLine("))");
            builder.AppendLine("        {");
            builder.Append("            ").Append(property.SetExpression).AppendLine(";");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        foreach (var validator in model.Validators)
        {
            builder.Append("        options.").Append(validator.MethodName).AppendLine("();");
        }

        builder.AppendLine();
        builder.AppendLine("        return options;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static T ReadRequiredOption<T>(");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.IConfig config,");
        builder.AppendLine("        string key,");
        builder.AppendLine("        global::System.Func<global::Hakaze.Build.Abstractions.Property, string, T> binder)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (!config.Properties.TryGetValue(key, out var property))");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.Collections.Generic.KeyNotFoundException($\"Required option '{key}' was not found.\");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return binder(property, key);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static bool TryReadOption<T>(");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.IConfig config,");
        builder.AppendLine("        string key,");
        builder.AppendLine("        global::System.Func<global::Hakaze.Build.Abstractions.Property, string, T> binder,");
        builder.AppendLine("        out T value)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (!config.Properties.TryGetValue(key, out var property))");
        builder.AppendLine("        {");
        builder.AppendLine("            value = default!;");
        builder.AppendLine("            return false;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        value = binder(property, key);");
        builder.AppendLine("        return true;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static void SetOptionValue<T>(");
        builder.Append("        ").Append(model.FullyQualifiedTypeName).AppendLine(" options,");
        builder.AppendLine("        string propertyName,");
        builder.AppendLine("        T value)");
        builder.AppendLine("    {");
        builder.Append("        var property = typeof(").Append(model.FullyQualifiedTypeName)
            .AppendLine(").GetProperty(propertyName, global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic);");
        builder.AppendLine("        if (property is null)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.InvalidOperationException($\"Property '{propertyName}' was not found on generated options type.\");");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        property.SetValue(options, value);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static global::System.InvalidOperationException CreateInvalidOptionTypeException(");
        builder.AppendLine("        string key,");
        builder.AppendLine("        string expectedType,");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.Property actualValue)");
        builder.AppendLine("    {");
        builder.AppendLine("        var actualType = actualValue.GetType().FullName ?? actualValue.GetType().Name;");
        builder.AppendLine("        return new global::System.InvalidOperationException($\"Option '{key}' expected property type '{expectedType}', but got '{actualType}'.\");");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static string ReadStringValue(global::Hakaze.Build.Abstractions.Property property, string key)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (property is global::Hakaze.Build.Abstractions.StringProperty stringProperty)");
        builder.AppendLine("        {");
        builder.AppendLine("            return stringProperty.Value;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        throw CreateInvalidOptionTypeException(key, \"Hakaze.Build.Abstractions.StringProperty\", property);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static int ReadInt32Value(global::Hakaze.Build.Abstractions.Property property, string key)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (property is global::Hakaze.Build.Abstractions.IntegerProperty integerProperty)");
        builder.AppendLine("        {");
        builder.AppendLine("            return checked((int)integerProperty.Value);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        throw CreateInvalidOptionTypeException(key, \"Hakaze.Build.Abstractions.IntegerProperty\", property);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static long ReadInt64Value(global::Hakaze.Build.Abstractions.Property property, string key)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (property is global::Hakaze.Build.Abstractions.IntegerProperty integerProperty)");
        builder.AppendLine("        {");
        builder.AppendLine("            return integerProperty.Value;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        throw CreateInvalidOptionTypeException(key, \"Hakaze.Build.Abstractions.IntegerProperty\", property);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static bool ReadBooleanValue(global::Hakaze.Build.Abstractions.Property property, string key)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (property is global::Hakaze.Build.Abstractions.BooleanProperty booleanProperty)");
        builder.AppendLine("        {");
        builder.AppendLine("            return booleanProperty.Value;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        throw CreateInvalidOptionTypeException(key, \"Hakaze.Build.Abstractions.BooleanProperty\", property);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static global::Hakaze.Build.Abstractions.Property ReadPropertyValue(global::Hakaze.Build.Abstractions.Property property, string key)");
        builder.AppendLine("    {");
        builder.AppendLine("        return property;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static global::System.Collections.Immutable.ImmutableArray<T> ReadListValue<T>(");
        builder.AppendLine("        global::Hakaze.Build.Abstractions.Property property,");
        builder.AppendLine("        string key,");
        builder.AppendLine("        global::System.Func<global::Hakaze.Build.Abstractions.Property, string, T> elementBinder)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (property is not global::Hakaze.Build.Abstractions.ListProperty listProperty)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw CreateInvalidOptionTypeException(key, \"Hakaze.Build.Abstractions.ListProperty\", property);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var builder = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(listProperty.Value.Length);");
        builder.AppendLine("        foreach (var element in listProperty.Value)");
        builder.AppendLine("        {");
        builder.AppendLine("            builder.Add(elementBinder(element, key));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return builder.MoveToImmutable();");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static bool TryCreateBindingKind(ITypeSymbol typeSymbol, out OptionBindingKind bindingKind, out OptionBindingKind? elementBindingKind)
    {
        elementBindingKind = null;
        if (TryGetScalarBindingKind(typeSymbol, out bindingKind))
        {
            return true;
        }

        if (typeSymbol is INamedTypeSymbol namedType &&
            namedType.Name == "ImmutableArray" &&
            namedType.ContainingNamespace.ToDisplayString() == "System.Collections.Immutable" &&
            namedType.TypeArguments.Length == 1 &&
            TryGetScalarBindingKind(namedType.TypeArguments[0], out var itemBindingKind))
        {
            bindingKind = OptionBindingKind.ImmutableArray;
            elementBindingKind = itemBindingKind;
            return true;
        }

        return false;
    }

    private static bool TryGetScalarBindingKind(ITypeSymbol typeSymbol, out OptionBindingKind bindingKind)
    {
        if (typeSymbol.SpecialType == SpecialType.System_String)
        {
            bindingKind = OptionBindingKind.String;
            return true;
        }

        if (typeSymbol.SpecialType == SpecialType.System_Int32)
        {
            bindingKind = OptionBindingKind.Int32;
            return true;
        }

        if (typeSymbol.SpecialType == SpecialType.System_Int64)
        {
            bindingKind = OptionBindingKind.Int64;
            return true;
        }

        if (typeSymbol.SpecialType == SpecialType.System_Boolean)
        {
            bindingKind = OptionBindingKind.Boolean;
            return true;
        }

        if (typeSymbol.ToDisplayString() == PropertyMetadataName)
        {
            bindingKind = OptionBindingKind.Property;
            return true;
        }

        bindingKind = default;
        return false;
    }

    private sealed class ExportedOptionsModel
    {
        public ExportedOptionsModel(
            string namespaceName,
            string typeName,
            string typeAccessibility,
            string fullyQualifiedTypeName,
            string hintName,
            ImmutableArray<OptionPropertyModel> properties,
            ImmutableArray<OptionValidatorModel> validators,
            ImmutableArray<Diagnostic> diagnostics,
            bool shouldGenerate)
        {
            NamespaceName = namespaceName;
            TypeName = typeName;
            TypeAccessibility = typeAccessibility;
            FullyQualifiedTypeName = fullyQualifiedTypeName;
            HintName = hintName;
            Properties = properties;
            Validators = validators;
            Diagnostics = diagnostics;
            ShouldGenerate = shouldGenerate;
        }

        public string NamespaceName { get; }

        public string TypeName { get; }

        public string TypeAccessibility { get; }

        public string FullyQualifiedTypeName { get; }

        public string HintName { get; }

        public ImmutableArray<OptionPropertyModel> Properties { get; }

        public ImmutableArray<OptionValidatorModel> Validators { get; }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public bool ShouldGenerate { get; }
    }

    private sealed class OptionPropertyBuilder
    {
        public OptionPropertyBuilder(
            string propertyName,
            string optionKey,
            string typeName,
            OptionBindingKind bindingKind,
            OptionBindingKind? elementBindingKind,
            string? elementTypeName,
            bool isRequired,
            Location location)
        {
            PropertyName = propertyName;
            OptionKey = optionKey;
            TypeName = typeName;
            BindingKind = bindingKind;
            ElementBindingKind = elementBindingKind;
            ElementTypeName = elementTypeName;
            IsRequired = isRequired;
            Location = location;
        }

        public string PropertyName { get; }

        public string OptionKey { get; }

        public string TypeName { get; }

        public OptionBindingKind BindingKind { get; }

        public OptionBindingKind? ElementBindingKind { get; }

        public string? ElementTypeName { get; }

        public bool IsRequired { get; }

        public Location Location { get; }

        public bool CanGenerate { get; private set; } = true;

        public static OptionPropertyBuilder Invalid(string optionKey, Location location)
        {
            var builder = new OptionPropertyBuilder(
                string.Empty,
                optionKey,
                string.Empty,
                OptionBindingKind.Property,
                null,
                null,
                false,
                location);
            builder.MarkInvalid();
            return builder;
        }

        public void MarkInvalid()
        {
            CanGenerate = false;
        }

        public OptionPropertyModel Build()
        {
            return new OptionPropertyModel(
                PropertyName,
                OptionKey,
                TypeName,
                BindingKind,
                ElementBindingKind,
                ElementTypeName,
                IsRequired);
        }
    }

    private sealed class OptionPropertyModel
    {
        public OptionPropertyModel(
            string propertyName,
            string optionKey,
            string typeName,
            OptionBindingKind bindingKind,
            OptionBindingKind? elementBindingKind,
            string? elementTypeName,
            bool isRequired)
        {
            PropertyName = propertyName;
            OptionKey = optionKey;
            TypeName = typeName;
            BindingKind = bindingKind;
            ElementBindingKind = elementBindingKind;
            ElementTypeName = elementTypeName;
            IsRequired = isRequired;
        }

        public string PropertyName { get; }

        public string OptionKey { get; }

        public string TypeName { get; }

        public OptionBindingKind BindingKind { get; }

        public OptionBindingKind? ElementBindingKind { get; }

        public string? ElementTypeName { get; }

        public bool IsRequired { get; }

        public string LocalName => char.ToLowerInvariant(PropertyName[0]) + PropertyName.Substring(1);

        public string SetExpression => $"SetOptionValue(options, nameof({PropertyName}), {LocalName})";

        public string ReadExpression => BindingKind == OptionBindingKind.ImmutableArray
            ? $"static (property, key) => ReadListValue<{ElementTypeName}>(property, key, {GetReaderMethodName(ElementBindingKind!.Value)})"
            : GetReaderMethodName(BindingKind);
    }

    private sealed class OptionValidatorBuilder(string methodName, Location location)
    {
        public string MethodName { get; } = methodName;

        public Location Location { get; } = location;

        public bool CanGenerate { get; private set; } = true;

        public void MarkInvalid()
        {
            CanGenerate = false;
        }

        public OptionValidatorModel Build()
        {
            return new OptionValidatorModel(MethodName);
        }
    }

    private sealed class OptionValidatorModel(string methodName)
    {
        public string MethodName { get; } = methodName;
    }

    private static string GetReaderMethodName(OptionBindingKind kind)
    {
        return kind switch
        {
            OptionBindingKind.String => "ReadStringValue",
            OptionBindingKind.Int32 => "ReadInt32Value",
            OptionBindingKind.Int64 => "ReadInt64Value",
            OptionBindingKind.Boolean => "ReadBooleanValue",
            OptionBindingKind.Property => "ReadPropertyValue",
            _ => throw new InvalidOperationException("Unsupported reader kind.")
        };
    }

    private enum OptionBindingKind
    {
        String,
        Int32,
        Int64,
        Boolean,
        Property,
        ImmutableArray
    }
}
