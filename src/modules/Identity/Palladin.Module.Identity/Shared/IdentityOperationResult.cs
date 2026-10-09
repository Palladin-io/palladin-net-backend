namespace Palladin.Module.Identity.Shared;

internal sealed record IdentityOperationResult<T>(int StatusCode, T? Body = default, string? Error = null)
{
    public static IdentityOperationResult<T> Ok(T body) => new(200, body);
    public static IdentityOperationResult<T> Status(int code) => new(code);
    public static IdentityOperationResult<T> Failure(int code, string error) => new(code, default, error);
}
