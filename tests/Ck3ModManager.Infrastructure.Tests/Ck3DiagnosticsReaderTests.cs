using System.Text;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure.Tests;

public sealed class Ck3DiagnosticsReaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ck3-diagnostics-");
    private GameEnvironment Environment => new(null, _root.FullName, "1.20.0.4", [], []);

    [Fact]
    public async Task SeparatesSessionFromTheFiveMostRecentCrashesAndPreservesLogText()
    {
        var token = TestContext.Current.CancellationToken;
        File.WriteAllText(PathFor("logs/error.log"), "[E] latest session");
        var now = DateTime.UtcNow;
        for (var index = 0; index < 7; index++)
        {
            File.WriteAllText(PathFor($"crashes/ck3_{index}/exception.txt"), $"原始异常 {index}", Encoding.Unicode);
            File.WriteAllText(PathFor($"crashes/ck3_{index}/logs/error.log"), $"[E] past {index}");
            Directory.SetLastWriteTimeUtc(Path.Combine(_root.FullName, "crashes", $"ck3_{index}"), now.AddDays(index - 7));
        }

        var report = await new Ck3DiagnosticsReader().ReadAsync(Environment, token);
        var session = Assert.Single(report.Session!.Logs);
        Assert.Equal("logs/error.log", session.RelativePath);
        Assert.Equal("[E] latest session", session.Tail);
        Assert.Equal(new[] { "ck3_6", "ck3_5", "ck3_4", "ck3_3", "ck3_2" }, report.Crashes.Select(crash => crash.ReportName));
        var recent = report.Crashes[0];
        Assert.Equal("原始异常 6", recent.Logs[0].Tail);
        Assert.Equal("[E] past 6", recent.Logs[1].Tail);
        Assert.Equal(Path.Combine(_root.FullName, "crashes", "ck3_6"), recent.Directory);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task LimitsTheTailAndKeepsOtherFilesWhenALogIsInvalid()
    {
        File.WriteAllLines(PathFor("logs/error.log"), Enumerable.Range(0, 4000).Select(i => $"测试 {i:D4} {new string('x', 50)}"));
        File.WriteAllBytes(PathFor("logs/game.log"), [0x00, 0xff, 0xfd, 0x00]);
        File.WriteAllText(PathFor("logs/system.log"), "");
        var report = await new Ck3DiagnosticsReader().ReadAsync(Environment, TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Session!.Logs.Count);
        var tail = report.Session.Logs[0];
        Assert.True(tail.Truncated);
        Assert.Equal(120, tail.Tail.Split('\n').Length);
        Assert.StartsWith("测试 3880", tail.Tail);
        Assert.Contains("测试 3999", tail.Tail);
        Assert.Equal("", report.Session.Logs[1].Tail);
        Assert.Equal(new DiagnosticIssue("logs/game.log", DiagnosticReadIssue.NotText), Assert.Single(report.Issues));
    }

    [Fact]
    public async Task EmptyAndCancelledReadsLeaveTheDirectoryUntouched()
    {
        var reader = new Ck3DiagnosticsReader();
        var report = await reader.ReadAsync(Environment, TestContext.Current.CancellationToken);
        Assert.Null(report.Session);
        Assert.Empty(report.Crashes);
        Assert.Empty(report.Issues);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Environment, cancelled.Token));
        Assert.Empty(_root.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task NeverFollowsLinkedDiagnosticFilesOrCrashFolders()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = _root.CreateSubdirectory("outside").FullName;
        var file = Path.Combine(outside, "private.txt");
        File.WriteAllText(file, "must not read");
        File.CreateSymbolicLink(PathFor("logs/error.log"), file);
        Directory.CreateSymbolicLink(PathFor("crashes/linked"), outside);
        var report = await new Ck3DiagnosticsReader().ReadAsync(Environment, TestContext.Current.CancellationToken);
        Assert.Empty(report.Session!.Logs);
        Assert.Empty(report.Crashes);
        Assert.Equal(new[]
        {
            new DiagnosticIssue("logs/error.log", DiagnosticReadIssue.UnsafePath),
            new DiagnosticIssue("crashes/linked", DiagnosticReadIssue.UnsafePath)
        }, report.Issues);
    }

    private string PathFor(string relative)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public void Dispose() => _root.Delete(recursive: true);
}
