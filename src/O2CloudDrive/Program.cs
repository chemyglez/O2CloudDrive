using System.Text;
using System.Text.Json;
using O2CloudDrive;
using O2CloudDrive.Config;
using O2CloudDrive.Mounting;
using O2CloudDrive.Shell;
using O2CloudDrive.Ui;
using O2CloudDrive.VirtualFileSystem;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var config = new ConfigService().Load(args);
        if (!string.IsNullOrWhiteSpace(config.SharePath))
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var shareServices = O2DriveAppServices.Create(config);
            return ShareLinkCommand.Run(config, shareServices, config.SharePath);
        }

        if (ShouldRunCommandLine(config, args))
        {
            return RunCommandLine(config);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var services = O2DriveAppServices.Create(config);
        ShellContextMenuService.Register(Application.ExecutablePath);
        Application.Run(new MainForm(services));
        return 0;
    }

    private static bool ShouldRunCommandLine(AppConfig config, string[] args)
    {
        return config.SelfTest ||
               config.ApiProbe ||
               config.Logout ||
               config.RunForSeconds is not null ||
               args.Any(arg => arg.Equals("--console", StringComparison.OrdinalIgnoreCase));
    }

    private static int RunCommandLine(AppConfig config)
    {
        using var services = O2DriveAppServices.Create(config);
        if (config.SelfTest)
        {
            var selfTestStore = new InMemoryCloudFileStore();
            RunSelfTest(selfTestStore);
            return 0;
        }

        if (config.Logout)
        {
            services.AuthService.Logout();
            Console.WriteLine("Sesion de O2 Cloud eliminada: Credential Manager, validationkey y perfil WebView2 local.");
            return 0;
        }

        if (config.ApiProbe)
        {
            return RunApiProbe(config, services);
        }

        if (config.RequireAuthentication)
        {
            Console.WriteLine("Comprobando sesion de O2 Cloud...");
            var session = services.AuthService.EnsureAuthenticated(allowInteractive: true);
            if (session is not { IsAuthenticated: true })
            {
                Console.Error.WriteLine("No hay sesion valida. No se monta la unidad.");
                return 1;
            }

            Console.WriteLine("Sesion de O2 Cloud validada.");
        }
        else
        {
            Console.WriteLine("Autenticacion omitida por --skip-auth. La unidad usara datos simulados.");
        }

        services.MountService.Mount(new DriveMountOptions(config.MountPoint, config.VolumeLabel, config.UseSimulatedData));

        Console.WriteLine($"O2 Cloud Prototype montado en {config.MountPoint}");
        Console.WriteLine("Pulsa Ctrl+C para desmontar.");

        using var stopEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopEvent.Set();
        };

        if (config.RunForSeconds is > 0)
        {
            stopEvent.Wait(TimeSpan.FromSeconds(config.RunForSeconds.Value));
        }
        else
        {
            stopEvent.Wait();
        }

        services.MountService.Unmount();
        Console.WriteLine($"Unidad {config.MountPoint} desmontada.");
        return 0;
    }

    private static void RunSelfTest(ICloudFileStore store)
    {
        if (!store.TryGetByPath("\\Documentos\\README.txt", out var readme))
        {
            throw new InvalidOperationException("No se encontro el archivo de ejemplo.");
        }

        var bytes = store.ReadBytes(readme, 0, 32);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("El archivo de ejemplo no se puede leer.");
        }

        var folder = store.Create("\\Pruebas", CloudItemKind.Directory, FileSystemAttributes.Directory);
        var file = store.Create("\\Pruebas\\nuevo.txt", CloudItemKind.File, FileSystemAttributes.Archive);
        store.WriteBytes(file, 0, "contenido local"u8.ToArray(), writeToEndOfFile: false, constrainedIo: false);
        store.Rename(file, "\\Pruebas\\renombrado.txt", replaceIfExists: false);

        if (!store.TryGetChild(folder, "renombrado.txt", out var renamed) || renamed.Size == 0)
        {
            throw new InvalidOperationException("La escritura o renombrado local fallo.");
        }

        store.Delete(renamed);
        store.Delete(folder);
        Console.WriteLine("Self-test OK: lectura, creacion, escritura, renombrado y borrado funcionan en el backend simulado.");
    }

    private static int RunApiProbe(AppConfig config, O2DriveAppServices services)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "O2CloudDrive-api-probe.txt");
        var report = new StringBuilder();
        report.AppendLine("O2 Cloud Drive API probe");
        report.AppendLine(DateTimeOffset.Now.ToString("u"));

        var session = services.AuthService.EnsureAuthenticated(allowInteractive: false);
        if (session is not { IsAuthenticated: true })
        {
            report.AppendLine("No hay sesion guardada valida. Haz login nuevo antes de ejecutar el probe.");
            File.WriteAllText(outputPath, report.ToString(), Encoding.UTF8);
            return 1;
        }

        using var httpClient = new HttpClient();
        var baseUri = new Uri(config.ApiBaseUrl, UriKind.Absolute);
        JsonDocument rootDocument;
        try
        {
            rootDocument = SendProbeRequest(
                httpClient,
                baseUri,
                session,
                HttpMethod.Post,
                "media/folder/root",
                new Dictionary<string, string?> { ["action"] = "get" });
        }
        catch (Exception ex)
        {
            report.AppendLine("Root error: " + Sanitize(ex.Message));
            File.WriteAllText(outputPath, report.ToString(), Encoding.UTF8);
            return 1;
        }

        using (rootDocument)
        {
            var root = rootDocument.RootElement;
            var data = Object(root, "data");
            var rootFolders = Array(data, "folders");
            var rootFolder = rootFolders.Count > 0 ? rootFolders[0] : root;
            report.AppendLine("Root keys: " + Keys(rootFolder));
            report.AppendLine("Root ids: " + IdSummary(rootFolder));

            var rootCandidates = IdCandidates(rootFolder, includeParent: false);
            ProbeCandidates(report, httpClient, baseUri, session, "root", rootCandidates);

            var workingRootCandidate = rootCandidates.FirstOrDefault(candidate =>
                TryGetFolderChildren(httpClient, baseUri, session, candidate, out var children, out _) && children.Count > 0);

            if (workingRootCandidate is null)
            {
                report.AppendLine("No se pudo obtener ninguna carpeta hija de la raiz.");
            }
            else
            {
                TryGetFolderChildren(httpClient, baseUri, session, workingRootCandidate, out var children, out _);
                foreach (var child in children.Take(8))
                {
                    report.AppendLine();
                    report.AppendLine("Child: " + (String(child, "name") ?? "Carpeta"));
                    report.AppendLine("Child keys: " + Keys(child));
                    report.AppendLine("Child ids: " + IdSummary(child));
                    ProbeCandidates(report, httpClient, baseUri, session, "child", IdCandidates(child, includeParent: true));
                }
            }
        }

        File.WriteAllText(outputPath, report.ToString(), Encoding.UTF8);
        return 0;
    }

    private static void ProbeCandidates(
        StringBuilder report,
        HttpClient httpClient,
        Uri baseUri,
        O2CloudDrive.Auth.O2Session session,
        string label,
        IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            report.Append("Probe ");
            report.Append(label);
            report.Append(' ');
            report.Append(Mask(candidate));
            report.Append(": ");

            var folderStatus = TryGetFolderChildren(httpClient, baseUri, session, candidate, out var folders, out var folderError)
                ? $"folders={folders.Count}"
                : $"foldersError={folderError}";
            var mediaStatus = TryGetMediaChildren(httpClient, baseUri, session, candidate, out var media, out var mediaError)
                ? $"media={media.Count}"
                : $"mediaError={mediaError}";

            report.Append(folderStatus);
            report.Append("; ");
            report.AppendLine(mediaStatus);
            foreach (var variant in ProbeMediaVariants(httpClient, baseUri, session, candidate))
            {
                report.Append("  ");
                report.AppendLine(variant);
            }
        }
    }

    private static IEnumerable<string> ProbeMediaVariants(
        HttpClient httpClient,
        Uri baseUri,
        O2CloudDrive.Auth.O2Session session,
        string folderId)
    {
        var variants = new (string Name, HttpMethod Method, string Resource, Dictionary<string, string?> Query, object? Body)[]
        {
            ("POST media query folderid fields", HttpMethod.Post, "media", new() { ["action"] = "get", ["folderid"] = folderId, ["limit"] = "20" }, MediaFieldsBody()),
            ("POST media query folderid no-body", HttpMethod.Post, "media", new() { ["action"] = "get", ["folderid"] = folderId, ["limit"] = "20" }, null),
            ("GET media query folderid", HttpMethod.Get, "media", new() { ["action"] = "get", ["folderid"] = folderId, ["limit"] = "20" }, null),
            ("POST media body folderid", HttpMethod.Post, "media", new() { ["action"] = "get", ["limit"] = "20" }, new { data = new { folderid = folderId, fields = new[] { "name", "size", "folderid", "type", "mediatype" } } }),
            ("POST media raw body folderid", HttpMethod.Post, "media", new() { ["action"] = "get", ["limit"] = "20" }, new { folderid = folderId, fields = new[] { "name", "size", "folderid", "type", "mediatype" } }),
            ("POST media query parentid", HttpMethod.Post, "media", new() { ["action"] = "get", ["parentid"] = folderId, ["limit"] = "20" }, MediaFieldsBody()),
            ("POST media/file query folderid", HttpMethod.Post, "media/file", new() { ["action"] = "get", ["folderid"] = folderId, ["limit"] = "20" }, MediaFieldsBody()),
            ("GET media/file query folderid", HttpMethod.Get, "media/file", new() { ["action"] = "get", ["folderid"] = folderId, ["limit"] = "20" }, null),
        };

        foreach (var variant in variants)
        {
            using JsonDocument document = TryProbeVariant(
                httpClient,
                baseUri,
                session,
                variant.Method,
                variant.Resource,
                variant.Query,
                variant.Body,
                out var error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                yield return $"{variant.Name}: error={error}";
                continue;
            }

            var root = document.RootElement;
            var data = Object(root, "data");
            var media = FirstArray(data, "media", "files", "videos", "audios", "pictures", "images", "items");
            if (media.Count == 0)
            {
                media = FirstArray(root, "media", "files", "videos", "audios", "pictures", "images", "items");
            }

            var firstMedia = media.FirstOrDefault();
            var firstMediaSummary = firstMedia.ValueKind == JsonValueKind.Object
                ? $"; firstMediaKeys={Keys(firstMedia)}; firstMediaIds={string.Join(",", IdCandidates(firstMedia, includeParent: false).Select(Mask))}"
                : string.Empty;
            yield return $"{variant.Name}: ok media={media.Count}; rootKeys={Keys(root)}; dataKeys={Keys(data)}{firstMediaSummary}";
        }
    }

    private static object MediaFieldsBody()
    {
        return new
        {
            data = new
            {
                fields = MediaListFields(),
            },
        };
    }

    private static string[] MediaListFields()
    {
        return
        [
            "name",
            "modificationdate",
            "creationdate",
            "size",
            "thumbnails",
            "thumbnaildimensions",
            "viewurl",
            "videometadata",
            "audiometadata",
            "favorite",
            "shared",
            "etag",
            "origin",
            "folderid",
            "uploaded",
        ];
    }

    private static JsonDocument TryProbeVariant(
        HttpClient httpClient,
        Uri baseUri,
        O2CloudDrive.Auth.O2Session session,
        HttpMethod method,
        string resource,
        IReadOnlyDictionary<string, string?> query,
        object? body,
        out string error)
    {
        error = string.Empty;
        try
        {
            return SendProbeRequest(httpClient, baseUri, session, method, resource, query, body);
        }
        catch (Exception ex)
        {
            error = Sanitize(ex.Message);
            return JsonDocument.Parse("{}");
        }
    }

    private static bool TryGetFolderChildren(
        HttpClient httpClient,
        Uri baseUri,
        O2CloudDrive.Auth.O2Session session,
        string folderId,
        out IReadOnlyList<JsonElement> folders,
        out string error)
    {
        folders = [];
        error = string.Empty;
        try
        {
            using var document = SendProbeRequest(
                httpClient,
                baseUri,
                session,
                HttpMethod.Get,
                "media/folder",
                new Dictionary<string, string?>
                {
                    ["action"] = "list",
                    ["parentid"] = folderId,
                    ["limit"] = "20",
                });
            var root = document.RootElement;
            var data = Object(root, "data");
            folders = Array(data, "folders");
            if (folders.Count == 0)
            {
                folders = Array(root, "folders");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = Sanitize(ex.Message);
            return false;
        }
    }

    private static bool TryGetMediaChildren(
        HttpClient httpClient,
        Uri baseUri,
        O2CloudDrive.Auth.O2Session session,
        string folderId,
        out IReadOnlyList<JsonElement> media,
        out string error)
    {
        media = [];
        error = string.Empty;
        try
        {
            using var document = SendProbeRequest(
                httpClient,
                baseUri,
                session,
                HttpMethod.Post,
                "media",
                new Dictionary<string, string?>
                {
                    ["action"] = "get",
                    ["folderid"] = folderId,
                    ["limit"] = "20",
                },
                new
                {
                    data = new
                    {
                        fields = new[]
                        {
                            "name",
                            "modificationdate",
                            "creationdate",
                            "size",
                            "thumbnails",
                            "thumbnaildimensions",
                            "viewurl",
                            "videometadata",
                            "audiometadata",
                            "favorite",
                            "shared",
                            "etag",
                            "origin",
                            "folderid",
                            "uploaded",
                        },
                    },
                });
            var root = document.RootElement;
            var data = Object(root, "data");
            media = FirstArray(data, "media", "files", "videos", "audios", "pictures", "images", "items");
            if (media.Count == 0)
            {
                media = FirstArray(root, "media", "files", "videos", "audios", "pictures", "images", "items");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = Sanitize(ex.Message);
            return false;
        }
    }

    private static JsonDocument SendProbeRequest(
        HttpClient httpClient,
        Uri baseUri,
        O2CloudDrive.Auth.O2Session session,
        HttpMethod method,
        string resource,
        IReadOnlyDictionary<string, string?> query,
        object? body = null)
    {
        var builder = new UriBuilder(new Uri(baseUri, resource));
        var parameters = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .Append($"validationkey={Uri.EscapeDataString(session.ValidationKey)}");
        builder.Query = string.Join("&", parameters);

        using var request = new HttpRequestMessage(method, builder.Uri);
        if (!string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(session.UserAgent) ? "O2CloudDrive/0.1" : session.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = httpClient.Send(request);
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Sanitize(text)}");
        }

        var document = JsonDocument.Parse(text);
        var error = Object(document.RootElement, "error");
        if (error.ValueKind == JsonValueKind.Object)
        {
            var code = String(error, "code", "errorcode") ?? string.Empty;
            var message = String(error, "message", "description", "detail") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message))
            {
                throw new InvalidOperationException($"{code} {message}".Trim());
            }
        }

        return document;
    }

    private static JsonElement Object(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : default;
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(item => item.Clone()).ToList()
            : [];
    }

    private static IReadOnlyList<JsonElement> FirstArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var array = Array(element, name);
            if (array.Count > 0)
            {
                return array;
            }
        }

        return [];
    }

    private static IReadOnlyList<string> IdCandidates(JsonElement element, bool includeParent)
    {
        var names = includeParent
            ? new[] { "id", "mediaid", "mediaId", "fdoid", "folderid", "folderId", "uuid", "parentid" }
            : new[] { "id", "mediaid", "mediaId", "fdoid", "folderid", "folderId", "uuid" };
        return names
            .Select(name => String(element, name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? String(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            var value = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Keys(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            ? string.Join(", ", element.EnumerateObject().Select(property => property.Name))
            : "<no-object>";
    }

    private static string IdSummary(JsonElement element)
    {
        var keys = new[] { "id", "folderid", "folderId", "uuid", "parentid", "mediaid", "fdoid" };
        return string.Join(", ", keys
            .Select(key => (key, value: String(element, key)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.value))
            .Select(pair => $"{pair.key}={Mask(pair.value!)}"));
    }

    private static string Mask(string value)
    {
        var clean = value.Trim();
        return clean.Length <= 6 ? "***" : $"{clean[..3]}...{clean[^3..]}";
    }

    private static string Sanitize(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
