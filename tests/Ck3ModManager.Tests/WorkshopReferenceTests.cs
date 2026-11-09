using Ck3ModManager.Core;

namespace Ck3ModManager.Tests;

public sealed class WorkshopReferenceTests
{
    [Theory]
    [InlineData("1", 1UL)]
    [InlineData(" 123456 ", 123456UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=123", 123UL)]
    [InlineData("http://steamcommunity.com/workshop/filedetails?id=456&searchtext=test", 456UL)]
    [InlineData("https://STEAMCOMMUNITY.COM/sharedfiles/filedetails/?searchtext=a&id=789#description", 789UL)]
    public void ParsesOnlySupportedReferenceForms(string text, ulong expected)
    {
        var reference = WorkshopReference.Parse(text);
        Assert.Equal(expected, reference.PublishedFileId);
        Assert.Equal($"https://steamcommunity.com/sharedfiles/filedetails/?id={expected}", reference.WorkshopUrl.AbsoluteUri);
        Assert.True(WorkshopReference.TryParse(text, out var result));
        Assert.Equal(reference, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("123\0")]
    [InlineData("18446744073709551616")]
    [InlineData("https://example.com/sharedfiles/filedetails/?id=123")]
    [InlineData("https://steamcommunity.com.evil.example/sharedfiles/filedetails/?id=123")]
    [InlineData("https://steamcommunity.com@evil.example/sharedfiles/filedetails/?id=123")]
    [InlineData("https://user@steamcommunity.com/sharedfiles/filedetails/?id=123")]
    [InlineData("ftp://steamcommunity.com/sharedfiles/filedetails/?id=123")]
    [InlineData("steam://url/CommunityFilePage/123")]
    [InlineData("https://steamcommunity.com/app/1158310/workshop/?id=123")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails///?id=123")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=0")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=18446744073709551616")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=123&id=456")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=123&id=123")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=hello")]
    [InlineData("https://steamcommunity.com:1234/sharedfiles/filedetails/?id=123")]
    public void RejectsInvalidOrAmbiguousReferences(string? text)
    {
        Assert.False(WorkshopReference.TryParse(text, out var reference));
        Assert.Null(reference);
    }

    [Fact]
    public void ParseReportsActionableFormatError() =>
        Assert.Throws<FormatException>(() => WorkshopReference.Parse("https://example.com/?id=1"));
}
