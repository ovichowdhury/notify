using System.Net;
using System.Net.Http.Headers;

namespace Notify.Test.Integration;

public class AccountEndpointTests(NotifyWebFactory factory) : IClassFixture<NotifyWebFactory>
{
    [Fact]
    public async Task Home_AnonymousShowsLandingPage()
    {
        var client = factory.CreateBrowser();
        var html = await client.GetOkAsync("/");
        Assert.Contains("Send email campaigns", html);
        Assert.Contains("Create account", html);
    }

    [Fact]
    public async Task Home_AuthenticatedRedirectsToDashboard()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Dashboard", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("/Dashboard")]
    [InlineData("/Campaigns")]
    [InlineData("/Settings/Smtp")]
    [InlineData("/Account/Profile")]
    public async Task ProtectedPages_RedirectAnonymousToLogin(string path)
    {
        var response = await factory.CreateBrowser().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/Account/Login?ReturnUrl=", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Register_CreatesTenantSignsInAndRedirectsToSmtp()
    {
        var client = factory.CreateBrowser();
        var email = await client.RegisterAsync(company: "Fresh Co");

        var dashboard = await client.GetOkAsync("/Dashboard");
        Assert.Contains("Fresh Co", dashboard);
        Assert.Contains(email, dashboard);
        Assert.Contains("SMTP is not configured", dashboard);
    }

    [Fact]
    public async Task Register_PasswordMismatchShowsValidationError()
    {
        var client = factory.CreateBrowser();
        var response = await client.PostFormAsync("/Account/Register", "/Account/Register",
            ("CompanyName", "X"), ("Email", "mismatch@example.com"), ("Password", "secret123"), ("ConfirmPassword", "different"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The passwords do not match.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_DuplicateEmailIsRejected()
    {
        var email = await factory.CreateBrowser().RegisterAsync();
        var response = await factory.CreateBrowser().PostFormAsync("/Account/Register", "/Account/Register",
            ("CompanyName", "Dup"), ("Email", email), ("Password", "secret123"), ("ConfirmPassword", "secret123"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("is already taken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_ShortPasswordIsRejected()
    {
        var response = await factory.CreateBrowser().PostFormAsync("/Account/Register", "/Account/Register",
            ("CompanyName", "X"), ("Email", "short@example.com"), ("Password", "abc"), ("ConfirmPassword", "abc"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("at least 6 characters", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_WrongPasswordShowsError()
    {
        var email = await factory.CreateBrowser().RegisterAsync();
        var client = factory.CreateBrowser();
        var response = await client.LoginAsync(email, "nope");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email or password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_SucceedsAndHonoursLocalReturnUrl()
    {
        var email = await factory.CreateBrowser().RegisterAsync();
        var client = factory.CreateBrowser();

        var response = await client.LoginAsync(email, "secret123", "/Campaigns");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Campaigns", response.Headers.Location?.ToString());

        var external = await factory.CreateBrowser().LoginAsync(email, "secret123", "https://evil.example/");
        Assert.Equal("/Dashboard", external.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Login_SeededAdminWorks()
    {
        var client = factory.CreateBrowser();
        var response = await client.LoginAsync(NotifyWebFactory.AdminEmail, NotifyWebFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Test Admin Co", await client.GetOkAsync("/Dashboard"));
    }

    [Fact]
    public async Task LoginAndRegisterPages_RedirectWhenAlreadySignedIn()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Account/Register")).StatusCode);
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var token = ExtractTokenFromPage(await client.GetOkAsync("/Dashboard"));

        var response = await client.PostWithTokenAsync("/Account/Logout", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Dashboard")).StatusCode);
    }

    [Fact]
    public async Task Profile_UpdatesCompanyName()
    {
        var client = factory.CreateBrowser();
        var email = await client.RegisterAsync(company: "Before Co");

        var page = await client.GetOkAsync("/Account/Profile");
        Assert.Contains(email, page);
        Assert.Contains("Before Co", page);

        var response = await client.PostFormAsync("/Account/Profile", "/Account/Profile", ("CompanyName", "After Co"));
        var body = await client.FollowAsync(response);
        Assert.Contains("Profile updated.", body);
        Assert.Contains("After Co", body);
        Assert.DoesNotContain("Before Co", body);
    }

    [Fact]
    public async Task Profile_RejectsNonImageLogo()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var token = await client.GetTokenAsync("/Account/Profile");
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Co"), "CompanyName" }
        };
        var file = new ByteArrayContent("not an image"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "Logo", "notes.txt");

        var response = await client.PostAsync("/Account/Profile", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Logo must be a PNG, JPG, GIF or WEBP image.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Profile_UploadsThenRemovesLogo()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();

        var token = await client.GetTokenAsync("/Account/Profile");
        using (var content = new MultipartFormDataContent
               {
                   { new StringContent(token), "__RequestVerificationToken" },
                   { new StringContent("Logo Co"), "CompanyName" }
               })
        {
            // 1x1 transparent PNG
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(file, "Logo", "logo.png");

            var upload = await client.PostAsync("/Account/Profile", content);
            var page = await client.FollowAsync(upload);
            Assert.Contains("/uploads/logos/", page);
        }

        var remove = await client.PostFormAsync("/Account/Profile", "/Account/Profile", ("CompanyName", "Logo Co"), ("RemoveLogo", "true"));
        var after = await client.FollowAsync(remove);
        Assert.DoesNotContain("/uploads/logos/", after);
        Assert.Contains("No logo yet", after);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPasswordFails()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var response = await client.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword",
            ("CurrentPassword", "wrong"), ("NewPassword", "newsecret1"), ("ConfirmPassword", "newsecret1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Incorrect password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangePassword_SucceedsAndNewPasswordWorks()
    {
        var client = factory.CreateBrowser();
        var email = await client.RegisterAsync();

        var response = await client.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword",
            ("CurrentPassword", "secret123"), ("NewPassword", "newsecret1"), ("ConfirmPassword", "newsecret1"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Profile", response.Headers.Location?.ToString());
        Assert.Contains("Password changed.", await client.GetOkAsync("/Account/Profile"));

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateBrowser().LoginAsync(email, "secret123")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await factory.CreateBrowser().LoginAsync(email, "newsecret1")).StatusCode);
    }

    [Fact]
    public async Task AccessDenied_Renders()
    {
        Assert.Contains("Access denied", await factory.CreateBrowser().GetOkAsync("/Account/AccessDenied"));
    }

    [Fact]
    public async Task PostWithoutAntiForgeryTokenIsRejected()
    {
        var client = factory.CreateBrowser();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = NotifyWebFactory.AdminEmail, ["Password"] = NotifyWebFactory.AdminPassword
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string ExtractTokenFromPage(string html) => ClientExtensions.ExtractToken(html);
}
