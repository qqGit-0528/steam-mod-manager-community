using static Ck3ModManager.Core.AppText;
using System.Windows.Input;
using Ck3ModManager.Application;
using Ck3ModManager.Core;

namespace Ck3ModManager.Desktop;

public sealed partial class MainWindowViewModel
{
    private bool _gameProfileApplied;
    public ICommand ApplyGameProfileCommand { get; private set; } = null!;
    public ICommand ReadGameProfileCommand { get; private set; } = null!;
    public bool SupportsGameActivation => _workspace.HasGameModActivation;
    public bool GameProfileApplied
    {
        get => _gameProfileApplied;
        private set { if (Set(ref _gameProfileApplied, value)) { Changed(nameof(GameActivationState)); Changed(nameof(LaunchGameTooltip)); } }
    }
    public string GameActivationState => !SupportsGameActivation ? T("Unsupported") : GameProfileApplied ? T("Applied") : T("Pending");
    public string ActiveProfileLabel => HasProfile ? T("ActiveProfile", ProfileName) : T("SelectOrCreateAProfile");

    private void InitializeGameActivation()
    {
        ApplyGameProfileCommand = new AsyncCommand(ApplyGameProfileAsync, () => !IsBusy && HasProfile && SupportsGameActivation);
        ReadGameProfileCommand = new AsyncCommand(ReadGameProfileAsync, () => !IsBusy && SupportsGameActivation);
    }

    private InstalledModRow CreateInstalledRow(InstalledMod mod) =>
        new(mod, _gameVersion, ToggleInstalledMod, _workspace.LocalMods.FindDuplicateCopies(mod));
    private ProfileModRow CreateProfileRow(ProfileMod mod) => new(mod, ProfileMods.Count + 1, _gameVersion, MarkProfileChanged);
    private void SyncLocalModToggles()
    {
        foreach (var row in InstalledMods)
            row.SyncEnabled(ProfileMods.Any(profile => profile.Mod.InstalledModId == row.Mod.Id && profile.Enabled));
        Changed(nameof(ActiveProfileLabel));
    }

    private void ToggleInstalledMod(InstalledModRow row, bool enabled)
    {
        if (IsBusy) { SyncLocalModToggles(); return; }
        if (!HasProfile)
        {
            var profile = new ModProfile(Guid.NewGuid(), T("ActiveProfile2"), _gameVersion, [], DateTimeOffset.UtcNow);
            Profiles.Add(profile); SelectedProfile = profile;
        }
        var entry = ProfileMods.FirstOrDefault(mod => mod.Mod.InstalledModId == row.Mod.Id);
        if (entry is null && enabled)
        {
            if (row.Mod.WorkshopId is { } id && ProfileMods.Any(mod => mod.Mod.WorkshopId == id))
            { ErrorMessage = T("ThisProfileAlreadyContainsAnotherCopyOf"); SyncLocalModToggles(); return; }
            if (!row.Mod.IsAvailable) { SyncLocalModToggles(); return; }
            ProfileMods.Add(CreateProfileRow(new ProfileMod(row.Mod.Id, row.Name, row.Mod.ContentPath, row.Mod.WorkshopId,
                row.Mod.SupportedGameVersion, row.Mod.ModVersion)));
            MarkProfileChanged();
        }
        else if (entry is not null) entry.Enabled = enabled;
        SyncLocalModToggles();
    }

    private async Task InitializeGameProfileAsync(CancellationToken token)
    {
        if (!SupportsGameActivation) return;
        try
        {
            var profile = await _workspace.ReadGameProfileAsync(CurrentSettings(), token);
            if (profile is null) return;
            if (HasProfile)
            {
                GameProfileApplied = WorkspaceService.MatchesGameProfile(BuildGameProfile(), profile);
                return;
            }
            Profiles.Add(profile); SelectedProfile = profile; GameProfileApplied = true;
            Changed(nameof(ProfileCountText));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { Status = T("LocalEnvironmentLoadedTheLauncherProfileCould", error.Message); }
    }

    public Task ReadGameProfileAsync() => RunOperationAsync(T("ReadGameProfile"), async token =>
    {
        var profile = await _workspace.ReadGameProfileAsync(CurrentSettings(), token)
            ?? throw new InvalidOperationException(T("NoLauncherPlaysetFound"));
        // 导入为另一份草稿，保留用户正在编辑的配置。
        Profiles.Add(profile); SelectedProfile = profile; GameProfileApplied = true;
        Changed(nameof(ProfileCountText));
        Status = T("CurrentGameProfileLoaded");
    });

    public Task ApplyGameProfileAsync() => RunOperationAsync(T("ApplyGameProfile"), async token =>
    {
        var profile = BuildGameProfile();
        GameProfileApplied = false;
        var preparation = await _workspace.ApplyAndVerifyGameProfileAsync(CurrentSettings(), profile, token);
        PresentGameProfilePreparation(profile, preparation);
    });

    public async Task LaunchGameWithProfileAsync()
    {
        if (IsBusy || _disposed) return;
        if (!SupportsGameActivation || !HasProfile)
        {
            LaunchGame();
            return;
        }

        await RunOperationAsync(T("PrepareGameLaunch"), async token =>
        {
            var profile = BuildGameProfile();
            var pending = !GameProfileApplied;
            GameProfileApplied = false;
            var preparation = await _workspace.PrepareGameLaunchAsync(CurrentSettings(), profile, pending, token);
            PresentGameProfilePreparation(profile, preparation);
            if (preparation.SaveError is not null || _disposed) return;
            token.ThrowIfCancellationRequested();
            LaunchGame();
        });
    }

    private ModProfile BuildGameProfile()
    {
        if (SelectedProfile is null) throw new InvalidOperationException(T("SelectAProfileFirst"));
        return new(SelectedProfile.Id, ProfileName.Trim(), Clean(ProfileGameVersion),
            ProfileMods.Select(row => row.Mod).ToArray(), DateTimeOffset.UtcNow);
    }

    private void PresentGameProfilePreparation(ModProfile profile, GameProfilePreparation preparation)
    {
        GameProfileApplied = true;
        if (preparation.Applied is not { } applied) return;
        if (preparation.SaveError is { } error)
        {
            ErrorMessage = T("TheLauncherWasUpdatedButTheApp", error.Message);
        }
        else
        {
            _drafts.Remove(profile.Id);
            IsProfileDirty = false;
            var index = Profiles.ToList().FindIndex(item => item.Id == profile.Id);
            if (index >= 0) Profiles[index] = profile;
            SelectedProfile = profile;
            GameProfileApplied = true;
            IsProfileDirty = false;
        }
        Status = T("EnabledModsTheChangeTakesEffectThe", applied.EnabledCount);
    }
}
