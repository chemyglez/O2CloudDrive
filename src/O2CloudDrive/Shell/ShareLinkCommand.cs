using System.Runtime.InteropServices;
using System.Text;
using O2CloudDrive.Api;
using O2CloudDrive.Config;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Shell;

internal static class ShareLinkCommand
{
    public static int Run(AppConfig config, O2DriveAppServices services, string selectedPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                throw new InvalidOperationException("No se recibio ningun elemento para compartir.");
            }

            var session = services.AuthService.EnsureAuthenticated(allowInteractive: true);
            if (session is not { IsAuthenticated: true })
            {
                throw new InvalidOperationException("No hay una sesion valida de O2 Cloud.");
            }

            var virtualPath = ToVirtualPath(selectedPath);
            var store = new O2CloudFileStore(services.ApiClient);
            if (!store.TryGetByPath(virtualPath, out var node))
            {
                throw new FileNotFoundException("El elemento no aparece en el listado remoto de O2 Cloud.", selectedPath);
            }

            if (node.IsVirtualTrashRoot || node.IsTrashItem)
            {
                throw new InvalidOperationException("No se pueden compartir elementos de la papelera.");
            }

            var item = ToDto(node);
            var link = services.ApiClient.GetShareLink(item);
            using var form = new O2CloudDrive.Ui.ShareLinkForm(node.Name, link);
            form.ShowDialog();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
            "Compartir O2 Cloud",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string ToVirtualPath(string selectedPath)
    {
        var fullPath = selectedPath.Trim().Trim('"');
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("La ruta seleccionada no es valida.");
        }

        ValidateO2CloudDrive(root);
        var relative = fullPath[root.Length..].TrimStart('\\');
        return string.IsNullOrWhiteSpace(relative)
            ? "\\"
            : "\\" + relative;
    }

    private static void ValidateO2CloudDrive(string rootPath)
    {
        var volumeName = new StringBuilder(256);
        var fileSystemName = new StringBuilder(256);
        if (!GetVolumeInformation(
                rootPath,
                volumeName,
                volumeName.Capacity,
                out _,
                out _,
                out _,
                fileSystemName,
                fileSystemName.Capacity))
        {
            throw new InvalidOperationException("No se pudo comprobar la unidad seleccionada.");
        }

        if (!fileSystemName.ToString().Equals("O2Cloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Esta opcion solo funciona con archivos de la unidad O2 Cloud Drive.");
        }
    }

    private static O2CloudItemDto ToDto(CloudNode node)
    {
        return new O2CloudItemDto(
            node.Id,
            node.Name,
            node.Parent?.Id,
            node.IsDirectory,
            checked((long)node.Size),
            DateTimeOffset.FromFileTime(checked((long)node.LastWriteTime)),
            node.DirectUrl,
            node.MediaKind,
            node.Fingerprint,
            node.Node,
            node.DownloadToken);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
