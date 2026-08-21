namespace Emissary;

/// <summary>
/// Decides which policy-gated tools the current caller may use. Evaluated once per agent:
/// unauthorized tools are removed before prompt construction and cannot be executed.
/// Implementations typically capture the acting principal (user delegation or service identity).
/// </summary>
public interface IToolAuthorizer
{
    /// <summary>Whether the current caller may use the tool.</summary>
    /// <param name="tool">The tool, including its <see cref="ToolDefinition.RequiredPolicy"/>.</param>
    bool IsAuthorized(ToolDefinition tool);
}

/// <summary>An <see cref="IToolAuthorizer"/> backed by a fixed set of granted policy names.</summary>
public sealed class PolicyToolAuthorizer : IToolAuthorizer
{
    private readonly HashSet<string> _granted;

    /// <summary>Creates an authorizer granting exactly the given policies.</summary>
    /// <param name="grantedPolicies">The policy names the caller holds.</param>
    public PolicyToolAuthorizer(params string[] grantedPolicies)
    {
        ArgumentNullException.ThrowIfNull(grantedPolicies);
        _granted = new HashSet<string>(grantedPolicies, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsAuthorized(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.RequiredPolicy is null || _granted.Contains(tool.RequiredPolicy);
    }
}
