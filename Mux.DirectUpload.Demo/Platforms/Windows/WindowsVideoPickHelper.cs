#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Mux.DirectUpload.Demo;

/// <summary>
/// Unpackaged MAUI Windows apps (<c>WindowsPackageType=None</c>) often need <see cref="FileOpenPicker"/>
/// initialized with the app window handle; <see cref="FilePicker.Default"/> / <see cref="MediaPicker"/> may no-op otherwise.
/// </summary>
internal static class WindowsVideoPickHelper
{
    public static async Task<FileResult?> PickVideoAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };

        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".m4v");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".wmv");
        picker.FileTypeFilter.Add(".webm");

        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView;
        if (window is null)
            return null;

        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return null;

        return new FileResult(file.Path);
    }
}
#endif
