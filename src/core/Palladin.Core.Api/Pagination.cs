namespace Palladin.Core.Api;

public interface IPaginationOverTokenRequest<TToken>
{
    public int PageSize { get; }
    public TToken? PageToken { get; }
}

public record PaginatedOverTokenList<TTEntity, TToken>(
    TTEntity[] Items,
    TToken? NextToken,
    bool HasMore);
