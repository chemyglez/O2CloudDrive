using Microsoft.Win32;

namespace O2CloudDrive.Shell;

internal static class ShellContextMenuService
{
    private const string ShareVerbKey = @"Software\Classes\*\shell\O2CloudDriveShare";
    private const string DirectoryShareVerbKey = @"Software\Classes\Directory\shell\O2CloudDriveShare";

    public static void Register(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        try
        {
            RegisterVerb(ShareVerbKey, executablePath);
            RegisterVerb(DirectoryShareVerbKey, executablePath);
        }
        catch
        {
            // The app still works without the Explorer verb; registration is best effort.
        }
    }

    private static void RegisterVerb(string keyPath, string executablePath)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(keyPath);
        if (verb is null)
        {
            return;
        }

        var menuText = "Compartir O2 Cloud";
        verb.SetValue(string.Empty, menuText);
        verb.SetValue("MUIVerb", menuText);
        verb.SetValue("Icon", ShellIconPath(executablePath));
        verb.SetValue("Position", "Top");

        using var command = verb.CreateSubKey("command");
        command?.SetValue(string.Empty, $"\"{executablePath}\" --share \"%1\"");
    }

    private static string ShellIconPath(string executablePath)
    {
        var assetIcon = Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "Assets", "O2CloudDrive.ico");
        return File.Exists(assetIcon)
            ? $"\"{assetIcon}\""
            : $"\"{executablePath}\",0";
    }
}
