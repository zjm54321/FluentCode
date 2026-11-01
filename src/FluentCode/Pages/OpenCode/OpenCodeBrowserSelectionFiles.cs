using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

internal static class OpenCodeBrowserSelectionFiles
{
    internal static async Task CopyAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        stream.Seek(0); var package = new DataPackage(); package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(package); Clipboard.Flush();
    }
    internal static async Task<bool> SaveAsync(byte[] bytes, XamlRoot root)
    {
        if ((Application.Current as App)?.OpenCodeOwnerWindow is not { } owner) throw new InvalidOperationException("无法取得保存窗口。");
        var picker = new FileSavePicker { SuggestedFileName = "浏览器截图" }; picker.FileTypeChoices.Add("PNG 图片", new[] { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;
        await FileIO.WriteBytesAsync(file, bytes);
        return true;
    }
}
