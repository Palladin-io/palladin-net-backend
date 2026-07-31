namespace Palladin.Core.Types;

// How an agent's CLI may use a delivered credential. Bitwise flags (binary sum, like Permission):
// Get returns plaintext into the agent's context (full LLM exposure), Exec injects it into a
// subprocess environment, Inject fills a browser login form over CDP. Enforcement at delivery is a
// server-side policy gate; what an honest CLI does with the decrypted secret is client policy —
// this protects against accidental LLM-context leakage, not a malicious client.
[Flags]
public enum GrantMethods
{
    Get = 1,
    Exec = 2,
    Inject = 4,
}

public static class GrantMethodsExtensions
{
    public const GrantMethods All = GrantMethods.Get | GrantMethods.Exec | GrantMethods.Inject;

    // A valid grant value: at least one method, no undefined bits.
    public static bool IsValidSet(this GrantMethods methods) =>
        methods != 0 && (methods & ~All) == 0;

    // A valid delivery `method` parameter: exactly one defined flag.
    public static bool IsSingleMethod(this GrantMethods method) =>
        method is GrantMethods.Get or GrantMethods.Exec or GrantMethods.Inject;
}
