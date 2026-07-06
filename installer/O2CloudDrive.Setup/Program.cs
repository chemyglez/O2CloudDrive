using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace O2CloudDrive.Setup;

internal static class Program
{
    private const string ProductName = "O2 Cloud Drive";
    private const string DisplayVersion = "0.8.1 beta";
    private const string Publisher = "Chemys";
    private const string WebView2ClientKey = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\O2CloudDrive";

    internal static readonly string DefaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "O2 Cloud Drive");

    private static readonly string StartMenuDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        "Programs",
        "O2 Cloud Drive");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunSelfTest() ? 0 : 1;
        }

        if (!IsAdministrator())
        {
            RelaunchElevated(args);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return InstallerForm.ExitCode;
    }

    internal static async Task InstallAsync(string selectedInstallDir, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var installDir = NormalizeInstallDir(selectedInstallDir);
        ValidateInstallDir(installDir);

        var workDir = Path.Combine(Path.GetTempPath(), "O2CloudDriveSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            progress.Report(new InstallProgress(5, "Preparando instalador..."));
            var payloadZip = await ExtractResourceAsync("payload.zip", workDir, cancellationToken);
            var winFspMsi = await ExtractResourceAsync("winfsp-2.1.25156.msi", workDir, cancellationToken);
            var webView2Installer = await ExtractResourceAsync("MicrosoftEdgeWebView2RuntimeInstallerX64.exe", workDir, cancellationToken);
            var uninstaller = await ExtractResourceAsync("O2CloudDrive.Uninstall.exe", workDir, cancellationToken);

            if (!IsWinFspInstalled())
            {
                progress.Report(new InstallProgress(20, "Instalando WinFsp..."));
                var code = await RunProcessAsync("msiexec.exe", $"/i \"{winFspMsi}\" /qn /norestart", [0, 3010], cancellationToken);
                InstallerForm.NeedsReboot = code == 3010;
            }
            else
            {
                progress.Report(new InstallProgress(25, "WinFsp ya esta instalado."));
            }

            if (!IsWebView2Installed())
            {
                progress.Report(new InstallProgress(38, "Instalando WebView2 Runtime..."));
                await RunProcessAsync(webView2Installer, "/silent /install", [0], cancellationToken);
            }
            else
            {
                progress.Report(new InstallProgress(42, "WebView2 Runtime ya esta instalado."));
            }

            progress.Report(new InstallProgress(52, "Cerrando O2 Cloud Drive si estaba abierto..."));
            CloseRunningApp();

            progress.Report(new InstallProgress(60, "Desplegando aplicacion..."));
            var extractDir = Path.Combine(workDir, "payload");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(payloadZip, extractDir, overwriteFiles: true);

            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            Directory.CreateDirectory(installDir);
            CopyDirectory(extractDir, installDir);
            DeleteInstalledReadme(installDir);
            File.Copy(uninstaller, Path.Combine(installDir, "O2CloudDrive.Uninstall.exe"), overwrite: true);

            progress.Report(new InstallProgress(78, "Creando accesos directos..."));
            CreateShortcuts(installDir);

            progress.Report(new InstallProgress(88, "Registrando desinstalador..."));
            RegisterUninstaller(installDir);

            progress.Report(new InstallProgress(100, "Instalacion completada."));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static bool RunSelfTest()
    {
        var names = Assembly.GetExecutingAssembly().GetManifestResourceNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("payload.zip") &&
               names.Contains("winfsp-2.1.25156.msi") &&
               names.Contains("MicrosoftEdgeWebView2RuntimeInstallerX64.exe") &&
               names.Contains("O2CloudDrive.Uninstall.exe");
    }

    internal static string NormalizeInstallDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = DefaultInstallDir;
        }

        return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string FolderSelectionToInstallDir(string selectedFolder)
    {
        var folder = NormalizeInstallDir(selectedFolder);
        return string.Equals(Path.GetFileName(folder), "O2 Cloud Drive", StringComparison.OrdinalIgnoreCase)
            ? folder
            : Path.Combine(folder, "O2 Cloud Drive");
    }

    internal static void ValidateInstallDir(string installDir)
    {
        var normalized = NormalizeInstallDir(installDir);
        var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La carpeta de instalacion no es valida.");
        }

        var blocked = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(NormalizeInstallDir);

        if (blocked.Any(path => string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Elige una subcarpeta especifica para O2 Cloud Drive.");
        }

        if (Directory.Exists(normalized))
        {
            var hasExistingContent = Directory.EnumerateFileSystemEntries(normalized).Any();
            var looksLikePreviousInstall =
                File.Exists(Path.Combine(normalized, "O2CloudDrive.exe")) ||
                File.Exists(Path.Combine(normalized, "O2CloudDrive.Uninstall.exe"));

            if (hasExistingContent && !looksLikePreviousInstall)
            {
                throw new InvalidOperationException("La carpeta elegida ya contiene archivos. Elige una carpeta vacia o una instalacion previa de O2 Cloud Drive.");
            }
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated(string[] args)
    {
        var escapedArgs = string.Join(" ", args.Select(QuoteArgument));
        var startInfo = new ProcessStartInfo
        {
            FileName = Application.ExecutablePath,
            Arguments = escapedArgs,
            UseShellExecute = true,
            Verb = "runas"
        };

        Process.Start(startInfo);
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

    private static async Task<string> ExtractResourceAsync(string resourceName, string targetDirectory, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(targetDirectory, resourceName);
        await using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No se encontro el recurso embebido {resourceName}.");
        await using var output = File.Create(outputPath);
        await input.CopyToAsync(output, cancellationToken);
        return outputPath;
    }

    private static bool IsWinFspInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
        var installDir = key?.GetValue("InstallDir") as string;
        return !string.IsNullOrWhiteSpace(installDir) && Directory.Exists(Path.Combine(installDir, "bin"));
    }

    private static bool IsWebView2Installed()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WebView2ClientKey);
        var version = key?.GetValue("pv") as string;
        return !string.IsNullOrWhiteSpace(version);
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, int[] successCodes, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);

        if (!successCodes.Contains(process.ExitCode))
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} termino con codigo {process.ExitCode}.");
        }

        return process.ExitCode;
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
                // The installer can continue if the process exits between enumeration and close.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void DeleteInstalledReadme(string installDir)
    {
        File.Delete(Path.Combine(installDir, "LEEME.txt"));
    }

    private static void CreateShortcuts(string installDir)
    {
        Directory.CreateDirectory(StartMenuDir);

        var exePath = Path.Combine(installDir, "O2CloudDrive.exe");
        var uninstallerPath = Path.Combine(installDir, "O2CloudDrive.Uninstall.exe");
        CreateShortcut(Path.Combine(StartMenuDir, "O2 Cloud Drive.lnk"), exePath, "", installDir, exePath);
        CreateShortcut(
            Path.Combine(StartMenuDir, "Desinstalar O2 Cloud Drive.lnk"),
            uninstallerPath,
            "",
            installDir,
            exePath);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string iconPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("No se pudo abrir WScript.Shell.");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            SetComProperty(shortcut!, "TargetPath", targetPath);
            SetComProperty(shortcut!, "Arguments", arguments);
            SetComProperty(shortcut!, "WorkingDirectory", workingDirectory);
            SetComProperty(shortcut!, "IconLocation", iconPath);
            shortcut!.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void SetComProperty(object instance, string propertyName, object value)
    {
        instance.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, instance, [value]);
    }

    private static void RegisterUninstaller(string installDir)
    {
        var exePath = Path.Combine(installDir, "O2CloudDrive.exe");
        var uninstallerPath = Path.Combine(installDir, "O2CloudDrive.Uninstall.exe");
        var estimatedSizeKb = Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length) / 1024;

        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, writable: true)
            ?? throw new InvalidOperationException("No se pudo crear la entrada de desinstalacion.");

        key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", DisplayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"{exePath},0", RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --install-dir \"{installDir}\"", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --install-dir \"{installDir}\" --quiet", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(estimatedSizeKb, int.MaxValue), RegistryValueKind.DWord);
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
        catch
        {
            // Temporary installer files are safe to leave behind if Windows is still scanning them.
        }
    }
}

internal readonly record struct InstallProgress(int Percent, string Message);
