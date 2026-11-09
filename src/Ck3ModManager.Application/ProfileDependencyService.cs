using static Ck3ModManager.Core.AppText;
using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed record DependencyQueueResult(int Queued, IReadOnlyList<string> Failures);

public sealed class ProfileDependencyService(WorkshopService workshop, IWorkshopDependencySource source)
{
    private const int MaxItems = 128;

    private sealed record Node(string Key, string Name, ulong? WorkshopId, InstalledMod? Installation);
    private sealed record WorkshopDependencies(WorkshopItem? Item, IReadOnlyList<ulong>? Required);

    public async Task<ProfileDependencyReport> CheckAsync(ModProfile profile, IReadOnlyList<InstalledMod> installations,
        CancellationToken token = default)
    {
        var available = installations.Where(mod => mod.IsAvailable).DistinctBy(mod => mod.Id).ToArray();
        var byId = available.ToLookup(mod => mod.Id, StringComparer.Ordinal);
        var byName = available.ToLookup(mod => mod.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var byWorkshopId = available.Where(mod => mod.WorkshopId is > 0).ToLookup(mod => mod.WorkshopId!.Value);
        var enabled = profile.Mods.Where(mod => mod.Enabled).Select(mod => mod.InstalledModId).ToHashSet(StringComparer.Ordinal);
        var findings = new List<DependencyFinding>();
        var stack = new List<Node>();
        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var requests = new Dictionary<ulong, WorkshopDependencies>();
        var missing = new HashSet<ulong>();
        var visitedMissingInDependencyOrder = new List<ulong>();
        var cyclic = new HashSet<ulong>();
        var reachedLimit = false;

        foreach (var root in profile.Mods.Where(mod => mod.Enabled))
        {
            token.ThrowIfCancellationRequested();
            var installed = byId[root.InstalledModId].FirstOrDefault();
            if (installed is not null)
                await VisitAsync(InstalledNode(installed)).ConfigureAwait(false);
            else
            {
                findings.Add(new(T("TheInstallationSelectedInThisProfileIs", root.DisplayName), root.WorkshopId, root.InstalledModId));
                if (root.WorkshopId is > 0)
                    await VisitAsync(RemoteNode(root.WorkshopId.Value, root.DisplayName)).ConfigureAwait(false);
            }
            if (reachedLimit) break;
        }

        if (reachedLimit) findings.Add(new(T("ReachedTheItemLimitSomeDependenciesWere", MaxItems)));
        var downloadable = visitedMissingInDependencyOrder.Where(id => missing.Contains(id) && !cyclic.Contains(id))
            .Distinct().Select(id => requests.GetValueOrDefault(id)?.Item).OfType<WorkshopItem>().ToArray();
        return new(completed.Count, findings.Distinct().ToArray(), downloadable);

        async Task VisitAsync(Node node)
        {
            token.ThrowIfCancellationRequested();
            if (completed.Contains(node.Key) || reachedLimit) return;
            if (active.TryGetValue(node.Key, out var from))
            {
                var cycle = stack.Skip(from).Append(node).ToArray();
                foreach (var entry in cycle)
                    if (entry.WorkshopId is { } id) cyclic.Add(id);
                findings.Add(new(T("DependencyCycle", string.Join(" → ", cycle.Select(entry => entry.Name)))));
                return;
            }
            if (completed.Count + active.Count >= MaxItems)
            {
                reachedLimit = true;
                return;
            }

            WorkshopDependencies? remote = null;
            if (node.WorkshopId is > 0)
            {
                remote = await ReadWorkshopAsync(node.WorkshopId.Value).ConfigureAwait(false);
                if (node.Installation is null && remote.Item is { } item) node = node with { Name = item.Title };
            }
            active.Add(node.Key, stack.Count);
            stack.Add(node);
            if (remote is { Required: null })
                findings.Add(new(T("WorkshopDependenciesCouldNotBeVerified", node.Name), node.WorkshopId, node.Key));

            foreach (var dependency in node.Installation?.Dependencies ?? [])
            {
                token.ThrowIfCancellationRequested();
                var candidate = SelectCopy(node, dependency, byName[dependency.Trim()]);
                if (candidate is not null) await VisitAsync(InstalledNode(candidate)).ConfigureAwait(false);
                if (reachedLimit) break;
            }
            foreach (var id in remote?.Required ?? [])
            {
                if (reachedLimit) break;
                token.ThrowIfCancellationRequested();
                var candidates = byWorkshopId[id];
                if (candidates.Any())
                {
                    var candidate = SelectCopy(node, T("Workshop2", id), candidates);
                    if (candidate is not null) await VisitAsync(InstalledNode(candidate)).ConfigureAwait(false);
                }
                else
                {
                    missing.Add(id);
                    await VisitAsync(RemoteNode(id, T("Workshop2", id))).ConfigureAwait(false);
                    var detail = requests.GetValueOrDefault(id)?.Item;
                    findings.Add(new(T("RequiredModIsMissing", node.Name, detail?.Title ?? T("Workshop2", id)), id, node.Key));
                }
            }
            if (node.Installation is null && node.WorkshopId is { } remoteId)
                visitedMissingInDependencyOrder.Add(remoteId);
            active.Remove(node.Key);
            stack.RemoveAt(stack.Count - 1);
            completed.Add(node.Key);
        }

        InstalledMod? SelectCopy(Node parent, string reference, IEnumerable<InstalledMod> candidates)
        {
            var copies = candidates.ToArray();
            var selected = copies.Where(mod => enabled.Contains(mod.Id)).ToArray();
            if (selected.Length == 1) return selected[0];
            if (copies.Length == 0)
                findings.Add(new(T("DependencyDeclaredByTheLocalDescriptorIs", parent.Name, reference), SourceKey: parent.Key));
            else if (copies.Length > 1)
                findings.Add(new(T("MultipleCopiesMatchChooseOneInThe", parent.Name, reference), SourceKey: parent.Key));
            else
            {
                findings.Add(new(T("InstalledButDisabled", parent.Name, copies[0].Name), copies[0].WorkshopId, parent.Key));
                return copies[0];
            }
            return null;
        }

        async Task<WorkshopDependencies> ReadWorkshopAsync(ulong id)
        {
            if (requests.TryGetValue(id, out var existing)) return existing;
            WorkshopItem? item = null;
            IReadOnlyList<ulong>? required = null;
            try
            {
                item = await workshop.LookupAsync(id.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                required = item.RequiredItemIds ?? await source.ReadRequiredItemsAsync(workshop.Game.AppId, id, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { }
            token.ThrowIfCancellationRequested();
            var result = new WorkshopDependencies(item, required?.Where(value => value > 0).Distinct().ToArray());
            requests.Add(id, result);
            return result;
        }
    }

    public async Task<DependencyQueueResult> QueueAsync(ProfileDependencyReport review, ModScanResult scan,
        WorkshopDownloadService downloads, IProgress<WorkshopDownloadSnapshot> progress, CancellationToken token)
    {
        var directory = scan.Environment?.UserDataDirectory;
        if (!workshop.Game.SupportsNativeWorkshopDownloads || directory is null || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException(T("ThisGameOrUserFolderDoesNot"));
        var installed = scan.Mods.Where(mod => mod.IsAvailable && mod.WorkshopId is > 0)
            .Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var items = review.MissingWorkshopItems;
        if (items.Any(item => item.ConsumerAppId != workshop.Game.AppId || item.PublishedFileId == 0))
            throw new InvalidDataException(T("ADependencyHasAnInvalidIDOr"));

        var queued = 0;
        var failures = new List<string>();
        foreach (var item in items.DistinctBy(item => item.PublishedFileId))
        {
            token.ThrowIfCancellationRequested();
            if (installed.Contains(item.PublishedFileId)) continue;
            try
            {
                var task = downloads.Start(item, directory);
                await task.IdentityReady.WaitAsync(token);
                progress.Report(task.Snapshot);
                queued++;
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            { failures.Add($"{item.Title}：{error.Message}"); }
        }
        return new(queued, failures);
    }

    private static Node InstalledNode(InstalledMod mod) => new("installed:" + mod.Id, mod.Name, mod.WorkshopId, mod);

    private static Node RemoteNode(ulong id, string name) =>
        new("steam:" + id.ToString(CultureInfo.InvariantCulture), name, id, null);
}
