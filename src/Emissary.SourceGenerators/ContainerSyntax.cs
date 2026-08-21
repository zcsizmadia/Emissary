using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Emissary.SourceGenerators;

internal static class ContainerSyntax
{
    /// <summary>The declaration keyword(s) needed to re-open a type as partial ("class", "record struct", ...).</summary>
    public static string DeclarationKeyword(TypeDeclarationSyntax typeDecl) =>
        typeDecl is RecordDeclarationSyntax { ClassOrStructKeyword.ValueText.Length: > 0 } record
            ? "record " + record.ClassOrStructKeyword.ValueText
            : typeDecl.Keyword.ValueText;
}
