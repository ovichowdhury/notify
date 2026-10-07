using System.Text;
using Notify.Test.TestSupport;
using Notify.Web.Services;

namespace Notify.Test.Unit;

public class RecipientFileParserTests
{
    private readonly RecipientFileParser _parser = new();

    private ParsedRecipientFile ParseCsv(string content, string fileName = "list.csv")
        => _parser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(content)), fileName);

    [Fact]
    public void Csv_ParsesHeaderAndRows()
    {
        var file = ParseCsv("Email,Name,Plan\nalice@example.com,Alice,Gold\nbob@example.com,Bob,Silver\n");

        Assert.Equal(new[] { "Email", "Name", "Plan" }, file.Columns);
        Assert.Equal(2, file.Rows.Count);
        Assert.Equal("Email", file.EmailColumn);
        Assert.Equal("Name", file.NameColumn);
        Assert.Equal("Gold", file.Rows[0]["Plan"]);
        Assert.Equal("Bob", file.Rows[1]["name"]); // row keys are case-insensitive
    }

    [Fact]
    public void Csv_DetectsSemicolonDelimiter()
    {
        var file = ParseCsv("Email;Name\nalice@example.com;Alice\n");
        Assert.Equal(new[] { "Email", "Name" }, file.Columns);
        Assert.Equal("alice@example.com", file.Rows[0]["Email"]);
    }

    [Fact]
    public void Csv_TrimsValuesAndSkipsEmptyRows()
    {
        var file = ParseCsv("Email,Name\n  alice@example.com , Alice \n,\n   \nbob@example.com,Bob\n");
        Assert.Equal(2, file.Rows.Count);
        Assert.Equal("alice@example.com", file.Rows[0]["Email"]);
        Assert.Equal("Alice", file.Rows[0]["Name"]);
    }

    [Fact]
    public void Csv_BlankHeaderGetsGeneratedName()
    {
        var file = ParseCsv("Email,,Plan\na@example.com,x,Gold\n");
        Assert.Equal(new[] { "Email", "Column2", "Plan" }, file.Columns);
        Assert.Equal("x", file.Rows[0]["Column2"]);
    }

    [Fact]
    public void Csv_MissingTrailingFieldsBecomeEmpty()
    {
        var file = ParseCsv("Email,Name,Plan\na@example.com,Alice\n");
        Assert.Equal(string.Empty, file.Rows[0]["Plan"]);
    }

    [Fact]
    public void Csv_UtfBomIsHandled()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Email,Name\na@example.com,Ä\n")).ToArray();
        var file = _parser.Parse(new MemoryStream(bytes), "bom.csv");
        Assert.Equal("Email", file.Columns[0]);
        Assert.Equal("Ä", file.Rows[0]["Name"]);
    }

    [Theory]
    [InlineData("E-mail Address")]
    [InlineData("email_address")]
    [InlineData("EMAIL")]
    [InlineData("Mail")]
    [InlineData("EmailId")]
    public void EmailColumn_DetectedByCommonHeaderNames(string header)
    {
        var file = ParseCsv($"Name,{header}\nAlice,alice@example.com\n");
        Assert.Equal(header, file.EmailColumn);
    }

    [Fact]
    public void EmailColumn_DetectedByHeaderContainingMail()
    {
        var file = ParseCsv("Name,Primary Mailbox\nAlice,alice@example.com\n");
        Assert.Equal("Primary Mailbox", file.EmailColumn);
    }

    [Fact]
    public void EmailColumn_FallsBackToColumnWithMostAddresses()
    {
        var file = ParseCsv("Person,Contact\nAlice,alice@example.com\nBob,bob@example.com\nCarol,not-an-address\n");
        Assert.Equal("Contact", file.EmailColumn);
    }

    [Fact]
    public void EmailColumn_NotFoundThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ParseCsv("Person,City\nAlice,Dhaka\n"));
        Assert.Contains("Could not find an email column", ex.Message);
    }

    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("Full Name", "Full Name")]
    [InlineData("first_name", "first_name")]
    [InlineData("Customer Name", "Customer Name")]
    public void NameColumn_Detected(string header, string expected)
    {
        var file = ParseCsv($"Email,{header}\na@example.com,Alice\n");
        Assert.Equal(expected, file.NameColumn);
    }

    [Fact]
    public void NameColumn_NullWhenAbsent()
    {
        var file = ParseCsv("Email,Plan\na@example.com,Gold\n");
        Assert.Null(file.NameColumn);
    }

    [Fact]
    public void Csv_EmptyFileThrowsNoHeader()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ParseCsv(""));
        Assert.Contains("no header row", ex.Message);
    }

    [Theory]
    [InlineData("list.pdf")]
    [InlineData("list.docx")]
    [InlineData("list")]
    public void UnsupportedExtensionThrows(string fileName)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ParseCsv("Email\na@example.com\n", fileName));
        Assert.Contains("Unsupported file type", ex.Message);
    }

    [Fact]
    public void Txt_IsTreatedAsCsv()
    {
        var file = ParseCsv("Email\na@example.com\n", "list.txt");
        Assert.Single(file.Rows);
    }

    [Fact]
    public void Xlsx_ParsesFirstSheetWithFormattedValues()
    {
        var bytes = TestData.Xlsx(
            new object?[] { "Full Name", "E-mail Address", "Discount", "Score" },
            new object?[] { "Alice", "alice@example.com", 10, 1.5 },
            new object?[] { "Bob", "bob@example.com", 15, null });

        var file = _parser.Parse(new MemoryStream(bytes), "list.xlsx");

        Assert.Equal(new[] { "Full Name", "E-mail Address", "Discount", "Score" }, file.Columns);
        Assert.Equal("E-mail Address", file.EmailColumn);
        Assert.Equal("Full Name", file.NameColumn);
        Assert.Equal(2, file.Rows.Count);
        Assert.Equal("10", file.Rows[0]["Discount"]);
        Assert.Equal("1.5", file.Rows[0]["Score"]);
        Assert.Equal(string.Empty, file.Rows[1]["Score"]);
    }

    [Fact]
    public void Xlsx_SkipsEmptyRowsAndNamesBlankHeaders()
    {
        var bytes = TestData.Xlsx(
            new object?[] { "Email", null, "Plan" },
            new object?[] { "a@example.com", "x", "Gold" },
            new object?[] { null, null, null },
            new object?[] { "b@example.com", "y", "Silver" });

        var file = _parser.Parse(new MemoryStream(bytes), "list.xlsx");
        Assert.Equal(new[] { "Email", "Column2", "Plan" }, file.Columns);
        Assert.Equal(2, file.Rows.Count);
    }

    [Fact]
    public void Xlsx_EmptyWorkbookThrowsNoHeader()
    {
        var bytes = TestData.Xlsx(Array.Empty<object?>());
        var ex = Assert.Throws<InvalidOperationException>(() => _parser.Parse(new MemoryStream(bytes), "empty.xlsx"));
        Assert.Contains("no header row", ex.Message);
    }

    [Theory]
    [InlineData("alice@example.com", true)]
    [InlineData("  alice@example.com  ", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("no-at-sign", false)]
    [InlineData("@example.com", false)]
    [InlineData("alice@", false)]
    public void IsValidEmail(string? email, bool expected)
    {
        Assert.Equal(expected, RecipientFileParser.IsValidEmail(email));
    }
}
