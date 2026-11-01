using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentCode.Services.Appearance;

/// <summary>一个已安装的系统字体族。</summary>
/// <param name="FamilyName">稳定（英文 / 不变）族名：持久化与 <c>FontFamily</c> 构造都使用它。</param>
/// <param name="DisplayName">按界面语言本地化的显示名（无本地化名时与 <paramref name="FamilyName"/> 相同）。</param>
/// <param name="IsMonospaced">族内常规字形是否为等宽字体。</param>
/// <param name="IsSymbol">是否为符号字体（Wingdings / Segoe MDL2 Assets 等，不适合做正文）。</param>
/// <param name="SupportsChinese">是否含常用汉字字形（以“中”“文”检测）。</param>
public sealed record SystemFontFamily(
    string FamilyName,
    string DisplayName,
    bool IsMonospaced,
    bool IsSymbol,
    bool SupportsChinese);

/// <summary>
/// 枚举已安装的系统字体族：直接调用 Windows 自带 DirectWrite（<c>dwrite.dll</c>）系统字体集合，
/// 不引入任何 NuGet 依赖、不使用 GDI+。可在任意线程调用（共享 DirectWrite 工厂是线程安全的）。
/// </summary>
/// <remarks>
/// 失败（极少见：DirectWrite 不可用 / COM 异常）时返回空列表，调用方应仍能显示“默认”项；
/// 本类不缓存结果（字体可能被安装/卸载），调用方按需缓存。
/// </remarks>
public static class SystemFontCatalog
{
    private const int DWriteFactoryTypeShared = 0;

    /// <summary>枚举系统字体族，按显示名排序，去除竖排（<c>@</c> 前缀）与重复族名。</summary>
    /// <param name="culture">显示名使用的语言；null 使用 <see cref="CultureInfo.CurrentUICulture"/>。</param>
    public static IReadOnlyList<SystemFontFamily> GetInstalledFamilies(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var result = new List<SystemFontFamily>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        try
        {
            Guid iid = typeof(IDWriteFactory).GUID;
            Marshal.ThrowExceptionForHR(DWriteCreateFactory(DWriteFactoryTypeShared, ref iid, out object factoryObject));
            var factory = (IDWriteFactory)factoryObject;
            Marshal.ThrowExceptionForHR(factory.GetSystemFontCollection(out IDWriteFontCollection collection, false));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint count = collection.GetFontFamilyCount();
            for (uint i = 0; i < count; i++)
            {
                SystemFontFamily? family = TryReadFamily(collection, i, culture);
                if (family is not null && seen.Add(family.FamilyName))
                {
                    result.Add(family);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return Array.Empty<SystemFontFamily>();
        }

        StringComparer comparer = StringComparer.Create(culture, ignoreCase: true);
        result.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        return result;
    }

    private static SystemFontFamily? TryReadFamily(IDWriteFontCollection collection, uint index, CultureInfo culture)
    {
        try
        {
            if (collection.GetFontFamily(index, out IDWriteFontFamily family) < 0 || family is null)
            {
                return null;
            }

            if (family.GetFamilyNames(out IDWriteLocalizedStrings names) < 0 || names is null)
            {
                return null;
            }

            string? invariant = ReadName(names, "en-us") ?? ReadName(names, 0);
            if (string.IsNullOrWhiteSpace(invariant) || invariant.StartsWith('@') || !AppFontDefaults.IsValidFamilyName(invariant))
            {
                return null;
            }

            string display = ReadName(names, culture.Name)
                ?? (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ReadName(names, "zh-cn") : null)
                ?? invariant;

            bool monospaced = false;
            bool symbol = false;
            bool chinese = false;
            if (family.GetFont(0, out IDWriteFont1 font) >= 0 && font is not null)
            {
                symbol = font.IsSymbolFont() != 0;
                monospaced = SafeIsMonospaced(font);
                chinese = HasCharacter(font, '中') && HasCharacter(font, '文');
            }

            return new SystemFontFamily(invariant, display, monospaced, symbol, chinese);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static bool SafeIsMonospaced(IDWriteFont1 font)
    {
        try
        {
            return font.IsMonospacedFont() != 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static bool HasCharacter(IDWriteFont1 font, char c)
        => font.HasCharacter(c, out int exists) >= 0 && exists != 0;

    private static string? ReadName(IDWriteLocalizedStrings names, string locale)
    {
        if (string.IsNullOrEmpty(locale) ||
            names.FindLocaleName(locale, out uint index, out int exists) < 0 ||
            exists == 0)
        {
            return null;
        }

        return ReadName(names, index);
    }

    private static string? ReadName(IDWriteLocalizedStrings names, uint index)
    {
        if (index >= names.GetCount() || names.GetStringLength(index, out uint length) < 0)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)length + 1));
        if (names.GetString(index, buffer, length + 1) < 0)
        {
            return null;
        }

        string value = buffer.ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(
        int factoryType,
        ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    // ---- 最小 DirectWrite COM 投影（只声明到实际调用的方法为止；vtable 顺序必须与 dwrite.h 一致） ----

    [ComImport]
    [Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig]
        int GetSystemFontCollection(
            [MarshalAs(UnmanagedType.Interface)] out IDWriteFontCollection fontCollection,
            [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport]
    [Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig]
        uint GetFontFamilyCount();

        [PreserveSig]
        int GetFontFamily(uint index, [MarshalAs(UnmanagedType.Interface)] out IDWriteFontFamily fontFamily);
    }

    /// <summary>IDWriteFontFamily（继承 IDWriteFontList：GetFontCollection / GetFontCount / GetFont）。</summary>
    [ComImport]
    [Guid("da20d8ef-812a-4c43-9802-62ec4abd7add")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        // IDWriteFontList
        [PreserveSig]
        int GetFontCollection(out IntPtr fontCollection);

        [PreserveSig]
        uint GetFontCount();

        [PreserveSig]
        int GetFont(uint index, [MarshalAs(UnmanagedType.Interface)] out IDWriteFont1 font);

        // IDWriteFontFamily
        [PreserveSig]
        int GetFamilyNames([MarshalAs(UnmanagedType.Interface)] out IDWriteLocalizedStrings names);
    }

    /// <summary>
    /// IDWriteFont1（Windows 8+）。GetFont 的出参按 IDWriteFont 返回，RCW 转换时经 QueryInterface 取得 IDWriteFont1。
    /// 前 11 个方法为 IDWriteFont，随后为 IDWriteFont1 新增方法。
    /// </summary>
    [ComImport]
    [Guid("acd16696-8c14-4f5d-877e-fe3fc1d32738")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFont1
    {
        // IDWriteFont
        [PreserveSig] int GetFontFamily(out IntPtr fontFamily);
        [PreserveSig] int GetWeight();
        [PreserveSig] int GetStretch();
        [PreserveSig] int GetStyle();
        [PreserveSig] int IsSymbolFont();
        [PreserveSig] int GetFaceNames(out IntPtr names);
        [PreserveSig] int GetInformationalStrings(int informationalStringId, out IntPtr informationalStrings, out int exists);
        [PreserveSig] int GetSimulations();
        [PreserveSig] void GetMetrics(IntPtr fontMetrics);
        [PreserveSig] int HasCharacter(uint unicodeValue, out int exists);
        [PreserveSig] int CreateFontFace(out IntPtr fontFace);

        // IDWriteFont1
        [PreserveSig] void GetMetrics1(IntPtr fontMetrics);
        [PreserveSig] void GetPanose(IntPtr panose);
        [PreserveSig] int GetUnicodeRanges(uint maxRangeCount, IntPtr unicodeRanges, out uint actualRangeCount);
        [PreserveSig] int IsMonospacedFont();
    }

    [ComImport]
    [Guid("08256209-099a-4b34-b86d-c22b110e7771")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteLocalizedStrings
    {
        [PreserveSig]
        uint GetCount();

        [PreserveSig]
        int FindLocaleName([MarshalAs(UnmanagedType.LPWStr)] string localeName, out uint index, out int exists);

        [PreserveSig]
        int GetLocaleNameLength(uint index, out uint length);

        [PreserveSig]
        int GetLocaleName(uint index, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder localeName, uint size);

        [PreserveSig]
        int GetStringLength(uint index, out uint length);

        [PreserveSig]
        int GetString(uint index, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder stringBuffer, uint size);
    }
}
