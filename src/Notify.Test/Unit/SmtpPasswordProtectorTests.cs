using Microsoft.AspNetCore.DataProtection;
using Notify.Web.Services;

namespace Notify.Test.Unit;

public class SmtpPasswordProtectorTests
{
    private readonly SmtpPasswordProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void RoundTrip_ReturnsOriginal()
    {
        var cipher = _protector.Protect("s3cret!");
        Assert.NotNull(cipher);
        Assert.NotEqual("s3cret!", cipher);
        Assert.Equal("s3cret!", _protector.Unprotect(cipher));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmptyReturnsNull(string? value)
    {
        Assert.Null(_protector.Protect(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unprotect_NullOrEmptyReturnsNull(string? value)
    {
        Assert.Null(_protector.Unprotect(value));
    }

    [Fact]
    public void Unprotect_TamperedOrForeignCipherReturnsNullInsteadOfThrowing()
    {
        Assert.Null(_protector.Unprotect("not-a-valid-payload"));

        var other = new SmtpPasswordProtector(new EphemeralDataProtectionProvider());
        var cipher = other.Protect("secret")!;
        Assert.Null(_protector.Unprotect(cipher));
    }

    [Fact]
    public void Protect_ProducesDifferentCiphertextEachTime()
    {
        Assert.NotEqual(_protector.Protect("same"), _protector.Protect("same"));
    }
}
