using MemberRegistration.Core.Mapping;

namespace MemberRegistration.Tests;

public class PlanCodeMapperTests
{
    [Theory]
    [InlineData("Single", "S")]
    [InlineData("Couple", "C")]
    [InlineData("Family", "F")]
    public void Maps_each_known_membership_type(string membershipType, string expected)
    {
        Assert.True(PlanCodeMapper.TryMap(membershipType, out var planCode));
        Assert.Equal(expected, planCode);
    }

    [Theory]
    [InlineData("single", "S")]
    [InlineData("SINGLE", "S")]
    [InlineData("cOuPlE", "C")]
    [InlineData("  Family  ", "F")]
    public void Ignores_case_and_surrounding_whitespace(string membershipType, string expected)
    {
        Assert.True(PlanCodeMapper.TryMap(membershipType, out var planCode));
        Assert.Equal(expected, planCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Gold")]
    [InlineData("Singles")]
    [InlineData("S")]
    [InlineData("F")]
    [InlineData("Si ngle")]
    public void Rejects_unknown_types(string membershipType)
    {
        Assert.False(PlanCodeMapper.TryMap(membershipType, out var planCode));
        Assert.Equal(string.Empty, planCode);
    }
}
