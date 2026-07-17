using Microsoft.CodeAnalysis;

namespace GodotResourceGenerator.Diagnostics;

internal static class DiagnosticDescriptors
{
    private const string Usage = "Usage";
    private const string Compilation = "Compilation";

    public static readonly DiagnosticDescriptor InvalidResourceTarget = new DiagnosticDescriptor(
        "GRG0001",
        "Invalid Godot resource target",
        "The type '{0}' must be a non-generic top-level partial class to use [GodotResource]",
        Usage,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidModelArgument = new DiagnosticDescriptor(
        "GRG0002",
        "Invalid Godot resource model",
        "The [GodotResource] attribute on '{0}' must reference a source model class with typeof(...)",
        Usage,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateModelMapping = new DiagnosticDescriptor(
        "GRG0003",
        "Duplicate Godot resource model mapping",
        "The source model type '{0}' is mapped by more than one Godot resource",
        Usage,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateHintName = new DiagnosticDescriptor(
        "GRG0004",
        "Duplicate generated Godot resource name",
        "More than one Godot resource would generate the hint name '{0}'",
        Compilation,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingGodotReference = new DiagnosticDescriptor(
        "GRG0005",
        "Missing Godot reference",
        "Godot resource generation requires the consuming project to reference Godot C# types",
        Compilation,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedPropertyType = new DiagnosticDescriptor(
        "GRG0006",
        "Unsupported Godot export property type",
        "The property '{0}' has unsupported type '{1}' and cannot be exported by GodotResourceGenerator",
        Usage,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedCollectionType = new DiagnosticDescriptor(
        "GRG0007",
        "Unsupported Godot export collection type",
        "The property '{0}' has unsupported collection or dictionary type '{1}'",
        Usage,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidResourceBaseType = new DiagnosticDescriptor(
        "GRG0008",
        "Invalid Godot resource base type",
        "The Godot resource base for '{0}' is invalid: {1}",
        Usage,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
