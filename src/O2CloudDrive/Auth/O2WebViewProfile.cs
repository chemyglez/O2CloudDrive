namespace O2CloudDrive.Auth;

public static class O2WebViewProfile
{
    private static readonly object Gate = new();
    private static string _activeUserDataFolder = DefaultUserDataFolder;

    public static string UserDataFolder
    {
        get
        {
            lock (Gate)
            {
                return _activeUserDataFolder;
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            var folderToClear = _activeUserDataFolder;
            _activeUserDataFolder = NewUserDataFolder();
            ClearFolder(folderToClear);
            ClearStaleFolders();
        }
    }

    private static string BaseFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O2CloudDrive");

    private static string DefaultUserDataFolder => Path.Combine(BaseFolder, "WebView2");

    private static string NewUserDataFolder()
    {
        Directory.CreateDirectory(BaseFolder);
        return Path.Combine(BaseFolder, "WebView2-" + Guid.NewGuid().ToString("N"));
    }

    private static void ClearStaleFolders()
    {
        if (!Directory.Exists(BaseFolder))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(BaseFolder, "WebView2*"))
        {
            if (string.Equals(folder, _activeUserDataFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ClearFolder(folder);
        }
    }

    private static void ClearFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(150);
            }
        }

        try
        {
            var pendingDelete = Path.Combine(
                Path.GetDirectoryName(folder) ?? BaseFolder,
                Path.GetFileName(folder) + ".delete-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            Directory.Move(folder, pendingDelete);
            TryDeleteMovedFolder(pendingDelete);
        }
        catch
        {
            // A locked WebView2 process can keep files alive until it exits; the next cleanup pass retries.
        }
    }

    private static void TryDeleteMovedFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Best effort; the folder name marks it as stale and ClearStaleFolders will retry.
        }
    }
}
