using System;
using System.IO;

namespace AIFrontend.Pages.OpenCode;

// Quick-access UI state, separate from backend/account settings (which have no editor field).
internal sealed class WslQuickAccessEditorPreference(string path)
{
    public string? Load()
    {
        try
        {
            var name = File.ReadAllText(path);
            return IsEditor(name) ? name : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(string name)
    {
        if (!IsEditor(name)) throw new ArgumentException("未知主机编辑器", nameof(name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
    }

    private static bool IsEditor(string name) => name is "VS Code" or "VSCodium" or "Emacs" or "Zed";
}
