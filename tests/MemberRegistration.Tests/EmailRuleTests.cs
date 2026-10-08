using MemberRegistration.Core.Mapping;

namespace MemberRegistration.Tests;

public class EmailRuleTests
{
    [Theory]
    [InlineData("alex.nguyen@example.com")]
    [InlineData("a@b.co")]
    [InlineData("alex+test@example.com.au")]
    [InlineData("o'brien@example.com")]
    [InlineData("ALEX@EXAMPLE.COM")]
    [InlineData("alex@sub-domain.example.org")]
    public void Accepts_plausible_addresses(string email)
    {
        Assert.True(EmailRule.IsPlausible(email));
    }

    [Theory]
    [InlineData("")]
    [InlineData("alex")]
    [InlineData("alex@")]
    [InlineData("@example.com")]
    [InlineData("alex@example")]
    [InlineData("alex@@example.com")]
    [InlineData("alex@exa@mple.com")]
    [InlineData("al ex@example.com")]
    [InlineData("alex@exam ple.com")]
    [InlineData("alex@example..com")]
    [InlineData("alex@.example.com")]
    [InlineData("alex@example.com.")]
    [InlineData("alex @example.com")]
    public void Rejects_implausible_addresses(string email)
    {
        Assert.False(EmailRule.IsPlausible(email));
    }

    [Fact]
    public void Enforces_the_254_character_limit()
    {
        const string domain = "@example.com";

        Assert.True(EmailRule.IsPlausible(new string('a', 254 - domain.Length) + domain));
        Assert.False(EmailRule.IsPlausible(new string('a', 255 - domain.Length) + domain));
    }
}
