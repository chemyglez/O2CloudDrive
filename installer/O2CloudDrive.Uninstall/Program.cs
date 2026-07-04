using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace O2CloudDrive.Uninstall;

internal static class Program
{
    private const string ProductName = "O2 Cloud Drive";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\O2CloudDrive";

    internal static readonly string StartMenuDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        "Programs",
        "O2 Cloud Drive");

    [STAThread]
    private static int Main(string[] args)
    {
        var options = UninstallOptions.Parse(args);

        if (options.SelfTest)
        {
            return File.Exists(Application.ExecutablePath) ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UninstallForm(options));
        return UninstallForm.ExitCode;
    }

    internal static void StageAndRunElevated(UninstallOptions options)
    {
        var stageDir = Path.Combine(Path.GetTempPath(), "O2CloudDriveUninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDir);

        var stagedExe = Path.Combine(stageDir, "O2CloudDrive.Uninstall.exe");
        File.Copy(Application.ExecutablePath, stagedExe, overwrite: true);

        var args = new List<string>
        {
            "--perform",
            "--install-dir",
            options.InstallDir,
            "--wait-pid",
            Process.GetCurrentProcess().Id.ToString()
        };

        if (options.Quiet)
        {
            args.Add("--quiet");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = stagedExe,
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            WorkingDirectory = stageDir,
            UseShellExecute = true,
            Verb = IsAdministrator() ? "open" : "runas"
        };

        Process.Start(startInfo);
    }

    internal static async Task UninstallAsync(UninstallOptions options, IProgress<UninstallProgress> progress, CancellationToken cancellationToken)
    {
        var installDir = NormalizeInstallDir(options.InstallDir);
        ValidateInstallDir(installDir);

        if (options.WaitPid is int waitPid)
        {
            progress.Report(new UninstallProgress(5, "Preparando desinstalacion..."));
            await WaitForProcessExitAsync(waitPid, cancellationToken);
        }

        progress.Report(new UninstallProgress(18, "Cerrando O2 Cloud Drive si esta abierto..."));
        CloseRunningApp();

        progress.Report(new UninstallProgress(35, "Eliminando accesos directos..."));
        TryDeleteDirectory(StartMenuDir);

        progress.Report(new UninstallProgress(50, "Quitando integracion del Explorador..."));
        TryDeleteCurrentUserKey(@"Software\Classes\*\shell\O2CloudDriveShare");
        TryDeleteCurrentUserKey(@"Software\Classes\Directory\shell\O2CloudDriveShare");

        progress.Report(new UninstallProgress(62, "Quitando registro de desinstalacion..."));
        TryDeleteLocalMachineKey(UninstallKey);

        progress.Report(new UninstallProgress(78, "Eliminando archivos instalados..."));
        TryDeleteDirectoryWithRetry(installDir, cancellationToken);

        progress.Report(new UninstallProgress(100, "Desinstalacion completada."));
        ScheduleSelfDeleteIfStaged();
    }

    internal static string NormalizeInstallDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ValidateInstallDir(string installDir)
    {
        var root = Path.GetPathRoot(installDir)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(installDir, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La carpeta de instalacion no es valida.");
        }

        var appExe = Path.Combine(installDir, "O2CloudDrive.exe");
        var uninstallerExe = Path.Combine(installDir, "O2CloudDrive.Uninstall.exe");
        if (!File.Exists(appExe) && !File.Exists(uninstallerExe))
        {
            throw new InvalidOperationException("No se encontro una instalacion valida de O2 Cloud Drive en esa carpeta.");
        }
    }

    private static async Task WaitForProcessExitAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            // If the launcher already exited, uninstall can continue.
        }
    }

    private static void CloseRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("O2CloudDrive"))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have exited between enumeration and close.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"No se pudo eliminar {directory}. {ex.Message}", ex);
        }
    }

    private static void TryDeleteDirectoryWithRetry(string directory, CancellationToken cancellationToken)
    {
        const int attempts = 8;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == attempts)
                {
                    break;
                }

                Thread.Sleep(750);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new InvalidOperationException($"No se pudo eliminar {directory}. {lastError?.Message}", lastError);
    }

    private static void TryDeleteCurrentUserKey(string subKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // Explorer integration is best-effort; uninstall should still remove the app.
        }
    }

    private static void TryDeleteLocalMachineKey(string subKey)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // If the key is already gone, the important part is removing files.
        }
    }

    private static void ScheduleSelfDeleteIfStaged()
    {
        try
        {
            var exe = Application.ExecutablePath;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrWhiteSpace(dir) || !dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            MoveFileEx(exe, null, MoveFileFlags.DelayUntilReboot);
            MoveFileEx(dir, null, MoveFileFlags.DelayUntilReboot);
        }
        catch
        {
            // Temporary staged uninstaller can remain until Windows cleans temp files.
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, MoveFileFlags flags);

    [Flags]
    private enum MoveFileFlags
    {
        DelayUntilReboot = 0x4
    }
}

internal readonly record struct UninstallProgress(int Percent, string Message);

internal sealed record UninstallOptions(string InstallDir, bool Perform, bool Quiet, bool SelfTest, int? WaitPid)
{
    public static UninstallOptions Parse(string[] args)
    {
        var installDir = AppContext.BaseDirectory;
        var perform = false;
        var quiet = false;
        var selfTest = false;
        int? waitPid = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--install-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                installDir = args[++i];
            }
            else if (string.Equals(arg, "--perform", StringComparison.OrdinalIgnoreCase))
            {
                perform = true;
            }
            else if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase))
            {
                quiet = true;
            }
            else if (string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                selfTest = true;
            }
            else if (string.Equals(arg, "--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var parsedPid))
            {
                waitPid = parsedPid;
            }
        }

        return new UninstallOptions(Program.NormalizeInstallDir(installDir), perform, quiet, selfTest, waitPid);
    }
}
