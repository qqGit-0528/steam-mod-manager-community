using static Ck3ModManager.Core.AppText;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ck3ModManager.Core;
using Microsoft.Data.Sqlite;

namespace Ck3ModManager.Infrastructure;

/// <summary>只修改本工具的本地播放集；由官方启动器生成对应游戏版本的加载文件。</summary>
public sealed class ParadoxGameModActivator(GameDefinition game, string storageRoot,
    Action<GameEnvironment>? ensureStopped = null) : IGameModActivator
{
    private const string Prefix = "Mod Manager · ";
    private sealed record Registration(string Id, string Name, string? Registry, string? Directory, string? Archive, string Status);

    public Task<ModProfile?> ReadAsync(GameEnvironment environment, IReadOnlyList<InstalledMod> installed,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (database, _) = GetPaths(environment);
        using var connection = Open(database, readOnly: true);
        ValidateSchema(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var activeName = connection.CreateCommand();
        activeName.Transaction = transaction;
        activeName.CommandText = "SELECT name FROM playsets WHERE isActive=1 AND isRemoved=0 LIMIT 1";
        if (activeName.ExecuteScalar() is not string name) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT p.name, m.id, COALESCE(m.displayName,m.name,''), m.gameRegistryId, m.dirPath, m.archivePath, m.status, pm.enabled " +
            "FROM playsets p JOIN playsets_mods pm ON pm.playsetId=p.id JOIN mods m ON m.id=pm.modId " +
            "WHERE p.isActive=1 AND p.isRemoved=0 ORDER BY pm.position";
        using var reader = command.ExecuteReader();
        var mods = new List<ProfileMod>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            name = reader.GetString(0);
            var registration = new Registration(reader.GetString(1), reader.GetString(2), String(reader, 3), String(reader, 4), String(reader, 5), reader.GetString(6));
            var found = installed.FirstOrDefault(mod => Matches(registration, mod, environment.UserDataDirectory));
            var enabled = !reader.IsDBNull(7) && reader.GetBoolean(7);
            mods.Add(new ProfileMod(found?.Id ?? "launcher:" + registration.Id, found?.Name ?? registration.Name,
                found?.ContentPath ?? registration.Directory ?? registration.Archive ?? "", found?.WorkshopId,
                found?.SupportedGameVersion, found?.ModVersion) { Enabled = enabled });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (ModProfile?)new ModProfile(Guid.NewGuid(), name.StartsWith(Prefix) ? name[Prefix.Length..] : name,
            environment.GameVersion, mods.GroupBy(mod => mod.InstalledModId)
                .Select(group => group.First() with { Enabled = group.Any(mod => mod.Enabled) }).ToArray(), DateTimeOffset.UtcNow);
    }, cancellationToken);

    public Task<GameProfileApplyResult> ApplyAsync(GameEnvironment environment, ModProfile profile,
        IReadOnlyList<InstalledMod> installed, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var (database, format) = GetPaths(environment);
        CheckStopped(environment);
        var lockPath = Path.Combine(environment.UserDataDirectory, ".modmanager-activation.lock");
        RejectLink(lockPath);
        using var fileLock = AcquireLock(lockPath);
        using var connection = Open(database, readOnly: false);
        ValidateSchema(connection);
        var backupDirectory = Path.Combine(Path.GetFullPath(storageRoot), "activation-backups");
        Directory.CreateDirectory(backupDirectory);
        // 在创建新备份之前腾出一个名额；失败和取消同样受五份上限约束。
        foreach (var old in new DirectoryInfo(backupDirectory).GetFiles("activation-*.sqlite")
                     .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name).Skip(4)) old.Delete();
        var backupPath = Path.Combine(backupDirectory, $"activation-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
        using (var backup = Open(backupPath, readOnly: false, create: true)) connection.BackupDatabase(backup);
        var createdWrappers = new List<string>();
        var committed = false;
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"Ck3ModManager/{game.AppId}/{profile.Id}"))[..16];
            identity[7] = (byte)((identity[7] & 0x0f) | 0x50);
            identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
            var id = new Guid(identity).ToString();
            var existing = Scalar(connection, transaction, "SELECT name FROM playsets WHERE id=$id", ("$id", id));
            if (existing is string ownedName && !ownedName.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidDataException(T("ThePlaysetIDConflictsWithAnotherProfile"));
            if (existing is not null && Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COUNT(*) FROM playsets WHERE id=$id AND (pdxId IS NOT NULL OR state<>'private' OR owned<>1 OR isRemoved<>0)", ("$id", id))) != 0)
                throw new InvalidOperationException(T("TheLauncherSharesOrManagesThisPlayset"));
            if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM playsets WHERE isActive=1")) > 1)
                throw new InvalidDataException(T("TheLauncherHasMultipleActivePlaysetsChoose"));
            var dlcs = new List<(string Id, bool Enabled)>();
            using (var command = Command(connection, transaction, "SELECT d.dlcId,d.enabled FROM playsets_dlcs d JOIN playsets p ON p.id=d.playsetId WHERE p.isActive=1"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) dlcs.Add((reader.GetString(0), reader.GetBoolean(1)));
            var registrations = ReadRegistrations(connection, transaction);
            var entries = new List<(string Id, bool Enabled)>();
            foreach (var desired in profile.Mods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mod = installed.FirstOrDefault(mod => mod.Id == desired.InstalledModId);
                var registration = mod is null
                    ? registrations.FirstOrDefault(row => "launcher:" + row.Id == desired.InstalledModId)
                    : registrations.FirstOrDefault(row => Matches(row, mod, environment.UserDataDirectory));
                if (mod is null || !mod.IsAvailable)
                {
                    if (desired.Enabled) throw new InvalidOperationException(T("ModFilesAreMissing", desired.DisplayName));
                    if (registration is not null) entries.Add((registration.Id, false));
                    continue;
                }
                if (registration is not null && registration.Status != "ready_to_play" && desired.Enabled)
                    throw new InvalidOperationException(T("CheckThisModInTheOfficialLauncher", desired.DisplayName));
                // 未登记的禁用项只保存在本工具清单中，不应阻止其他有效项应用。
                if (registration is null && !desired.Enabled) continue;
                if (registration is null)
                {
                    var registry = format == 0 ? EnsureDescriptor(mod, environment.UserDataDirectory, createdWrappers) : null;
                    if (format != 0 && (!Directory.Exists(mod.ContentPath) || !File.Exists(Path.Combine(mod.ContentPath, ".metadata", "metadata.json"))))
                        throw new InvalidOperationException(T("AModIsMissingTheMetadataJson", mod.Name));
                    if (format == 1.1 && !IsNativeLocalDirectory(environment, mod.ContentPath))
                        throw new NotSupportedException(T("TheLauncherHasNotDiscoveredThisMod", mod.Name));
                    var modId = Guid.NewGuid().ToString();
                    Execute(connection, transaction,
                        "INSERT INTO mods(id,name,displayName,gameRegistryId,dirPath,archivePath,status,source,isNew,createdDate,requiredVersion,version) " +
                        "VALUES($id,$name,$name,$registry,$directory,$archive,'ready_to_play','local',0,$created,$required,$version)",
                        ("$id", modId), ("$name", mod.Name), ("$registry", registry),
                        ("$directory", Directory.Exists(mod.ContentPath) ? Path.GetFullPath(mod.ContentPath) : null),
                        ("$archive", File.Exists(mod.ContentPath) ? Path.GetFullPath(mod.ContentPath) : null),
                        ("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$required", mod.SupportedGameVersion), ("$version", mod.ModVersion));
                    registration = new Registration(modId, mod.Name, registry, mod.ContentPath, null, "ready_to_play");
                    registrations.Add(registration);
                }
                if (entries.Any(entry => entry.Id == registration.Id)) throw new InvalidOperationException(T("AModMayAppearOnlyOnceIn"));
                entries.Add((registration.Id, desired.Enabled));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Select(entry => entry.Id).Distinct().Count() != entries.Count)
                throw new InvalidOperationException(T("AModMayAppearOnlyOnceIn"));
            if (format == 1.1)
            {
                var selected = registrations.Where(row => entries.Any(entry => entry.Id == row.Id && entry.Enabled)).ToArray();
                if (selected.Any(row => row.Archive is not null) && selected.Any(row => row.Directory is not null))
                    throw new NotSupportedException(T("ThisGameLoadsFoldersBeforeArchivesUse"));
            }
            var title = Prefix + profile.Name.Trim();
            if (existing is null)
                Execute(connection, transaction, "INSERT INTO playsets(id,name,isActive,loadOrder,createdOn,updatedOn,syncState) VALUES($id,$name,0,'custom',$now,$now,'NOT_ELIGIBLE')",
                    ("$id", id), ("$name", title), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            else
                Execute(connection, transaction, "UPDATE playsets SET name=$name,loadOrder='custom',updatedOn=$now WHERE id=$id",
                    ("$id", id), ("$name", title), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Execute(connection, transaction, "DELETE FROM playsets_mods WHERE playsetId=$id", ("$id", id));
            for (var i = 0; i < entries.Count; i++)
                Execute(connection, transaction, "INSERT INTO playsets_mods(playsetId,modId,enabled,position) VALUES($id,$mod,$enabled,$position)",
                    ("$id", id), ("$mod", entries[i].Id), ("$enabled", entries[i].Enabled), ("$position", i));
            Execute(connection, transaction, "DELETE FROM playsets_dlcs WHERE playsetId=$id", ("$id", id));
            foreach (var dlc in dlcs.DistinctBy(dlc => dlc.Id))
                Execute(connection, transaction, "INSERT INTO playsets_dlcs(playsetId,dlcId,enabled) VALUES($id,$dlc,$enabled)",
                    ("$id", id), ("$dlc", dlc.Id), ("$enabled", dlc.Enabled));
            Execute(connection, transaction, "UPDATE playsets SET isActive=0 WHERE isActive=1");
            Execute(connection, transaction, "UPDATE playsets SET isActive=1 WHERE id=$id", ("$id", id));
            CheckStopped(environment);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            committed = true;
            return new GameProfileApplyResult(title, entries.Count(entry => entry.Enabled), backupPath);
        }
        catch
        {
            foreach (var wrapper in committed ? [] : createdWrappers)
                try { File.Delete(wrapper); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }, cancellationToken);

    private (string Database, double Format) GetPaths(GameEnvironment environment)
    {
        if (game.Id == "prisonarchitect") throw new NotSupportedException(T("PrisonArchitectDoesNotUseParadoxPlaysets"));
        if (environment.GameDirectory is null) throw new InvalidOperationException(T("TheGameInstallationWasNotFoundCheck"));
        var settings = new[] { "launcher/launcher-settings.json", "Launcher/launcher-settings.json", "launcher-settings.json" }
            .Select(path => Path.Combine(environment.GameDirectory, path)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(T("TheOfficialLauncherConfigurationWasNotFound"));
        using var data = JsonDocument.Parse(TextDataParser.ReadSmallFile(settings));
        var id = data.RootElement.GetProperty("gameId").GetString();
        if (id != (game.Id == "imperator" ? "imperator_rome" : game.Id)) throw new InvalidDataException(T("TheInstallationFolderDoesNotMatchThe"));
        var format = data.RootElement.GetProperty("formatVersion").GetDouble();
        if (format != 0 && format != 1.1) throw new NotSupportedException(T("LauncherFormatIsUnsupported", format));
        var database = Path.Combine(Path.GetFullPath(environment.UserDataDirectory), "launcher-v2.sqlite");
        if (!File.Exists(database)) throw new InvalidOperationException(T("OpenTheOfficialLauncherOnceToDiscover"));
        RejectLink(database);
        return (database, format);
    }

    private void CheckStopped(GameEnvironment environment)
    {
        if (ensureStopped is not null) { ensureStopped(environment); return; }
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            string name;
            bool isLauncher;
            try
            {
                name = process.ProcessName;
                isLauncher = name.Equals("launcher", StringComparison.OrdinalIgnoreCase) &&
                    (process.MainModule?.FileName?.Contains("Paradox", StringComparison.OrdinalIgnoreCase) ?? true);
            }
            catch (InvalidOperationException) { continue; } // 进程在枚举后正常退出。
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException(T("CouldNotConfirmThatTheLauncherClosed")); }
            if (name.Equals(game.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Paradox Launcher", StringComparison.OrdinalIgnoreCase) || name.Equals("dowser", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("bootstrapper-v2", StringComparison.OrdinalIgnoreCase) || isLauncher)
                throw new InvalidOperationException(T("CloseTheGameAndParadoxLauncherBefore"));
        }
    }

    private static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException(T("AnotherAppInstanceIsWritingAProfile"), error); }
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException(T("TheSettingsFileCannotBeASymbolic"));
    }

    private static SqliteConnection Open(string path, bool readOnly, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 3, ForeignKeys = !readOnly }.ToString());
        connection.Open();
        return connection;
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        foreach (var (table, required) in new[]
        {
            ("mods", new[] { "id", "gameRegistryId", "dirPath", "archivePath", "status", "source", "name", "displayName", "isNew", "createdDate", "requiredVersion", "version" }),
            ("playsets", new[] { "id", "name", "isActive", "isRemoved", "loadOrder", "createdOn", "updatedOn", "syncState", "pdxId", "state", "owned" }),
            ("playsets_mods", new[] { "playsetId", "modId", "enabled", "position" }),
            ("playsets_dlcs", new[] { "playsetId", "dlcId", "enabled" })
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) fields.Add(reader.GetString(1));
            if (required.Any(field => !fields.Contains(field))) throw new NotSupportedException(T("TheLauncherDatabaseFormatIsUnsupportedThe"));
        }
    }

    private static List<Registration> ReadRegistrations(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,COALESCE(displayName,name,''),gameRegistryId,dirPath,archivePath,status FROM mods");
        using var reader = command.ExecuteReader();
        var result = new List<Registration>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), String(reader, 2), String(reader, 3), String(reader, 4), reader.GetString(5)));
        return result;
    }

    private static string? String(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static bool IsNativeLocalDirectory(GameEnvironment environment, string content)
    {
        var settings = new[] { "launcher/launcher-settings.json", "Launcher/launcher-settings.json", "launcher-settings.json" }
            .Select(path => Path.Combine(environment.GameDirectory!, path)).First(File.Exists);
        using var data = JsonDocument.Parse(TextDataParser.ReadSmallFile(settings));
        var path = data.RootElement.TryGetProperty("modPath", out var configured) ? configured.GetString() : "mod";
        var nativeDirectory = Path.GetFullPath(Path.Combine(environment.UserDataDirectory, path ?? "mod"));
        return SteamGameLocator.PathComparer.Equals(nativeDirectory, Path.GetDirectoryName(Path.GetFullPath(content)));
    }
    private static bool Matches(Registration row, InstalledMod mod, string root) =>
        SamePath(row.Directory, mod.ContentPath, root) || SamePath(row.Archive, mod.ContentPath, root) || SamePath(row.Registry, mod.DescriptorPath, root);
    private static bool SamePath(string? first, string second, string root)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        return SteamGameLocator.PathComparer.Equals(Path.GetFullPath(Path.Combine(root, first)), Path.GetFullPath(second));
    }

    private static string EnsureDescriptor(InstalledMod mod, string root, List<string> created)
    {
        var relative = Path.GetRelativePath(root, mod.DescriptorPath).Replace('\\', '/');
        if (relative.StartsWith("mod/", StringComparison.Ordinal) && !relative.Contains("/../") && relative.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)) return relative;
        var original = TextDataParser.ReadSmallFile(mod.DescriptorPath);
        var properties = TextDataParser.Read(original);
        if (properties.Value("path") is not null || properties.Value("archive") is not null)
            throw new InvalidDataException(T("MoveTheOuterModFileForThis", mod.Name));
        var folder = Path.Combine(root, "mod");
        if (new DirectoryInfo(folder).LinkTarget is not null) throw new InvalidDataException(T("TheModDescriptorFolderCannotBeA"));
        Directory.CreateDirectory(folder);
        var name = "app_mm_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(mod.ContentPath)))) + ".mod";
        var path = Path.Combine(folder, name);
        RejectLink(path);
        var field = Directory.Exists(mod.ContentPath) ? "path" : "archive";
        var text = LocalModDescriptor.Write(properties, field, Path.GetFullPath(mod.ContentPath), mod.WorkshopId);
        if (File.Exists(path))
        {
            if (File.ReadAllText(path) != text) throw new InvalidDataException(T("TheModDescriptorCreatedByThisApp"));
        }
        else
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Add(path);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text); writer.Flush(); stream.Flush(true);
        }
        return "mod/" + name;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }
}
