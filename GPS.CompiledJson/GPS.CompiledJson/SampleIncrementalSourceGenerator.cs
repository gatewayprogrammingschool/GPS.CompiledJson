using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;


namespace GPS.CompiledJson;

/// <summary>
/// A sample source generator that creates a custom report based on class properties. The target class should be annotated with the 'Generators.ReportAttribute' attribute.
/// When using the source code as a baseline, an incremental source generator is preferable because it reduces the performance overhead.
/// </summary>
[Generator]
public class SampleIncrementalSourceGenerator : IIncrementalGenerator
{
    private const string Namespace = "Generators";
    private const string AttributeName = "CompiledJsonAttribute";

    private const string AttributeSourceCode = $$"""
                                                 // <auto-generated/>

                                                 namespace {{Namespace}}
                                                 {
                                                     [System.AttributeUsage(System.AttributeTargets.Record)]
                                                     public class {{AttributeName}} : System.Attribute
                                                     {
                                                     }
                                                 }
                                                 """;


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Add the marker attribute to the compilation.
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource(
            $"{AttributeName}.g.cs",
            SourceText.From(AttributeSourceCode, Encoding.UTF8)));

        // Filter classes annotated with the [Report] attribute. Only filtered Syntax Nodes can trigger code generation.
        var provider = context.SyntaxProvider
            .CreateSyntaxProvider(
                (s, _) => s is RecordDeclarationSyntax,
                (ctx, _) => GetRecordDeclarationForSourceGen(ctx))
            .Where(t => t.reportAttributeFound)
            .Select((t, _) => t.Item1);

        // Generate the source code.
        context.RegisterSourceOutput(context.CompilationProvider.Combine(provider.Collect()),
            ((ctx, t) => GenerateCode(ctx, t.Left, t.Right)));
    }

    /// <summary>
    /// Checks whether the Node is annotated with the [Report] attribute and maps syntax context to the specific node type (RecordDeclarationSyntax).
    /// </summary>
    /// <param name="context">Syntax context, based on CreateSyntaxProvider predicate</param>
    /// <returns>The specific cast and whether the attribute was found.</returns>
    private static (RecordDeclarationSyntax, bool reportAttributeFound) GetRecordDeclarationForSourceGen(
        GeneratorSyntaxContext context)
    {
        var recordDeclarationSyntax = (RecordDeclarationSyntax)context.Node;

        // Go through all attributes of the class.
        foreach (AttributeListSyntax attributeListSyntax in recordDeclarationSyntax.AttributeLists)
        foreach (AttributeSyntax attributeSyntax in attributeListSyntax.Attributes)
        {
            if (context.SemanticModel.GetSymbolInfo(attributeSyntax).Symbol is not IMethodSymbol attributeSymbol)
                continue; // if we can't get the symbol, ignore it

            string attributeName = attributeSymbol.ContainingType.ToDisplayString();

            // Check the full name of the [Report] attribute.
            if (attributeName == $"{Namespace}.{AttributeName}")
                return (recordDeclarationSyntax, true);
        }

        return (recordDeclarationSyntax, false);
    }

    /// <summary>
    /// Generate code action.
    /// It will be executed on specific nodes (RecordDeclarationSyntax annotated with the [Report] attribute) changed by the user.
    /// </summary>
    /// <param name="context">Source generation context used to add source files.</param>
    /// <param name="compilation">Compilation used to provide access to the Semantic Model.</param>
    /// <param name="recordDeclarations">Nodes annotated with the [Report] attribute that trigger the generate action.</param>
    private void GenerateCode(SourceProductionContext context, Compilation compilation,
        ImmutableArray<RecordDeclarationSyntax> recordDeclarations)
    {
        // Go through all filtered class declarations.
        foreach (var recordDeclarationSyntax in recordDeclarations)
        {
            // We need to get semantic model of the class to retrieve metadata.
            var semanticModel = compilation.GetSemanticModel(recordDeclarationSyntax.SyntaxTree);

            // Symbols allow us to get the compile-time information.
            if (semanticModel.GetDeclaredSymbol(recordDeclarationSyntax) is not INamedTypeSymbol recordSymbol)
                continue;

            var namespaceName = recordSymbol.ContainingNamespace.ToDisplayString();

            // 'Identifier' means the token of the node. Get class name from the syntax node.
            var recordName = recordDeclarationSyntax.Identifier.Text;

            // Go through all class members with a particular type (property) to generate method lines.
            var methodBody = recordSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Select(p =>
                    $$"""        yield return $"{{p.Name}}:{this.{{p.Name}}}";"""); // e.g. yield return $"Id:{this.Id}";

            // Build up the source code
            var code = $$"""
                         // <auto-generated/>

                         using System;
                         using System.Collections.Generic;

                         namespace {{namespaceName}};

                         partial record {{recordName}}
                         {
                             public IEnumerable<string> Report()
                             {
                         {{string.Join("\n", methodBody)}}
                             }
                         }

                         """;

            // Add the source code to the compilation.
            context.AddSource($"{recordName}.g.cs", SourceText.From(code, Encoding.UTF8));
        }
    }
}