using static Ck3ModManager.Core.AppText;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Ck3ModManager.Desktop;

public partial class MainWindow : Window
{
    private static FilePickerFileType ProfileFileType => new(T("ProfileFileJSON"))
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"]
    };

    private bool _profilePickerOpen;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _profileList = this.FindControl<ListBox>("ProfileModList")!;
        InitializeProfileDrag();
    }

    private async void RefreshGameSelector(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.RefreshGameCatalogAsync();
    }

    private async void SelectCk3SaveFile(object? sender, RoutedEventArgs e)
    {
        if (_profilePickerOpen || DataContext is not MainWindowViewModel vm || !vm.SupportsSaveBindings || !CanUseProfilePicker(vm)) return;

        _profilePickerOpen = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("SelectCK3Save"),
                FileTypeFilter = [new FilePickerFileType(T("CK3Save")) { Patterns = ["*.ck3"] }]
            });
            if (files.Count == 0 || !CanUseProfilePicker(vm)) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) vm.ReportProfileTransferError(T("ChooseALocalCK3SaveFile"));
            else await vm.SelectCk3SaveAsync(path);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(DataContext, vm)) vm.ReportProfileTransferError(T("CouldNotSelectASave", error.Message));
        }
        finally { _profilePickerOpen = false; }
    }

    private async void ImportProfileFile(object? sender, RoutedEventArgs e)
    {
        if (_profilePickerOpen || DataContext is not MainWindowViewModel vm || !CanUseProfilePicker(vm)) return;

        _profilePickerOpen = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("ImportProfileFile"),
                FileTypeFilter = [ProfileFileType]
            });
            if (files.Count == 0 || !CanUseProfilePicker(vm)) return;

            await using var source = await files[0].OpenReadAsync();
            if (CanUseProfilePicker(vm)) await vm.PreviewProfileImportAsync(source);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(DataContext, vm)) vm.ReportProfileTransferError(T("CouldNotOpenTheProfileFile", error.Message));
        }
        finally
        {
            _profilePickerOpen = false;
        }
    }

    private async void ExportProfileFile(object? sender, RoutedEventArgs e)
    {
        if (_profilePickerOpen || DataContext is not MainWindowViewModel vm || !CanUseProfilePicker(vm) || !vm.HasProfile) return;

        _profilePickerOpen = true;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("ExportProfileFile"),
                SuggestedFileName = T("ModProfileJson"),
                DefaultExtension = "json",
                FileTypeChoices = [ProfileFileType],
                ShowOverwritePrompt = true
            });
            if (file is null || !CanUseProfilePicker(vm) || !vm.HasProfile) return;

            using var content = new MemoryStream();
            await vm.ExportProfileAsync(content);
            if (vm.HasError || content.Length == 0) return;

            content.Position = 0;
            await using var destination = await file.OpenWriteAsync();
            if (destination.CanSeek)
            {
                destination.SetLength(0);
                destination.Position = 0;
            }
            await content.CopyToAsync(destination);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(DataContext, vm)) vm.ReportProfileTransferError(T("CouldNotSaveTheProfileFile", error.Message));
        }
        finally
        {
            _profilePickerOpen = false;
        }
    }

    private bool CanUseProfilePicker(MainWindowViewModel vm) =>
        ReferenceEquals(DataContext, vm) && vm.IsProfiles && !vm.IsBusy && !vm.HasProfileTransferPreview;
}
