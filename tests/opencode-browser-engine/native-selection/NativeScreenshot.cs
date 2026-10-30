using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BrowserSelectionFixture;

internal static class NativeScreenshot
{
    internal static async Task SaveUiOnlyAsync(Window window,string path)
    {
        var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync((FrameworkElement)window.Content);
        var buffer=await bitmap.GetPixelsAsync();using var reader=DataReader.FromBuffer(buffer);var bytes=new byte[buffer.Length];reader.ReadBytes(bytes);
        using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,bytes);await encoder.FlushAsync();
        stream.Seek(0);using var pngReader=new DataReader(stream);await pngReader.LoadAsync((uint)stream.Size);var png=new byte[(int)stream.Size];pngReader.ReadBytes(png);File.WriteAllBytes(path,png);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { internal uint Size; internal int Width,Height; internal ushort Planes,BitCount; internal uint Compression,SizeImage; internal int X,Y; internal uint Used,Important; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd,nint dc,uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc,int width,int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc,nint bitmap,uint start,uint lines,byte[] pixels,ref Header info,uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint value);
    internal static async Task SaveAsync(Window window,string path)
    {
        window.Activate(); await Task.Delay(150);
        var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(window); if(!GetWindowRect(hwnd,out var rect))throw new InvalidOperationException("Owned fixture window bounds unavailable");
        var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
        var dc=GetDC(hwnd);var memory=CreateCompatibleDC(dc);var bitmap=CreateCompatibleBitmap(dc,width,height);var previous=SelectObject(memory,bitmap);
        try
        {
            if(!PrintWindow(hwnd,memory,2))throw new InvalidOperationException("Owned fixture screenshot failed");
            var pixels=new byte[checked(width*height*4)];var header=new Header{Size=(uint)Marshal.SizeOf<Header>(),Width=width,Height=-height,Planes=1,BitCount=32};
            if(GetDIBits(memory,bitmap,0,(uint)height,pixels,ref header,0)==0)throw new InvalidOperationException("Owned fixture screenshot pixels unavailable");
            var visible=0;for(var y=40;y<height;y++)for(var x=0;x<width;x++){var i=(y*width+x)*4;if((pixels[i]|pixels[i+1]|pixels[i+2])!=0)visible++;}
            if(visible<100)throw new InvalidOperationException("Desktop capture excludes accelerated client content; see separate native UI-only and real CDP browser captures.");
            using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,(uint)width,(uint)height,96,96,pixels);await encoder.FlushAsync();
            stream.Seek(0);using var reader=new DataReader(stream);await reader.LoadAsync((uint)stream.Size);var bytes=new byte[checked((int)stream.Size)];reader.ReadBytes(bytes);File.WriteAllBytes(path,bytes);
        }
        finally {SelectObject(memory,previous);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(hwnd,dc);}
    }
}
