using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notify.Test.Integration;

/// <summary>Drives the MVC app the way a browser would: fetch the form, take the anti-forgery token, post it back.</summary>
public static partial class ClientExtensions
{
    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    public static string ExtractToken(string html)
    {
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, "No anti-forgery token found in page.");
        return match.Groups[1].Value;
    }

    public static async Task<string> GetTokenAsync(this HttpClient client, string formUrl)
    {
        var response = await client.GetAsync(formUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractToken(await response.Content.ReadAsStringAsync());
    }

    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, string tokenFormUrl,
        params (string Name, string Value)[] fields)
    {
        var token = await client.GetTokenAsync(tokenFormUrl);
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    /// <summary>POST that re-uses the token embedded in an already fetched page (e.g. the campaign details page).</summary>
    public static Task<HttpResponseMessage> PostWithTokenAsync(this HttpClient client, string url, string token,
        params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        return client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    public static async Task<string> RegisterAsync(this HttpClient client, string? email = null, string company = "Test Co", string password = "secret123")
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        var response = await client.PostFormAsync("/Account/Register", "/Account/Register",
            ("CompanyName", company), ("Email", email), ("Password", password), ("ConfirmPassword", password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Settings/Smtp", response.Headers.Location?.ToString());
        return email;
    }

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email, string password, string? returnUrl = null)
    {
        var fields = new List<(string, string)> { ("Email", email), ("Password", password) };
        if (returnUrl is not null) fields.Add(("ReturnUrl", returnUrl));
        return client.PostFormAsync("/Account/Login", "/Account/Login", fields.ToArray());
    }

    public static async Task SaveSmtpAsync(this HttpClient client, int port, int concurrency = 2, string host = "127.0.0.1",
        string? username = null, string? password = null, int delayMs = 0)
    {
        var fields = new List<(string, string)>
        {
            ("Host", host), ("Port", port.ToString()), ("Security", "1"),
            ("FromEmail", "noreply@test.local"), ("FromName", "Test Sender"),
            ("ConcurrencyLevel", concurrency.ToString()), ("DelayBetweenEmailsMs", delayMs.ToString())
        };
        if (username is not null) fields.Add(("Username", username));
        if (password is not null) fields.Add(("Password", password));
        var response = await client.PostFormAsync("/Settings/Smtp", "/Settings/Smtp", fields.ToArray());
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public static async Task<int> CreateCampaignAsync(this HttpClient client, string? csv, string name = "Test campaign",
        string subject = "Hi {{Name}}", string body = "<p>Hello {{Name}}</p>", string fileName = "recipients.csv",
        byte[]? fileBytes = null, string contentType = "text/csv")
    {
        var token = await client.GetTokenAsync("/Campaigns/Create");
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(name), "Name" },
            { new StringContent(subject), "Subject" },
            { new StringContent(body), "BodyHtml" }
        };
        if (csv is not null || fileBytes is not null)
        {
            var file = new ByteArrayContent(fileBytes ?? System.Text.Encoding.UTF8.GetBytes(csv!));
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(file, "RecipientsFile", fileName);
        }

        var response = await client.PostAsync("/Campaigns/Create", content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        return int.Parse(location[(location.LastIndexOf('/') + 1)..]);
    }

    public static async Task<HttpResponseMessage> UploadAsync(this HttpClient client, int campaignId, string? csv, string fileName = "recipients.csv")
    {
        var token = await client.GetTokenAsync($"/Campaigns/Details/{campaignId}");
        using var content = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        if (csv is not null)
        {
            var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            content.Add(file, "file", fileName);
        }
        return await client.PostAsync($"/Campaigns/Upload/{campaignId}", content);
    }

    public static async Task<JsonElement> StatusAsync(this HttpClient client, int campaignId)
    {
        var response = await client.GetAsync($"/Campaigns/Status/{campaignId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    public static async Task<JsonElement> WaitForCompletionAsync(this HttpClient client, int campaignId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.StatusAsync(campaignId);
            if (!status.GetProperty("isActive").GetBoolean()) return status;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Campaign {campaignId} did not finish in time.");
    }

    /// <summary>Follows one redirect and returns the page body (where TempData flash messages render).</summary>
    public static async Task<string> FollowAsync(this HttpClient client, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        return await page.Content.ReadAsStringAsync();
    }

    public static async Task<string> GetOkAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
