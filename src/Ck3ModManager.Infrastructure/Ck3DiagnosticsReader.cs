using static Ck3ModManager.Core.AppText;
using System.Text;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

public sealed class Ck3DiagnosticsReader : ICk3DiagnosticsReader
{
    private const int MaxCrashes = 5;
    private const int TailBytes = 32 * 1024;
    private const int TailLines = 120;
    private static readonly string[] GameLogs = ["error.log", "game.log", "system.log", "setup.log", "debug.log", "gui_warnings.log", "database_conflicts.log"];
    private static readonly string[] CrashFiles = ["exception.txt", "meta.yml", "stacktrace.txt"];
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, true, true);
    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(true, true, true);

    public Task<Ck3DiagnosticsReport> ReadAsync(GameEnvironment environment, CancellationToken cancellationToken) =>
        Task.Run(() => Read(environment.UserDataDirectory, cancellationToken), cancellationToken);

    private static Ck3DiagnosticsReport Read(string userDirectory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(userDirectory)) throw new InvalidOperationException(T("SetTheCK3UserDataFolderFirst"));
        var root = Path.GetFullPath(userDirectory);
        var issues = new List<DiagnosticIssue>();
        if (!CanReadPath(root, root, directory: true, issues)) return new(root, null, [], issues);
        token.ThrowIfCancellationRequested();

        var logs = Path.Combine(root, "logs");
        DiagnosticGroup? session = null;
        if (CanReadPath(root, logs, directory: true, issues))
        {
            var files = ReadLogs(root, logs, GameLogs, issues, token);
            session = new(logs, null, files.Count == 0 ? null : files.Max(file => file.UpdatedAt), files);
        }

        var crashRoot = Path.Combine(root, "crashes");
        var recent = new PriorityQueue<string, (DateTimeOffset UpdatedAt, string Name)>();
        if (CanReadPath(root, crashRoot, directory: true, issues))
        {
            try
            {
                foreach (var path in Directory.EnumerateDirectories(crashRoot))
                {
                    token.ThrowIfCancellationRequested();
                    if (!CanReadPath(root, path, directory: true, issues)) continue;
                    recent.Enqueue(path, (Directory.GetLastWriteTimeUtc(path), Path.GetFileName(path)));
                    if (recent.Count > MaxCrashes) recent.Dequeue();
                }
            }
            catch (Exception error) when (IsReadableFailure(error))
            { AddIssue(root, crashRoot, DiagnosticReadIssue.Unavailable, issues); }
        }

        var crashes = new List<DiagnosticGroup>();
        foreach (var entry in recent.UnorderedItems.OrderByDescending(item => item.Priority))
        {
            token.ThrowIfCancellationRequested();
            var path = entry.Element;
            var files = ReadLogs(root, path, CrashFiles, issues, token);
            var nestedLogs = Path.Combine(path, "logs");
            if (CanReadPath(root, nestedLogs, directory: true, issues))
                files.AddRange(ReadLogs(root, nestedLogs, GameLogs, issues, token));
            crashes.Add(new(path, entry.Priority.Name, entry.Priority.UpdatedAt, files));
        }
        return new(root, session, crashes, issues);
    }

    private static List<DiagnosticLog> ReadLogs(string root, string directory, IEnumerable<string> names,
        List<DiagnosticIssue> issues, CancellationToken token)
    {
        var logs = new List<DiagnosticLog>();
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, name);
            if (!CanReadPath(root, path, directory: false, issues)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var (tail, truncated) = ReadTail(stream, token);
                logs.Add(new(Relative(root, path), File.GetLastWriteTimeUtc(path), tail, truncated));
            }
            catch (DecoderFallbackException) { AddIssue(root, path, DiagnosticReadIssue.NotText, issues); }
            catch (Exception error) when (IsReadableFailure(error))
            { AddIssue(root, path, DiagnosticReadIssue.Unavailable, issues); }
        }
        return logs;
    }

    private static (string Text, bool Truncated) ReadTail(FileStream stream, CancellationToken token)
    {
        var length = stream.Length;
        Span<byte> header = stackalloc byte[3];
        var headerLength = stream.ReadAtLeast(header, header.Length, false);
        var prefix = header[..headerLength];
        var encoding = prefix.StartsWith(Utf16.Preamble) ? Utf16 :
            prefix.StartsWith(Utf16BigEndian.Preamble) ? Utf16BigEndian : Utf8;
        var bom = encoding == Utf8 && prefix.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : encoding.Preamble.Length;
        var offset = Math.Max(bom, length - TailBytes);
        if (encoding != Utf8 && (offset - bom) % 2 != 0) offset++;
        stream.Position = offset;
        var data = new byte[(int)(length - offset)];
        stream.ReadExactly(data);
        token.ThrowIfCancellationRequested();

        var start = 0;
        var truncated = offset > bom;
        if (truncated && encoding == Utf8)
            while (start < data.Length && (data[start] & 0xc0) == 0x80) start++;
        if (truncated && encoding != Utf8 && data.Length >= 2)
        {
            var highByte = data[encoding == Utf16 ? 1 : 0];
            if (highByte is >= 0xdc and <= 0xdf) start = 2;
        }

        var text = encoding.GetString(data, start, data.Length - start);
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new DecoderFallbackException("Binary diagnostic file");
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        if (truncated && text.IndexOf('\n') is var first && first >= 0) text = text[(first + 1)..];
        var lines = text.Split('\n');
        return lines.Length > TailLines ? (string.Join('\n', lines.TakeLast(TailLines)), true) : (text, truncated);
    }

    private static bool CanReadPath(string root, string path, bool directory, List<DiagnosticIssue> issues)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 &&
                ((attributes & FileAttributes.Directory) != 0) == directory) return true;
            AddIssue(root, path, DiagnosticReadIssue.UnsafePath, issues);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (IsReadableFailure(error))
        { AddIssue(root, path, DiagnosticReadIssue.Unavailable, issues); }
        return false;
    }

    private static bool IsReadableFailure(Exception error) => error is IOException or UnauthorizedAccessException or NotSupportedException;

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void AddIssue(string root, string path, DiagnosticReadIssue reason, List<DiagnosticIssue> issues) =>
        issues.Add(new(Relative(root, path), reason));
}
