namespace Emissary;

/// <summary>
/// Requires a policy for a <see cref="ClaudeToolAttribute"/> method. Tools whose policy the
/// configured <see cref="IToolAuthorizer"/> does not grant are filtered out <b>before prompt
/// construction</b> — the model never sees their schemas — and cannot be executed. With no
/// authorizer configured, policy-gated tools are denied by default.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AuthorizeToolAttribute : Attribute
{
    /// <summary>Requires the given policy.</summary>
    /// <param name="policy">The policy name the authorizer must grant.</param>
    public AuthorizeToolAttribute(string policy)
    {
        Policy = policy;
    }

    /// <summary>The policy name the authorizer must grant.</summary>
    public string Policy { get; }
}
