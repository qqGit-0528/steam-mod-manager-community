using static Ck3ModManager.Core.AppText;
using System.Globalization;
using Ck3ModManager.Core;

namespace Ck3ModManager.Application;

public sealed record ProfileImportReview(PortableModProfile Document, PortableProfilePreview Preview, IReadOnlyList<ProfileMod> Downloads);
public sealed record ProfileImportPlan(ProfileImportReview Review, string UserDataDirectory, bool RequiresConfirmation);
public sealed record ProfileImportFailure(string ModName, string Message);
public sealed record ProfileImportResult(int DownloadCount, IReadOnlyList<ProfileImportFailure> Failures);

public sealed class ProfileImportService(WorkspaceService workspace, WorkshopService workshop, WorkshopDownloadService? downloads)
{
    public async Task<ProfileImportReview> PreviewAsync(Stream source, IReadOnlyList<InstalledMod> installed,
        string userDataDirectory, CancellationToken token)
    {
        var document = await PortableProfileService.ReadAsync(source, workshop.Game, token);
        return Match(document, installed, userDataDirectory);
    }

    public async Task<ProfileImportPlan> RecheckAsync(AppSettings settings, ProfileImportReview approved, CancellationToken token,
        IReadOnlyDictionary<int, PortableProfileCandidate>? choices = null)
    {
        var scan = await workspace.ScanLocalModsAsync(settings, token);
        var userDirectory = scan.Environment!.UserDataDirectory;
        var current = Match(approved.Document, scan.Mods, userDirectory, choices);
        var approvedIds = approved.Downloads.Select(mod => mod.WorkshopId).ToHashSet();
        var requiresConfirmation = current.Downloads.Any(mod => !approvedIds.Contains(mod.WorkshopId));
        for (var index = 0; index < current.Preview.Entries.Count && !requiresConfirmation; index++)
        {
            var entry = current.Preview.Entries[index];
            var previous = approved.Preview.Entries[index];
            if (choices?.GetValueOrDefault(index) is { } choice)
                requiresConfirmation = !entry.Candidates.Contains(choice);
            else if (previous.Status == PortableProfileMatchStatus.Installed)
                requiresConfirmation = entry.Status != previous.Status || entry.Mod != previous.Mod;
            else
                requiresConfirmation = entry.Status != PortableProfileMatchStatus.Installed &&
                    (entry.Status != previous.Status || !entry.Candidates.SequenceEqual(previous.Candidates));
        }
        if (requiresConfirmation) current = Match(approved.Document, scan.Mods, userDirectory);
        return new(current, userDirectory, requiresConfirmation);
    }

    public async Task<ProfileImportResult> CommitAsync(ProfileImportPlan plan, IProgress<ModProfile> profileSaved,
        IProgress<WorkshopDownloadSnapshot> downloadQueued, CancellationToken token)
    {
        if (plan.RequiresConfirmation) throw new InvalidOperationException(T("LocalModsChangedReviewTheMissingItems"));
        if (plan.Review.Document.GameAppId != workshop.Game.AppId)
            throw new InvalidOperationException(T("ThisProfileBelongsToAnotherGame"));
        if (plan.Review.Downloads.Count != 0 && downloads is null)
            throw new InvalidOperationException(T("DownloadsAreUnavailableInThisWorkspace"));
        var profile = plan.Review.Preview.Profile;
        token.ThrowIfCancellationRequested();
        await workspace.SaveProfileAsync(profile, token);
        profileSaved.Report(profile);

        var failures = new List<ProfileImportFailure>();
        foreach (var mod in plan.Review.Downloads)
        {
            token.ThrowIfCancellationRequested();
            var id = mod.WorkshopId!.Value;
            try
            {
                var task = downloads!.Tasks.FirstOrDefault(existing => existing.Snapshot.AppId == workshop.Game.AppId &&
                    existing.Snapshot.PublishedFileId == id && !existing.Snapshot.IsTerminal);
                if (task is null)
                {
                    var item = await workshop.LookupAsync(id.ToString(CultureInfo.InvariantCulture), token);
                    token.ThrowIfCancellationRequested();
                    if (item.ConsumerAppId != workshop.Game.AppId || item.PublishedFileId != id)
                        throw new InvalidDataException(T("TheWorkshopItemDoesNotMatchThe"));
                    task = downloads.Start(item, plan.UserDataDirectory);
                }
                await task.IdentityReady.WaitAsync(token);
                downloadQueued.Report(task.Snapshot);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                failures.Add(new(mod.DisplayName, error.Message));
            }
        }
        return new(plan.Review.Downloads.Count, failures);
    }

    private ProfileImportReview Match(PortableModProfile document, IReadOnlyList<InstalledMod> installed, string userDirectory,
        IReadOnlyDictionary<int, PortableProfileCandidate>? choices = null)
    {
        var preview = PortableProfileService.Match(document, installed, choices);
        var candidates = downloads is not null && workshop.Game.SupportsNativeWorkshopDownloads &&
            Path.IsPathFullyQualified(userDirectory)
            ? preview.Entries.Where(entry => entry.Status == PortableProfileMatchStatus.Missing &&
                entry.Mod.Enabled && entry.Mod.WorkshopId is not null).Select(entry => entry.Mod).ToArray()
            : [];
        return new(document, preview, candidates);
    }
}
