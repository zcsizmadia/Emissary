namespace Emissary.SourceGenerators;

internal static class GeneratorInfo
{
    /// <summary>The generator version stamped into [GeneratedCode] attributes; tracks the assembly version.</summary>
    public static readonly string Version =
        typeof(GeneratorInfo).Assembly.GetName().Version!.ToString(3);
}
