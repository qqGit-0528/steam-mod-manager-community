using static Ck3ModManager.Core.AppText;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Ck3ModManager.Desktop;

public partial class MainWindow
{
    private static FilePickerFileType ModZip => new("CK3 Mod（ZIP）") { Patterns = ["*.zip"] };
    private static FilePickerFileType PlaysetPackage => new(T("CK3PlaysetPack")) { Patterns = ["*.ck3pack"] };

    private async void PickLocalModZip(object? sender, RoutedEventArgs e) =>
        await PickPortablePathAsync(vm => vm.CanPreviewLocalModImport, async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("SelectCK3ModZIP"),
                FileTypeFilter = [ModZip]
            });
            return files.FirstOrDefault();
        }, (vm, path) => vm.PreviewLocalModImportAsync(path));

    private async void PickLocalModFolder(object? sender, RoutedEventArgs e) =>
        await PickPortablePathAsync(vm => vm.CanPreviewLocalModImport, async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("SelectCK3ModFolder") });
            return folders.FirstOrDefault();
        }, (vm, path) => vm.PreviewLocalModImportAsync(path));

    private async void PickPlaysetPackageImport(object? sender, RoutedEventArgs e) =>
        await PickPortablePathAsync(vm => vm.CanPreviewPlaysetPackage, async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("SelectCK3PlaysetPack"),
                FileTypeFilter = [PlaysetPackage]
            });
            return files.FirstOrDefault();
        }, (vm, path) => vm.PreviewPlaysetPackageAsync(path));

    private async void PickPlaysetPackageExport(object? sender, RoutedEventArgs e) =>
        await PickPortablePathAsync(vm => vm.CanExportPlaysetPackage, async () =>
            await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("ExportCK3PackWithEnabledModFiles"),
                SuggestedFileName = T("CK3PlaysetCk3pack"),
                DefaultExtension = "ck3pack",
                FileTypeChoices = [PlaysetPackage],
                ShowOverwritePrompt = true
            }), (vm, path) => vm.ExportPlaysetPackageAsync(path));

    private async Task PickPortablePathAsync(Func<MainWindowViewModel, bool> canUse, Func<Task<IStorageItem?>> pick,
        Func<MainWindowViewModel, string, Task> use)
    {
        if (_profilePickerOpen || DataContext is not MainWindowViewModel vm || !canUse(vm)) return;
        var profile = vm.SelectedProfile;
        _profilePickerOpen = true;
        try
        {
            var item = await pick();
            if (item is null || !ReferenceEquals(DataContext, vm) || !canUse(vm) ||
                !ReferenceEquals(profile, vm.SelectedProfile)) return;
            if (item.TryGetLocalPath() is { } path) await use(vm, path);
            else vm.ReportProfileTransferError(T("ChooseALocalFileOrFolder"));
        }
        catch (Exception error)
        {
            if (ReferenceEquals(DataContext, vm)) vm.ReportProfileTransferError(T("CouldNotSelectTheFileOrFolder", error.Message));
        }
        finally { _profilePickerOpen = false; }
    }
}
