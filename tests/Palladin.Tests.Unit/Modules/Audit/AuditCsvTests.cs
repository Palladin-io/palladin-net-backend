using Palladin.Module.Audit.Features.Export;

namespace Palladin.Tests.Unit.Modules.Audit;

public sealed class AuditCsvTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"https://evil.test\")", "\"'=HYPERLINK(\"\"https://evil.test\"\")\"")]
    [InlineData("+SUM(1)", "'+SUM(1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    public async Task When_CellStartsWithFormulaTrigger_Then_IsPrefixedAndQuoted(string value, string expected)
    {
        var csv = await WriteAsync(value);

        csv.ShouldBe($"{expected}\n");
    }

    [Theory]
    [InlineData("Claude", "Claude")]
    [InlineData("2026-10-27T10:00:00Z", "2026-10-27T10:00:00Z")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("a=b", "a=b")]
    public async Task When_CellHasNoFormulaTrigger_Then_IsWrittenUnprefixed(string value, string expected)
    {
        var csv = await WriteAsync(value);

        csv.ShouldBe($"{expected}\n");
    }

    [Fact]
    public async Task When_RowHasEmptyCells_Then_WritesEmptyFields()
    {
        var writer = new StringWriter();

        await AuditCsv.WriteRowAsync(writer, [null, "", "=x"]);

        writer.ToString().ShouldBe(",,'=x\n");
    }

    private static async Task<string> WriteAsync(string value)
    {
        var writer = new StringWriter();
        await AuditCsv.WriteRowAsync(writer, [value]);
        return writer.ToString();
    }
}
