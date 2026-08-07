namespace Palladin.Module.Search.Infrastructure.MassTransit;

// Search consumes only its own OpenHost commands — one receive endpoint per command.
internal static class SearchEndpoints
{
    internal const string Index = "search.commands.index";
    internal const string Remove = "search.commands.remove";
    internal const string Scope = "search.commands.scope";
}
