using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using GodotResourceGenerator.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GodotResourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class GodotResourceGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "GodotResourceGenerator.GodotResourceAttribute";
    private const string GeneratedCodeAttribute = "[global::System.CodeDom.Compiler.GeneratedCode(\"GodotResourceGenerator\", \"0.1.0.0\")]";

    private static readonly SymbolDisplayFormat TypeDisplayFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static context =>
        {
            context.AddSource("GodotResourceAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
        });

        var declarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (context, cancellationToken) => CreateResourceDeclaration(context, cancellationToken))
            .Where(static declaration => declaration is not null)
            .Select(static (declaration, _) => declaration!)
            .Collect()
            .WithTrackingName(TrackingNames.ResourceDeclarations);

        var output = declarations
            .Combine(context.CompilationProvider)
            .Select(static (input, cancellationToken) => CreateOutput(input.Left, input.Right, cancellationToken))
            .WithTrackingName(TrackingNames.GeneratorOutput);

        context.RegisterSourceOutput(output, static (context, output) => Emit(context, output));
    }

    private static ResourceDeclaration? CreateResourceDeclaration(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol resourceType)
        {
            return null;
        }

        var attribute = context.Attributes.FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == AttributeMetadataName);

        if (attribute is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var constructorArguments = attribute.ConstructorArguments;
        var modelType = constructorArguments.Length >= 1 && constructorArguments[0].Value is INamedTypeSymbol type
            ? type
            : null;
        var baseResourceType = constructorArguments.Length >= 2 && constructorArguments[1].Value is INamedTypeSymbol baseType
            ? baseType
            : null;
        var explicitBaseType = GetExplicitBaseType(resourceType, context.SemanticModel.Compilation, cancellationToken);

        return new ResourceDeclaration(
            resourceType,
            modelType,
            baseResourceType,
            explicitBaseType,
            GetPrimaryLocation(resourceType),
            GetAttributeLocation(attribute) ?? GetPrimaryLocation(resourceType));
    }

    private static GeneratorOutput CreateOutput(ImmutableArray<ResourceDeclaration> declarations, Compilation compilation, CancellationToken cancellationToken)
    {
        if (declarations.IsDefaultOrEmpty)
        {
            return GeneratorOutput.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var sources = ImmutableArray.CreateBuilder<GeneratedSource>();
        var invalidDeclarations = new HashSet<ResourceDeclaration>();
        var godotSymbols = GodotSymbols.Create(compilation);

        if (!godotSymbols.IsComplete)
        {
            foreach (var declaration in declarations)
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.MissingGodotReference, declaration.AttributeLocation));
            }

            return new GeneratorOutput(sources.ToImmutable(), diagnostics.ToImmutable());
        }

        foreach (var declaration in declarations)
        {
            if (!IsValidResourceTarget(declaration.ResourceType))
            {
                invalidDeclarations.Add(declaration);
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.InvalidResourceTarget,
                    declaration.AttributeLocation,
                    declaration.ResourceType.ToDisplayString(TypeDisplayFormat)));
            }

            if (declaration.ModelType is null || declaration.ModelType.TypeKind != TypeKind.Class)
            {
                invalidDeclarations.Add(declaration);
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.InvalidModelArgument,
                    declaration.AttributeLocation,
                    declaration.ResourceType.ToDisplayString(TypeDisplayFormat)));
            }

            if (!IsValidResourceBase(declaration, godotSymbols, diagnostics))
            {
                invalidDeclarations.Add(declaration);
            }
        }

        ReportDuplicateModels(declarations, invalidDeclarations, diagnostics);
        ReportDuplicateHintNames(declarations, invalidDeclarations, diagnostics);

        var mappings = new Dictionary<ITypeSymbol, ResourceDeclaration>(SymbolEqualityComparer.Default);
        foreach (var declaration in declarations)
        {
            if (!invalidDeclarations.Contains(declaration) && declaration.ModelType is not null)
            {
                mappings[declaration.ModelType] = declaration;
            }
        }

        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (invalidDeclarations.Contains(declaration) || declaration.ModelType is null)
            {
                continue;
            }

            var resource = CreateResourceInfo(declaration, mappings, godotSymbols, diagnostics);
            if (resource is null)
            {
                continue;
            }

            sources.Add(new GeneratedSource(resource.HintName, GenerateSource(resource)));
        }

        return new GeneratorOutput(sources.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ResourceInfo? CreateResourceInfo(
        ResourceDeclaration declaration,
        IReadOnlyDictionary<ITypeSymbol, ResourceDeclaration> mappings,
        GodotSymbols godotSymbols,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var resolver = new TypeResolver(mappings, godotSymbols);
        var properties = ImmutableArray.CreateBuilder<PropertyInfo>();
        var hasErrors = false;

        foreach (var property in GetModelProperties(declaration.ModelType!))
        {
            var mapping = resolver.Resolve(property.Type, property);
            if (mapping is null)
            {
                hasErrors = true;
                diagnostics.Add(CreateUnsupportedPropertyDiagnostic(property, resolver.LastFailureWasCollection));
                continue;
            }

            properties.Add(new PropertyInfo(property.Name, EscapeIdentifier(property.Name), mapping));
        }

        if (hasErrors)
        {
            return null;
        }

        var resourceType = declaration.ResourceType;
        var @namespace = resourceType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : resourceType.ContainingNamespace.ToDisplayString();
        var effectiveBaseType = GetEffectiveBaseResourceType(declaration, godotSymbols);

        return new ResourceInfo(
            GetHintName(resourceType),
            GetAccessibility(resourceType),
            EscapeIdentifier(resourceType.Name),
            FullyQualifiedTypeName(resourceType),
            @namespace,
            FullyQualifiedTypeName(declaration.ModelType!),
            FullyQualifiedTypeName(effectiveBaseType),
            declaration.ExplicitBaseType is null,
            properties.ToImmutable());
    }

    private static void Emit(SourceProductionContext context, GeneratorOutput output)
    {
        foreach (var diagnostic in output.Diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        foreach (var source in output.Sources)
        {
            context.AddSource(source.HintName, SourceText.From(source.SourceText, Encoding.UTF8));
        }
    }

    private static Diagnostic CreateUnsupportedPropertyDiagnostic(IPropertySymbol property, bool collection)
    {
        var descriptor = collection
            ? DiagnosticDescriptors.UnsupportedCollectionType
            : DiagnosticDescriptors.UnsupportedPropertyType;

        return Diagnostic.Create(
            descriptor,
            GetPrimaryLocation(property),
            property.Name,
            property.Type.ToDisplayString(TypeDisplayFormat));
    }

    private static bool IsValidResourceBase(
        ResourceDeclaration declaration,
        GodotSymbols godotSymbols,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var attributeBaseType = declaration.AttributeBaseResourceType;
        var explicitBaseType = declaration.ExplicitBaseType?.Type;

        if (attributeBaseType is not null
            && explicitBaseType is not null
            && !ContainsUnresolvedType(attributeBaseType)
            && !ContainsUnresolvedType(explicitBaseType)
            && !SymbolEqualityComparer.Default.Equals(attributeBaseType, explicitBaseType))
        {
            diagnostics.Add(CreateInvalidResourceBaseDiagnostic(
                declaration,
                declaration.AttributeLocation,
                $"attribute base '{attributeBaseType.ToDisplayString(TypeDisplayFormat)}' does not match explicit base '{explicitBaseType.ToDisplayString(TypeDisplayFormat)}'"));
            return false;
        }

        var effectiveBaseType = GetEffectiveBaseResourceType(declaration, godotSymbols);
        if (ContainsUnresolvedType(effectiveBaseType))
        {
            return true;
        }

        if (effectiveBaseType.IsUnboundGenericType
            || effectiveBaseType.TypeKind != TypeKind.Class
            || !InheritsFrom(effectiveBaseType, godotSymbols.Resource))
        {
            var location = declaration.AttributeBaseResourceType is not null
                ? declaration.AttributeLocation
                : declaration.ExplicitBaseType?.Location ?? declaration.AttributeLocation;

            diagnostics.Add(CreateInvalidResourceBaseDiagnostic(
                declaration,
                location,
                $"'{effectiveBaseType.ToDisplayString(TypeDisplayFormat)}' must be a closed type that derives from Godot.Resource"));
            return false;
        }

        return true;
    }

    private static Diagnostic CreateInvalidResourceBaseDiagnostic(
        ResourceDeclaration declaration,
        Location? location,
        string details)
    {
        return Diagnostic.Create(
            DiagnosticDescriptors.InvalidResourceBaseType,
            location,
            declaration.ResourceType.ToDisplayString(TypeDisplayFormat),
            details);
    }

    private static INamedTypeSymbol GetEffectiveBaseResourceType(ResourceDeclaration declaration, GodotSymbols godotSymbols)
    {
        return declaration.AttributeBaseResourceType
            ?? declaration.ExplicitBaseType?.Type
            ?? godotSymbols.Resource!;
    }

    private static bool IsValidResourceTarget(INamedTypeSymbol resourceType)
    {
        return resourceType.TypeKind == TypeKind.Class
            && resourceType.ContainingType is null
            && resourceType.TypeParameters.Length == 0
            && IsPartial(resourceType);
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var syntaxReference in type.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static ExplicitBaseTypeInfo? GetExplicitBaseType(
        INamedTypeSymbol resourceType,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        foreach (var syntaxReference in resourceType.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (syntaxReference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration
                || declaration.BaseList is null)
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var baseTypeSyntax in declaration.BaseList.Types)
            {
                var baseType = semanticModel.GetTypeInfo(baseTypeSyntax.Type, cancellationToken).Type;
                if (baseType is not INamedTypeSymbol namedBaseType || namedBaseType.TypeKind == TypeKind.Interface)
                {
                    continue;
                }

                return new ExplicitBaseTypeInfo(namedBaseType, baseTypeSyntax.GetLocation());
            }
        }

        return null;
    }

    private static void ReportDuplicateModels(
        ImmutableArray<ResourceDeclaration> declarations,
        ISet<ResourceDeclaration> invalidDeclarations,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new List<ResourceDeclaration>();

        foreach (var declaration in declarations)
        {
            if (declaration.ModelType is null || invalidDeclarations.Contains(declaration))
            {
                continue;
            }

            var duplicate = seen.FirstOrDefault(existing =>
                SymbolEqualityComparer.Default.Equals(existing.ModelType, declaration.ModelType));

            if (duplicate is null)
            {
                seen.Add(declaration);
                continue;
            }

            invalidDeclarations.Add(duplicate);
            invalidDeclarations.Add(declaration);
            diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.DuplicateModelMapping,
                declaration.AttributeLocation,
                declaration.ModelType.ToDisplayString(TypeDisplayFormat)));
        }
    }

    private static void ReportDuplicateHintNames(
        ImmutableArray<ResourceDeclaration> declarations,
        ISet<ResourceDeclaration> invalidDeclarations,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new Dictionary<string, ResourceDeclaration>(StringComparer.Ordinal);

        foreach (var declaration in declarations)
        {
            if (invalidDeclarations.Contains(declaration))
            {
                continue;
            }

            var hintName = GetHintName(declaration.ResourceType);
            if (!seen.TryGetValue(hintName, out var duplicate))
            {
                seen.Add(hintName, declaration);
                continue;
            }

            invalidDeclarations.Add(duplicate);
            invalidDeclarations.Add(declaration);
            diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.DuplicateHintName,
                declaration.AttributeLocation,
                hintName));
        }
    }

    private static IEnumerable<IPropertySymbol> GetModelProperties(INamedTypeSymbol modelType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var type = modelType; type is not null; type = type.BaseType)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (IsPublicReadableInstanceProperty(property) && names.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private static bool IsPublicReadableInstanceProperty(IPropertySymbol property)
    {
        return !property.IsStatic
            && !property.IsIndexer
            && property.DeclaredAccessibility == Accessibility.Public
            && property.GetMethod?.DeclaredAccessibility == Accessibility.Public;
    }

    private static string GenerateSource(ResourceInfo resource)
    {
        var builder = new IndentedStringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#pragma warning disable CS0612, CS0618, CS1591, CS8981");
        builder.AppendLine("#nullable disable");

        if (!string.IsNullOrWhiteSpace(resource.Namespace))
        {
            builder.AppendLine($"namespace {resource.Namespace}");
            builder.AppendLine("{");
            builder.IncreaseIndent();
        }

        builder.AppendLine(GeneratedCodeAttribute);
        builder.AppendLine("[global::Godot.GlobalClass]");
        var baseClause = resource.ShouldDeclareBase ? $" : {resource.BaseTypeName}" : string.Empty;
        builder.AppendLine($"{resource.Accessibility} partial class {resource.Name}{baseClause}");
        builder.AppendLine("{");
        builder.IncreaseIndent();

        foreach (var property in resource.Properties)
        {
            builder.AppendLine("[global::Godot.Export]");
            builder.AppendLine($"public {property.Mapping.OutputType} {property.EscapedName} {{ get; set; }}");
            builder.AppendLine();
        }

        builder.AppendLine($"public static {resource.FullTypeName} FromModel({resource.ModelTypeName} model)");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine("if (model is null) throw new global::System.ArgumentNullException(nameof(model));");
        builder.AppendLine();
        builder.AppendLine($"var resource = new {resource.FullTypeName}();");
        builder.AppendLine("resource.CopyFrom(model);");
        builder.AppendLine("return resource;");
        builder.DecreaseIndent();
        builder.AppendLine("}");
        builder.AppendLine();

        builder.AppendLine($"public void CopyFrom({resource.ModelTypeName} model)");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine("if (model is null) throw new global::System.ArgumentNullException(nameof(model));");

        if (!resource.Properties.IsDefaultOrEmpty)
        {
            builder.AppendLine();
        }

        foreach (var property in resource.Properties)
        {
            AppendCopyAssignment(builder, property, $"model.{property.EscapedName}", property.EscapedName);
        }

        builder.DecreaseIndent();
        builder.AppendLine("}");

        builder.DecreaseIndent();
        builder.AppendLine("}");

        if (!string.IsNullOrWhiteSpace(resource.Namespace))
        {
            builder.DecreaseIndent();
            builder.AppendLine("}");
        }

        builder.AppendLine("#nullable restore");
        builder.AppendLine("#pragma warning restore CS0612, CS0618, CS1591, CS8981");
        return builder.ToString();
    }

    private static void AppendCopyAssignment(IndentedStringBuilder builder, PropertyInfo property, string sourceExpression, string targetExpression)
    {
        switch (property.Mapping.Kind)
        {
            case TypeMappingKind.Direct:
                builder.AppendLine($"{targetExpression} = {sourceExpression};");
                break;

            case TypeMappingKind.MappedResource:
                builder.AppendLine($"{targetExpression} = {ToConvertedExpression(sourceExpression, property.Mapping)};");
                break;

            case TypeMappingKind.Array:
                AppendArrayAssignment(builder, property, sourceExpression, targetExpression);
                break;

            case TypeMappingKind.Enumerable:
                AppendEnumerableAssignment(builder, property, sourceExpression, targetExpression);
                break;
        }
    }

    private static void AppendArrayAssignment(IndentedStringBuilder builder, PropertyInfo property, string sourceExpression, string targetExpression)
    {
        var localName = $"__{property.Name}";
        var indexName = $"__{property.Name}Index";
        var element = property.Mapping.ElementMapping!;

        builder.AppendLine($"if ({sourceExpression} is null)");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine($"{targetExpression} = null;");
        builder.DecreaseIndent();
        builder.AppendLine("}");
        builder.AppendLine("else");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine($"var {localName} = new {element.OutputType}[{sourceExpression}.Length];");
        builder.AppendLine($"for (var {indexName} = 0; {indexName} < {sourceExpression}.Length; {indexName}++)");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine($"{localName}[{indexName}] = {ToConvertedExpression($"{sourceExpression}[{indexName}]", element)};");
        builder.DecreaseIndent();
        builder.AppendLine("}");
        builder.AppendLine($"{targetExpression} = {localName};");
        builder.DecreaseIndent();
        builder.AppendLine("}");
    }

    private static void AppendEnumerableAssignment(IndentedStringBuilder builder, PropertyInfo property, string sourceExpression, string targetExpression)
    {
        var localName = $"__{property.Name}";
        var itemName = $"__{property.Name}Item";
        var element = property.Mapping.ElementMapping!;

        builder.AppendLine($"if ({sourceExpression} is null)");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine($"{targetExpression} = null;");
        builder.DecreaseIndent();
        builder.AppendLine("}");
        builder.AppendLine("else");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine($"var {localName} = new {property.Mapping.OutputType}();");
        builder.AppendLine($"foreach (var {itemName} in {sourceExpression})");
        builder.AppendLine("{");
        builder.IncreaseIndent();
        builder.AppendLine($"{localName}.Add({ToConvertedExpression(itemName, element)});");
        builder.DecreaseIndent();
        builder.AppendLine("}");
        builder.AppendLine($"{targetExpression} = {localName};");
        builder.DecreaseIndent();
        builder.AppendLine("}");
    }

    private static string ToConvertedExpression(string sourceExpression, TypeMapping mapping)
    {
        return mapping.Kind == TypeMappingKind.MappedResource
            ? $"{sourceExpression} is null ? null : {mapping.OutputType}.FromModel({sourceExpression})"
            : sourceExpression;
    }

    private static Location? GetPrimaryLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(static location => location.IsInSource)
            ?? symbol.Locations.FirstOrDefault();
    }

    private static Location? GetAttributeLocation(AttributeData attribute)
    {
        return attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
    }

    private static string GetAccessibility(INamedTypeSymbol type)
    {
        return type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
    }

    private static string GetHintName(INamedTypeSymbol resourceType)
    {
        return $"{resourceType.ToDisplayString(TypeDisplayFormat).Replace("global::", string.Empty)}.GodotResource.g.cs";
    }

    private static string FullyQualifiedTypeName(ITypeSymbol type)
    {
        return type.ToDisplayString(TypeDisplayFormat);
    }

    private static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None
            && SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None
            ? identifier
            : $"@{identifier}";
    }

    private static bool ContainsUnresolvedType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error)
        {
            return true;
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return ContainsUnresolvedType(arrayType.ElementType);
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        return namedType.TypeArguments.Any(ContainsUnresolvedType)
            || (namedType.BaseType is not null && ContainsUnresolvedType(namedType.BaseType));
    }

    private static bool InheritsFrom(ITypeSymbol type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private const string AttributeSource = """
        // <auto-generated/>
        #nullable enable
        namespace GodotResourceGenerator
        {
            [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            internal sealed class GodotResourceAttribute : global::System.Attribute
            {
                public GodotResourceAttribute(global::System.Type modelType)
                {
                    ModelType = modelType;
                }

                public GodotResourceAttribute(global::System.Type modelType, global::System.Type baseResourceType)
                {
                    ModelType = modelType;
                    BaseResourceType = baseResourceType;
                }

                public global::System.Type ModelType { get; }

                public global::System.Type? BaseResourceType { get; }
            }
        }
        #nullable restore
        """;

    private sealed class TypeResolver
    {
        private readonly IReadOnlyDictionary<ITypeSymbol, ResourceDeclaration> _mappings;
        private readonly GodotSymbols _godotSymbols;

        public TypeResolver(IReadOnlyDictionary<ITypeSymbol, ResourceDeclaration> mappings, GodotSymbols godotSymbols)
        {
            _mappings = mappings;
            _godotSymbols = godotSymbols;
        }

        public bool LastFailureWasCollection { get; private set; }

        public TypeMapping? Resolve(ITypeSymbol type, IPropertySymbol property)
        {
            LastFailureWasCollection = false;
            return Resolve(type, property, allowCollection: true);
        }

        private TypeMapping? Resolve(ITypeSymbol type, IPropertySymbol property, bool allowCollection)
        {
            if (IsNullableValueType(type))
            {
                return null;
            }

            if (ContainsUnresolvedType(type))
            {
                return TypeMapping.Direct(FullyQualifiedTypeName(type));
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                var element = Resolve(arrayType.ElementType, property, allowCollection: false);
                if (element is null || !CanUseAsCollectionElement(element))
                {
                    LastFailureWasCollection = true;
                    return null;
                }

                return element.Kind == TypeMappingKind.Direct
                    ? TypeMapping.Direct(FullyQualifiedTypeName(type))
                    : TypeMapping.Array($"{element.OutputType}[]", element);
            }

            var mappedResource = FindMappedResource(type);
            if (mappedResource is not null)
            {
                return TypeMapping.MappedResource(FullyQualifiedTypeName(mappedResource.ResourceType));
            }

            if (IsDirectVariantType(type))
            {
                return TypeMapping.Direct(FullyQualifiedTypeName(type));
            }

            if (type is INamedTypeSymbol namedType)
            {
                var godotCollection = ResolveGodotCollection(namedType, property);
                if (godotCollection is not null)
                {
                    return godotCollection;
                }

                if (IsRegularDictionary(namedType))
                {
                    LastFailureWasCollection = true;
                    return null;
                }

                if (allowCollection)
                {
                    var enumerableElement = GetEnumerableElementType(namedType);
                    if (enumerableElement is not null)
                    {
                        var element = Resolve(enumerableElement, property, allowCollection: false);
                        if (element is null || !CanUseAsCollectionElement(element))
                        {
                            LastFailureWasCollection = true;
                            return null;
                        }

                        return TypeMapping.Enumerable(
                            $"global::Godot.Collections.Array<{element.OutputType}>",
                            element);
                    }
                }
            }

            return null;
        }

        private ResourceDeclaration? FindMappedResource(ITypeSymbol type)
        {
            if (_mappings.TryGetValue(type, out var mappedResource))
            {
                return mappedResource;
            }

            if (type is not INamedTypeSymbol namedType)
            {
                return null;
            }

            for (var baseType = namedType.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (_mappings.TryGetValue(baseType, out mappedResource))
                {
                    return mappedResource;
                }
            }

            return null;
        }

        private TypeMapping? ResolveGodotCollection(INamedTypeSymbol type, IPropertySymbol property)
        {
            if (!IsGodotCollectionsType(type))
            {
                return null;
            }

            if (type.TypeArguments.Length == 0)
            {
                return TypeMapping.Direct(FullyQualifiedTypeName(type));
            }

            if (type.Name == "Array" && type.TypeArguments.Length == 1)
            {
                var element = Resolve(type.TypeArguments[0], property, allowCollection: false);
                if (element is null || !CanUseAsCollectionElement(element))
                {
                    LastFailureWasCollection = true;
                    return null;
                }

                return element.Kind == TypeMappingKind.Direct
                    ? TypeMapping.Direct(FullyQualifiedTypeName(type))
                    : TypeMapping.Enumerable($"global::Godot.Collections.Array<{element.OutputType}>", element);
            }

            if (type.Name == "Dictionary" && type.TypeArguments.Length == 2)
            {
                var key = Resolve(type.TypeArguments[0], property, allowCollection: false);
                var value = Resolve(type.TypeArguments[1], property, allowCollection: false);
                if (key?.Kind == TypeMappingKind.Direct && value?.Kind == TypeMappingKind.Direct)
                {
                    return TypeMapping.Direct(FullyQualifiedTypeName(type));
                }

                LastFailureWasCollection = true;
                return null;
            }

            LastFailureWasCollection = true;
            return null;
        }

        private bool IsDirectVariantType(ITypeSymbol type)
        {
            return IsSupportedSpecialType(type)
                || type.TypeKind == TypeKind.Enum
                || IsGodotBuiltInType(type)
                || InheritsFrom(type, _godotSymbols.GodotObject);
        }

        private static bool IsSupportedSpecialType(ITypeSymbol type)
        {
            return type.SpecialType is SpecialType.System_Boolean
                or SpecialType.System_Char
                or SpecialType.System_SByte
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Single
                or SpecialType.System_Double
                or SpecialType.System_String;
        }

        private static bool IsNullableValueType(ITypeSymbol type)
        {
            return type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        }

        private static bool IsGodotBuiltInType(ITypeSymbol type)
        {
            return type.ContainingNamespace.ToDisplayString() == "Godot";
        }

        private static bool IsGodotCollectionsType(INamedTypeSymbol type)
        {
            return type.ContainingNamespace.ToDisplayString() == "Godot.Collections"
                && (type.Name == "Array" || type.Name == "Dictionary");
        }

        private static bool IsRegularDictionary(INamedTypeSymbol type)
        {
            if (type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
                && (type.Name == "Dictionary" || type.Name == "IDictionary" || type.Name == "IReadOnlyDictionary"))
            {
                return true;
            }

            return type.AllInterfaces.Any(static @interface =>
                @interface.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
                && (@interface.Name == "IDictionary" || @interface.Name == "IReadOnlyDictionary"));
        }

        private static ITypeSymbol? GetEnumerableElementType(INamedTypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_String)
            {
                return null;
            }

            if (type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
                && type.Name == "IEnumerable"
                && type.TypeArguments.Length == 1)
            {
                return type.TypeArguments[0];
            }

            foreach (var @interface in type.AllInterfaces)
            {
                if (@interface.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
                    && @interface.Name == "IEnumerable"
                    && @interface.TypeArguments.Length == 1)
                {
                    return @interface.TypeArguments[0];
                }
            }

            return null;
        }

        private static bool CanUseAsCollectionElement(TypeMapping mapping)
        {
            return mapping.Kind == TypeMappingKind.Direct || mapping.Kind == TypeMappingKind.MappedResource;
        }

    }

    private sealed class GodotSymbols
    {
        private GodotSymbols(INamedTypeSymbol? godotObject, INamedTypeSymbol? resource, INamedTypeSymbol? exportAttribute, INamedTypeSymbol? globalClassAttribute)
        {
            GodotObject = godotObject;
            Resource = resource;
            ExportAttribute = exportAttribute;
            GlobalClassAttribute = globalClassAttribute;
        }

        public INamedTypeSymbol? GodotObject { get; }
        public INamedTypeSymbol? Resource { get; }
        public INamedTypeSymbol? ExportAttribute { get; }
        public INamedTypeSymbol? GlobalClassAttribute { get; }

        public bool IsComplete => GodotObject is not null
            && Resource is not null
            && ExportAttribute is not null
            && GlobalClassAttribute is not null;

        public static GodotSymbols Create(Compilation compilation)
        {
            return new GodotSymbols(
                compilation.GetTypeByMetadataName("Godot.GodotObject"),
                compilation.GetTypeByMetadataName("Godot.Resource"),
                compilation.GetTypeByMetadataName("Godot.ExportAttribute"),
                compilation.GetTypeByMetadataName("Godot.GlobalClassAttribute"));
        }
    }

    private sealed class IndentedStringBuilder
    {
        private const string IndentText = "    ";
        private readonly StringBuilder _builder = new StringBuilder();
        private int _indent;

        public void IncreaseIndent()
        {
            _indent++;
        }

        public void DecreaseIndent()
        {
            _indent--;
        }

        public void AppendLine()
        {
            _builder.AppendLine();
        }

        public void AppendLine(string text)
        {
            if (text.Length > 0)
            {
                for (var i = 0; i < _indent; i++)
                {
                    _builder.Append(IndentText);
                }
            }

            _builder.AppendLine(text);
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }

    private sealed record ResourceDeclaration(
        INamedTypeSymbol ResourceType,
        INamedTypeSymbol? ModelType,
        INamedTypeSymbol? AttributeBaseResourceType,
        ExplicitBaseTypeInfo? ExplicitBaseType,
        Location? TargetLocation,
        Location? AttributeLocation);

    private sealed record ExplicitBaseTypeInfo(INamedTypeSymbol Type, Location? Location);

    private sealed record GeneratorOutput(ImmutableArray<GeneratedSource> Sources, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static readonly GeneratorOutput Empty = new GeneratorOutput(ImmutableArray<GeneratedSource>.Empty, ImmutableArray<Diagnostic>.Empty);
    }

    private sealed record GeneratedSource(string HintName, string SourceText);

    private sealed record ResourceInfo(
        string HintName,
        string Accessibility,
        string Name,
        string FullTypeName,
        string Namespace,
        string ModelTypeName,
        string BaseTypeName,
        bool ShouldDeclareBase,
        ImmutableArray<PropertyInfo> Properties);

    private sealed record PropertyInfo(string Name, string EscapedName, TypeMapping Mapping);

    private sealed record TypeMapping(string OutputType, TypeMappingKind Kind, TypeMapping? ElementMapping)
    {
        public static TypeMapping Direct(string outputType)
        {
            return new TypeMapping(outputType, TypeMappingKind.Direct, null);
        }

        public static TypeMapping MappedResource(string outputType)
        {
            return new TypeMapping(outputType, TypeMappingKind.MappedResource, null);
        }

        public static TypeMapping Array(string outputType, TypeMapping element)
        {
            return new TypeMapping(outputType, TypeMappingKind.Array, element);
        }

        public static TypeMapping Enumerable(string outputType, TypeMapping element)
        {
            return new TypeMapping(outputType, TypeMappingKind.Enumerable, element);
        }
    }

    private enum TypeMappingKind
    {
        Direct,
        MappedResource,
        Array,
        Enumerable,
    }
}
