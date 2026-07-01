namespace O2CloudDrive.Ui;

internal static class AppIcon
{
    private const string IconPath = "Assets\\O2CloudDrive.ico";

    public static Icon Load()
    {
        try
        {
            var assetPath = Path.Combine(AppContext.BaseDirectory, IconPath);
            if (File.Exists(assetPath))
            {
                return new Icon(assetPath);
            }
        }
        catch
        {
        }

        try
        {
            using var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted is not null)
            {
                return (Icon)extracted.Clone();
            }
        }
        catch
        {
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
