using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class CompatibilityEvaluatorTests
{
    [Theory]
    [InlineData("1.12.*", "1.12.5", CompatibilityStatus.DeclaredMatch)]
    [InlineData("1.12.x", "1.12.5.2", CompatibilityStatus.DeclaredMatch)]
    [InlineData("1.X.*", "1.12.5", CompatibilityStatus.DeclaredMatch)]
    [InlineData("1.12", "1.12.5", CompatibilityStatus.DeclaredMatch)]
    [InlineData("1.12.5", "1.12.5", CompatibilityStatus.DeclaredMatch)]
    [InlineData("*", "1.12.5", CompatibilityStatus.DeclaredMatch)]
    [InlineData(" 1.12.* ", " 1.12.5 ", CompatibilityStatus.DeclaredMatch)]
    [InlineData("1.12.*", "1.13.0", CompatibilityStatus.DeclaredMismatch)]
    [InlineData("1.12.5", "1.12.6", CompatibilityStatus.DeclaredMismatch)]
    [InlineData("1.2", "1.20.1", CompatibilityStatus.DeclaredMismatch)]
    [InlineData(null, "1.12.5", CompatibilityStatus.Unknown)]
    [InlineData("", "1.12.5", CompatibilityStatus.Unknown)]
    [InlineData("1.12.*", null, CompatibilityStatus.Unknown)]
    [InlineData("1.12.*", "1.12.*", CompatibilityStatus.Unknown)]
    [InlineData("1.*.3", "1.12.3", CompatibilityStatus.Unknown)]
    [InlineData("1.12 beta", "1.12.3", CompatibilityStatus.Unknown)]
    [InlineData("1..12", "1.12.3", CompatibilityStatus.Unknown)]
    [InlineData(">=1.12", "1.12.3", CompatibilityStatus.Unknown)]
    [InlineData("1.12.3", "1.12", CompatibilityStatus.Unknown)]
    [InlineData("1.4294967296", "1.12", CompatibilityStatus.Unknown)]
    public void EvaluatesDeclaredVersionPrefixWithoutClaimingTestedCompatibility(
        string? supported, string? actual, CompatibilityStatus expected)
    {
        var result = CompatibilityEvaluator.Evaluate(supported, actual);
        Assert.Equal(expected, result.Status);
        Assert.Contains("非实测", result.Message);
    }
}
