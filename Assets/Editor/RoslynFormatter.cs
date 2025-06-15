#if UNITY_EDITOR
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class RoslynFormatter
{
    public static string FormatCSharp(string code)
    {
        try
        {
            var root = CSharpSyntaxTree.ParseText(code).GetRoot();
            return root.NormalizeWhitespace().ToFullString();
        }
        catch { return code; }
    }
}
#endif