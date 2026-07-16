using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace GodotResourceGenerator.Tests;

internal static class GeneratorTestDriver
{
    private const string GodotStubs = """
        namespace Godot
        {
            [global::System.AttributeUsage(global::System.AttributeTargets.Property | global::System.AttributeTargets.Field)]
            public sealed class ExportAttribute : global::System.Attribute
            {
            }

            [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
            public sealed class GlobalClassAttribute : global::System.Attribute
            {
            }

            public class GodotObject
            {
            }

            public class Resource : GodotObject
            {
            }

            public struct Vector2
            {
            }

            public struct Vector3
            {
            }

            public struct Color
            {
            }

            namespace Collections
            {
                public class Array
                {
                    public void Add(object value)
                    {
                    }
                }

                public class Array<T> : global::System.Collections.Generic.List<T>
                {
                }

                public class Dictionary
                {
                }

                public class Dictionary<TKey, TValue>
                {
                }
            }
        }
        """;

    public static GeneratorTestResult Run(string source, bool includeGodot = true)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTrees = includeGodot
            ? new[]
            {
                CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), parseOptions),
                CSharpSyntaxTree.ParseText(SourceText.From(GodotStubs, Encoding.UTF8), parseOptions),
            }
            : new[]
            {
                CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), parseOptions),
            };

        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            syntaxTrees,
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var generator = new global::GodotResourceGenerator.GodotResourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results.Single().GeneratedSources
            .Select(source => new GeneratedFile(source.HintName, source.SourceText.ToString()))
            .ToImmutableArray();

        var compilationErrors = outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new GeneratorTestResult(generatedSources, generatorDiagnostics, compilationErrors);
    }

    private static ImmutableArray<MetadataReference> GetReferences()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Select(assembly => assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }
}

internal sealed record GeneratorTestResult(
    ImmutableArray<GeneratedFile> GeneratedSources,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationErrors)
{
    public ImmutableArray<GeneratedFile> GeneratedResourceSources => GeneratedSources
        .Where(source => !source.HintName.EndsWith("GodotResourceAttribute.g.cs", StringComparison.Ordinal))
        .ToImmutableArray();

    public void AssertNoErrors()
    {
        Assert.Empty(GeneratorDiagnostics);
        Assert.Empty(CompilationErrors);
    }

    public Diagnostic SingleGeneratorDiagnostic(string id)
    {
        var diagnostic = Assert.Single(GeneratorDiagnostics);
        Assert.Equal(id, diagnostic.Id);
        return diagnostic;
    }
}

internal sealed record GeneratedFile(string HintName, string Source);
