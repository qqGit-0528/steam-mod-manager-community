using static Ck3ModManager.Core.AppText;
using System.Globalization;
using System.Windows.Input;
using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed record ProfileImportCandidateOption(PortableProfileCandidate? Candidate, string Label);

public sealed record ProfileImportRow(string OrderText, string DisplayName, string EnabledText,
    string AvailabilityText, string VersionComparison, IReadOnlyList<ProfileImportCandidateOption> Choices)
{
    public bool HasChoices => Choices.Count > 0;
    public ProfileImportCandidateOption? SelectedChoice { get; set; } = Choices.FirstOrDefault();
}

public sealed partial class MainWindowViewModel
{
    private ProfileImportService _profileImport = null!;
    private ProfileImportReview? _importReview;
    private IReadOnlyList<ProfileImportRow> _profileImportRows = [];

    public bool HasProfileImportPreview => _importReview is not null;
    public IReadOnlyList<ProfileImportRow> ProfileImportRows => _profileImportRows;
    public string ProfileImportSummary
    {
        get
        {
            if (_importReview?.Preview is not { } preview) return "";
            var missing = preview.Entries.Count(entry => entry.Status != PortableProfileMatchStatus.Installed);
            var version = preview.Profile.GameVersion is { Length: > 0 } value ? T("GameVersion2", value) : "";
            return T("ItemsPending", preview.Profile.Name, version, preview.Entries.Count, missing);
        }
    }
    public string ConfirmProfileImportText => _importReview is { Downloads.Count: > 0 } review
        ? T("ImportAndDownload", review.Downloads.Count)
        : T("ConfirmImport");
    public ICommand ConfirmProfileImportCommand { get; private set; } = null!;
    public ICommand CancelProfileImportCommand { get; private set; } = null!;
    public ICommand UseInstalledCopyCommand { get; private set; } = null!;

    public bool HasUnresolvedSelectedMod => SelectedProfileMod is { } row && PortableProfileService.IsUnresolved(row.Mod);

    private bool CanUseInstalledCopy() =>
        !IsBusy && !HasProfileTransferPreview && HasUnresolvedSelectedMod && SelectedInstalledMod is { } installed &&
        PortableProfileService.MatchesInstallation(SelectedProfileMod!.Mod, installed.Mod) &&
        !ProfileMods.Any(row => row.Mod.InstalledModId == installed.Mod.Id);

    private void InitializeProfileTransfer()
    {
        _profileImport = new(_workspace, _workshop, _downloads);
        ConfirmProfileImportCommand = new AsyncCommand(ConfirmProfileImportAsync, () => HasProfileImportPreview && !IsBusy);
        CancelProfileImportCommand = new RelayCommand(_ => SetImportPreview(null), () => HasProfileImportPreview && !IsBusy);
        UseInstalledCopyCommand = new RelayCommand(_ => UseSelectedInstalledCopy(), CanUseInstalledCopy);
    }

    public void ReportProfileTransferError(string message) => ErrorMessage = message;

    public Task ExportProfileAsync(Stream destination) => RunOperationAsync(T("ExportProfile"), async token =>
    {
        if (SelectedProfile is null) throw new InvalidOperationException(T("SelectAProfileFirst"));
        var profile = new ModProfile(SelectedProfile.Id, ProfileName.Trim(), Clean(ProfileGameVersion),
            ProfileMods.Select(row => row.Mod).ToArray(), DateTimeOffset.UtcNow);
        await PortableProfileService.WriteAsync(destination, Game, profile, token);
        Status = T("ProfileExported");
    });

    public Task PreviewProfileImportAsync(Stream source) => RunOperationAsync(T("ReadProfile"), async token =>
    {
        SetImportPreview(null);
        var section = Section;
        var review = await _profileImport.PreviewAsync(source, CurrentInstalledMods(), _userDirectory, token);
        token.ThrowIfCancellationRequested();
        if (_disposed || Section != section) return;
        SetImportPreview(review);
        Status = T("ReviewEnabledModsLoadOrderAndVersion");
    });

    public Task ConfirmProfileImportAsync() => RunOperationAsync(T("ImportProfile"), async token =>
    {
        if (_importReview is not { } shown) return;
        var choices = new Dictionary<int, PortableProfileCandidate>();
        for (var index = 0; index < ProfileImportRows.Count; index++)
            if (ProfileImportRows[index].SelectedChoice?.Candidate is { } candidate) choices.Add(index, candidate);
        var plan = await _profileImport.RecheckAsync(CurrentSettings(), shown, token, choices);
        token.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(_importReview, shown)) return;
        if (plan.RequiresConfirmation)
        {
            SetImportPreview(plan.Review);
            Status = T("LocalModsChangedReviewVersionsAndMissing");
            return;
        }

        var saved = new InlineProgress<ModProfile>(profile =>
        {
            if (_disposed) return;
            Profiles.Add(profile);
            SelectedProfile = profile;
            Changed(nameof(ProfileCountText));
            SetImportPreview(null);
        });
        var queued = new InlineProgress<WorkshopDownloadSnapshot>(snapshot => { if (!_disposed) ApplyDownload(snapshot); });
        var result = await _profileImport.CommitAsync(plan, saved, queued, token);
        if (_disposed) return;
        Status = result.DownloadCount == 0 ? T("ProfileImported") : T("ProfileImportedFollowDownloadsInTheApp");
        if (result.Failures.Count > 0)
            ErrorMessage = T("SomeModsCouldNotBeQueued", string.Join(T("DetailSeparator"), result.Failures.Take(3).Select(failure => $"{failure.ModName}：{failure.Message}")));
    });

    private InstalledMod[] CurrentInstalledMods() => InstalledMods.Select(row => row.Mod).ToArray();

    private void SetImportPreview(ProfileImportReview? review)
    {
        _importReview = review;
        var downloads = review?.Downloads.Select(mod => mod.WorkshopId).ToHashSet();
        _profileImportRows = review?.Preview.Entries.Select((entry, index) =>
        {
            var state = entry.Status switch
            {
                PortableProfileMatchStatus.Installed => T("InstalledLocally"),
                PortableProfileMatchStatus.VersionMismatch => T("VersionDiffersChooseACopy"),
                PortableProfileMatchStatus.Ambiguous => T("MultipleCopiesChooseOne"),
                _ when !entry.Mod.Enabled => T("MissingDisabled"),
                _ when downloads?.Contains(entry.Mod.WorkshopId) == true => T("MissingDownloadAfterConfirmation"),
                _ => T("MissingInstallManually")
            };
            var versions = entry.Candidates.Select(candidate => DisplayVersion(candidate.ModVersion)).Distinct().ToArray();
            var local = versions.Length == 0 ? T("NoMatchingInstallation") : string.Join(" / ", versions.Take(4));
            if (versions.Length > 4) local += T("Total", versions.Length);
            var comparison = T("RequestedInstalled", DisplayVersion(entry.ExpectedVersion), local);
            var choices = entry.Status is PortableProfileMatchStatus.Ambiguous or PortableProfileMatchStatus.VersionMismatch ||
                entry.Candidates.Count > 1
                ? new[] { new ProfileImportCandidateOption(null,
                        entry.Status == PortableProfileMatchStatus.Installed ? T("UseMatchingVersion") : T("KeepPending")) }
                    .Concat(entry.Candidates.Select(candidate => new ProfileImportCandidateOption(candidate,
                        T("Use", DisplayVersion(candidate.ModVersion), InstalledModRow.ManagementLabel(candidate.Management), candidate.Path)))).ToArray()
                : [];
            return new ProfileImportRow((index + 1).ToString(CultureInfo.InvariantCulture), entry.Mod.DisplayName,
                entry.Mod.Enabled ? T("Enabled2") : T("Disabled2"), state, comparison, choices);
        }).ToArray() ?? [];
        Changed(nameof(HasProfileImportPreview));
        Changed(nameof(HasProfileTransferPreview));
        Changed(nameof(ProfileImportSummary));
        Changed(nameof(ProfileImportRows));
        Changed(nameof(ConfirmProfileImportText));
        RefreshProfileTransferCommands();
    }

    private static string DisplayVersion(string? version) => string.IsNullOrWhiteSpace(version) ? T("Undeclared") : version;

    private void UseSelectedInstalledCopy()
    {
        if (!CanUseInstalledCopy()) return;
        var row = SelectedProfileMod!;
        var index = ProfileMods.IndexOf(row);
        if (index < 0) return;
        var resolved = PortableProfileService.UseInstalledCopy(row.Mod, SelectedInstalledMod!.Mod);
        ReplaceImportedProfileRow(index, resolved);
        MarkProfileChanged();
    }

    private void RefreshProfileTransferCommands()
    {
        ((AsyncCommand)ConfirmProfileImportCommand).Refresh();
        ((AsyncCommand)CheckFileConflictsCommand).Refresh();
        RefreshDependencyCommands();
        RefreshPortableTransferCommands();
        ((RelayCommand)CancelProfileImportCommand).Refresh();
        ((RelayCommand)UseInstalledCopyCommand).Refresh();
        Changed(nameof(HasUnresolvedSelectedMod));
    }

    private void ResolveImportedProfileRows(IReadOnlyList<InstalledMod> installed)
    {
        for (var index = 0; index < ProfileMods.Count; index++)
        {
            var row = ProfileMods[index];
            var resolved = PortableProfileService.ResolveMissing(row.Mod, installed);
            if (ReferenceEquals(resolved, row.Mod) ||
                ProfileMods.Any(other => other.Mod.InstalledModId == resolved.InstalledModId)) continue;
            ReplaceImportedProfileRow(index, resolved);
        }
    }

    private void ReplaceImportedProfileRow(int index, ProfileMod resolved)
    {
        var row = ProfileMods[index];
        var replacement = new ProfileModRow(resolved, row.Order, _gameVersion, MarkProfileChanged);
        ProfileMods[index] = replacement;
        if (ReferenceEquals(SelectedProfileMod, row)) SelectedProfileMod = replacement;
    }
}
