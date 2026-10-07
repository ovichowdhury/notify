using Notify.Web.Services;

namespace Notify.Test.Unit;

public class TemplateRendererTests
{
    private static IReadOnlyDictionary<string, string> Data(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Render_ReplacesPlaceholders()
    {
        var result = TemplateRenderer.Render("Hello {{Name}}, your plan is {{Plan}}.", Data(("Name", "Alice"), ("Plan", "Gold")), htmlEncode: false);
        Assert.Equal("Hello Alice, your plan is Gold.", result);
    }

    [Theory]
    [InlineData("{{name}}")]
    [InlineData("{{NAME}}")]
    [InlineData("{{ Name }}")]
    [InlineData("{{  name}}")]
    public void Render_IsCaseInsensitiveAndTrimsWhitespace(string template)
    {
        Assert.Equal("Alice", TemplateRenderer.Render(template, Data(("Name", "Alice")), htmlEncode: false));
    }

    [Fact]
    public void Render_UnknownPlaceholderBecomesEmpty()
    {
        Assert.Equal("Hi !", TemplateRenderer.Render("Hi {{Missing}}!", Data(("Name", "Alice")), htmlEncode: false));
    }

    [Fact]
    public void Render_HtmlEncodesValuesWhenRequested()
    {
        var data = Data(("Name", "<b>Bob</b> & co"));
        Assert.Equal("&lt;b&gt;Bob&lt;/b&gt; &amp; co", TemplateRenderer.Render("{{Name}}", data, htmlEncode: true));
        Assert.Equal("<b>Bob</b> & co", TemplateRenderer.Render("{{Name}}", data, htmlEncode: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_EmptyTemplateReturnsEmpty(string? template)
    {
        Assert.Equal(string.Empty, TemplateRenderer.Render(template, Data(("Name", "x")), htmlEncode: true));
    }

    [Fact]
    public void Render_LeavesTextWithoutPlaceholdersUntouched()
    {
        const string html = "<p>No {placeholders} here { { nope } }</p>";
        Assert.Equal(html, TemplateRenderer.Render(html, Data(), htmlEncode: true));
    }

    [Fact]
    public void Render_RepeatedPlaceholderIsReplacedEverywhere()
    {
        Assert.Equal("A A A", TemplateRenderer.Render("{{x}} {{X}} {{ x }}", Data(("x", "A")), htmlEncode: false));
    }

    [Fact]
    public void ExtractPlaceholders_ReturnsDistinctNamesInOrder()
    {
        var names = TemplateRenderer.ExtractPlaceholders("{{Name}} {{Email}} {{ name }} {{Plan}}");
        Assert.Equal(new[] { "Name", "Email", "Plan" }, names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain text")]
    public void ExtractPlaceholders_EmptyWhenNone(string? template)
    {
        Assert.Empty(TemplateRenderer.ExtractPlaceholders(template));
    }

    [Fact]
    public void ToPlainText_StripsTagsScriptsAndDecodesEntities()
    {
        const string html = "<html><style>p{color:red}</style><body><p>Hello <b>World</b></p><br/><script>alert(1)</script>Line2 &amp; more &lt;ok&gt;</body></html>";
        Assert.Equal("Hello World\nLine2 & more <ok>", TemplateRenderer.ToPlainText(html));
    }

    [Fact]
    public void ToPlainText_CollapsesBlankLines()
    {
        Assert.Equal("One\nTwo", TemplateRenderer.ToPlainText("<div>One</div><div></div><p>  </p><div>Two</div>"));
    }

    [Fact]
    public void ToPlainText_EmptyInput()
    {
        Assert.Equal(string.Empty, TemplateRenderer.ToPlainText(""));
    }
}
