using System.Text.Json;

namespace O2CloudDrive.Config;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public AppConfig Load(string[] args)
    {
        var config = LoadFromFile();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { SelfTest = true };
                continue;
            }

            if (arg.Equals("--api-probe", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { ApiProbe = true };
                continue;
            }

            if (arg.Equals("--skip-auth", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { RequireAuthentication = false, UseSimulatedData = true };
                continue;
            }

            if (arg.Equals("--simulated", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { UseSimulatedData = true };
                continue;
            }

            if (arg.Equals("--logout", StringComparison.OrdinalIgnoreCase))
            {
                config = config with { Logout = true };
                continue;
            }

            if (arg.Equals("--share", StringComparison.OrdinalIgnoreCase) && TryReadValue(args, ref i, out var sharePath))
            {
                config = config with { SharePath = sharePath };
                continue;
            }

            if (arg.Equals("--mount", StringComparison.OrdinalIgnoreCase) && TryReadValue(args, ref i, out var mountPoint))
            {
                config = config with { MountPoint = NormalizeMountPoint(mountPoint) };
                continue;
            }

            if (arg.Equals("--run-for-seconds", StringComparison.OrdinalIgnoreCase) &&
                TryReadValue(args, ref i, out var secondsText) &&
                int.TryParse(secondsText, out var seconds))
            {
                config = config with { RunForSeconds = seconds };
                continue;
            }

            if (arg.Equals("--cache", StringComparison.OrdinalIgnoreCase) && TryReadValue(args, ref i, out var cache))
            {
                config = config with { CacheDirectory = cache };
                continue;
            }

            if (arg.Equals("--login-url", StringComparison.OrdinalIgnoreCase) && TryReadValue(args, ref i, out var loginUrl))
            {
                config = config with { LoginUrl = loginUrl };
            }
        }

        return config with
        {
            MountPoint = NormalizeMountPoint(config.MountPoint),
            CacheDirectory = ExpandEnvironmentVariables(config.CacheDirectory),
        };
    }

    private static AppConfig LoadFromFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Environment.CurrentDirectory, "appsettings.json");
        }

        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
        {
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        var trimmed = mountPoint.Trim().TrimEnd('\\');
        return trimmed.EndsWith(':') ? trimmed : $"{trimmed}:";
    }

    private static string ExpandEnvironmentVariables(string value)
    {
        return Environment.ExpandEnvironmentVariables(value);
    }
}
