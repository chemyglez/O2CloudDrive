using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using O2CloudDrive.Ui;

namespace O2CloudDrive.Auth;

public sealed class O2LoginForm : Form
{
    private static readonly TimeSpan SessionSettleDelay = TimeSpan.FromMilliseconds(1800);
    private const int MaxValidationKeyCandidates = 5;
    private const int MaxRootResponseCharacters = 1024 * 1024;

    private readonly Uri _loginUri;
    private readonly Uri _apiBaseUri;
    private readonly O2SessionValidator _validator;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Text = "Cargando login oficial de O2 Cloud...",
    };

    private readonly System.Windows.Forms.Timer _captureTimer = new() { Interval = 2000 };
    private readonly LinkedList<string> _recentValidationKeys = new();
    private readonly Dictionary<string, DateTimeOffset> _validationKeyObservedAt = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _captureCancellation = new();
    private string? _successfulRootValidationKey;
    private long _latestRootResponseOrdinal;
    private bool _capturing;

    public O2LoginForm(Uri loginUri, Uri apiBaseUri, O2SessionValidator validator)
    {
        _loginUri = loginUri;
        _apiBaseUri = apiBaseUri;
        _validator = validator;

        Text = "O2 Cloud - Login";
        Width = 1100;
        Height = 760;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Load();

        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 2,
            Padding = new Padding(8, 8, 8, 4),
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var cancelButton = new Button
        {
            Text = "Cancelar",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 0, 0, 0),
        };
        cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        topPanel.Controls.Add(_statusLabel, 0, 0);
        topPanel.Controls.Add(cancelButton, 1, 0);

        Controls.Add(_webView);
        Controls.Add(topPanel);

        _captureTimer.Tick += async (_, _) =>
        {
            await AcceptCookieConsentAsync();
            if (CanContinueCapture)
            {
                await CaptureAndValidateAsync();
            }
        };
        Shown += async (_, _) => await InitializeWebViewAsync();
        FormClosed += (_, _) =>
        {
            _captureTimer.Stop();
            _captureCancellation.Cancel();
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived;
            }

            _webView.Dispose();
        };
    }

    public O2Session? CapturedSession { get; private set; }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = O2WebViewProfile.UserDataFolder;
            Directory.CreateDirectory(userDataFolder);

            var environmentOptions = new CoreWebView2EnvironmentOptions
            {
                EnableTrackingPrevention = false,
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, environmentOptions);
            if (!CanContinueCapture)
            {
                return;
            }

            await _webView.EnsureCoreWebView2Async(environment);
            if (!CanContinueCapture)
            {
                return;
            }

            _webView.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
            _webView.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LoginBootstrapScript);
            if (!CanContinueCapture)
            {
                return;
            }

            _webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;

            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _webView.CoreWebView2.Navigate(args.Uri);
            };
            _webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!CanContinueCapture)
                {
                    return;
                }

                TrackUrl(args.Uri);
                _statusLabel.Text = "Login oficial de O2 Cloud cargando...";
            };
            _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                if (!CanContinueCapture)
                {
                    return;
                }

                _statusLabel.Text = "Esperando telefono, contrasena y codigo SMS de O2 Cloud...";
                await AcceptCookieConsentAsync();
                if (!CanContinueCapture)
                {
                    return;
                }

                await CaptureAndValidateAsync();
                if (CanContinueCapture && CapturedSession is null)
                {
                    _statusLabel.Text = "Introduce telefono, contrasena y SMS. La app capturara la sesion cuando O2 la valide.";
                }
            };
            _webView.CoreWebView2.DOMContentLoaded += async (_, _) =>
            {
                await AcceptCookieConsentAsync();
                if (CanContinueCapture)
                {
                    await CaptureAndValidateAsync();
                }
            };

            _captureTimer.Start();
            _statusLabel.Text = "Abriendo login oficial de O2 Cloud...";
            _webView.CoreWebView2.Navigate(_loginUri.ToString());
        }
        catch (Exception ex)
        {
            if (!CanContinueCapture)
            {
                return;
            }

            _statusLabel.Text = "No se pudo inicializar WebView2.";
            MessageBox.Show(this, ex.Message, "O2 Cloud Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task AcceptCookieConsentAsync()
    {
        if (!CanContinueCapture || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await _webView.ExecuteScriptAsync(CookieConsentScript);
        }
        catch
        {
            // The login page can navigate while the script is running.
        }
    }

    private async void OnWebResourceResponseReceived(
        object? sender,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!CanContinueCapture)
        {
            return;
        }

        try
        {
            if (!IsSuccessfulRootResponse(
                    args.Request.Method,
                    args.Request.Uri,
                    args.Response.StatusCode,
                    _apiBaseUri))
            {
                return;
            }

            var responseOrdinal = Interlocked.Increment(ref _latestRootResponseOrdinal);
            var requestKey = O2ValidationKey.ExtractFromUri(args.Request.Uri).FirstOrDefault();
            if (requestKey is not null)
            {
                _successfulRootValidationKey = requestKey;
                RememberValidationKey(requestKey);
            }

            using var content = await args.Response.GetContentAsync();
            var responseText = await ReadLimitedTextAsync(content, _captureCancellation.Token);
            if (!CanContinueCapture ||
                responseText is null ||
                !IsLatestRootResponse(responseOrdinal, Volatile.Read(ref _latestRootResponseOrdinal)))
            {
                return;
            }

            var responseKeys = ExtractValidationKeysFromJson(responseText);
            foreach (var responseKey in responseKeys.Reverse())
            {
                RememberValidationKey(responseKey);
            }

            _successfulRootValidationKey = responseKeys.FirstOrDefault() ?? requestKey;
        }
        catch (OperationCanceledException) when (_captureCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or System.Runtime.InteropServices.COMException)
        {
            // The response is only a prioritization hint; the .NET validator remains authoritative.
        }
    }

    private static async Task<string?> ReadLimitedTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return text.ToString();
            }

            if (text.Length + read > MaxRootResponseCharacters)
            {
                return null;
            }

            text.Append(buffer, 0, read);
        }
    }

    private async Task CaptureAndValidateAsync()
    {
        if (_capturing || !CanContinueCapture || _webView.CoreWebView2 is null)
        {
            return;
        }

        _capturing = true;
        try
        {
            var sessions = await CaptureSessionsAsync();
            if (!CanContinueCapture)
            {
                return;
            }

            if (sessions.Count == 0)
            {
                return;
            }

            var settledSessions = sessions
                .Where(session => HasSettled(session.ValidationKey))
                .ToList();
            if (settledSessions.Count == 0)
            {
                _statusLabel.Text = "Sesion detectada, esperando a que O2 termine de guardar las cookies...";
                return;
            }

            _statusLabel.Text = "Sesion detectada. Validando la carpeta raiz con O2 Cloud...";
            var result = await Task.Run(
                () => ValidateCandidates(
                    settledSessions,
                    session => _validator.ValidateSession(session, _captureCancellation.Token)),
                _captureCancellation.Token);
            if (!CanContinueCapture)
            {
                return;
            }

            if (result.Session is not null)
            {
                CapturedSession = result.Session;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _statusLabel.Text = result.HadUnavailable
                ? "O2 Cloud no permite comprobar la sesion ahora. Se reintentara sin borrar los datos del login."
                : "Sesion detectada, esperando a que O2 termine de validarla...";
        }
        catch (OperationCanceledException) when (_captureCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (CanContinueCapture)
            {
                _statusLabel.Text = "Esperando a que O2 Cloud complete el login...";
            }
        }
        finally
        {
            _capturing = false;
        }
    }

    private bool CanContinueCapture =>
        !_captureCancellation.IsCancellationRequested &&
        !IsDisposed &&
        !Disposing;

    private async Task<IReadOnlyList<O2Session>> CaptureSessionsAsync()
    {
        var state = await ReadBrowserStateAsync();
        var serializedState = state?.GetRawText() ?? string.Empty;
        var cookies = await ReadCookiesAsync();
        var cookieValidationKey = O2ValidationKey.ExtractFromCookies(cookies);
        var capturedKeys = ReadStringArray(state, "validationKeys")
            .Concat(ExtractValidationKeys(serializedState));
        var validationKeys = SelectValidationKeys(
            [
                _successfulRootValidationKey,
                cookieValidationKey,
                ReadString(state, "validationKey"),
            ],
            capturedKeys,
            _recentValidationKeys);

        if (validationKeys.Count == 0)
        {
            return [];
        }

        foreach (var validationKey in validationKeys.Reverse())
        {
            RememberValidationKey(validationKey);
        }

        var userAgent = ReadString(state, "userAgent") ?? string.Empty;

        return validationKeys
            .Select(validationKey => new O2Session
            {
                ValidationKey = validationKey,
                Cookies = new Dictionary<string, string>(cookies, StringComparer.OrdinalIgnoreCase),
                UserAgent = userAgent,
            })
            .ToList();
    }

    private async Task<JsonElement?> ReadBrowserStateAsync()
    {
        var raw = await _webView.ExecuteScriptAsync(CaptureScript);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<Dictionary<string, string>> ReadCookiesAsync()
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in CookieProbeUris(_loginUri, _apiBaseUri))
        {
            var webViewCookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
            foreach (var cookie in webViewCookies)
            {
                if (!CookieDomainMatches(_apiBaseUri.Host, cookie.Domain))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value))
                {
                    cookies[cookie.Name] = cookie.Value;
                }
            }
        }

        return cookies;
    }

    private void TrackUrl(string? uri)
    {
        foreach (var validationKey in ExtractValidationKeys(uri))
        {
            RememberValidationKey(validationKey);
        }
    }

    private void RememberValidationKey(string validationKey)
    {
        var normalized = NormalizeValidationKey(validationKey);
        if (normalized is null)
        {
            return;
        }

        var existing = _recentValidationKeys.Find(normalized);
        if (existing is not null)
        {
            _recentValidationKeys.Remove(existing);
        }
        else
        {
            _validationKeyObservedAt[normalized] = DateTimeOffset.UtcNow;
        }

        _recentValidationKeys.AddFirst(normalized);
        while (_recentValidationKeys.Count > MaxValidationKeyCandidates)
        {
            var oldest = _recentValidationKeys.Last!.Value;
            _recentValidationKeys.RemoveLast();
            _validationKeyObservedAt.Remove(oldest);
        }
    }

    private bool HasSettled(string validationKey)
    {
        return _validationKeyObservedAt.TryGetValue(validationKey, out var observedAt) &&
               DateTimeOffset.UtcNow - observedAt >= SessionSettleDelay;
    }

    internal static IReadOnlyList<Uri> CookieProbeUris(Uri loginUri, Uri apiBaseUri)
    {
        var loginRoot = new Uri(loginUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        var apiRootEndpoint = new Uri(apiBaseUri, "media/folder/root");
        return new[] { loginRoot, apiRootEndpoint }
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool CookieDomainMatches(string host, string? cookieDomain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(cookieDomain))
        {
            return false;
        }

        var normalizedDomain = cookieDomain.Trim().TrimStart('.');
        return host.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> SelectValidationKeys(
        IEnumerable<string?> preferredValues,
        IEnumerable<string> capturedValues,
        IEnumerable<string> rememberedValues)
    {
        var candidates = new List<string>();
        foreach (var value in preferredValues)
        {
            AddValidationKey(candidates, value);
        }

        foreach (var value in rememberedValues)
        {
            AddValidationKey(candidates, value);
        }

        foreach (var value in capturedValues)
        {
            AddValidationKey(candidates, value);
        }

        return candidates.Take(MaxValidationKeyCandidates).ToList();
    }

    internal static SessionValidationResult ValidateCandidates(
        IEnumerable<O2Session> sessions,
        Func<O2Session, O2SessionValidationResult> validate)
    {
        var hadUnavailable = false;
        foreach (var session in sessions)
        {
            var result = validate(session);
            if (result.Status == O2SessionValidationStatus.Valid)
            {
                return new SessionValidationResult(result.ValidatedSession ?? session, hadUnavailable);
            }

            if (result.Status == O2SessionValidationStatus.Unavailable)
            {
                hadUnavailable = true;
            }
        }

        return new SessionValidationResult(null, hadUnavailable);
    }

    internal static string? SuccessfulRootValidationKey(
        string? method,
        string? requestUri,
        int statusCode,
        Uri apiBaseUri)
    {
        if (!IsSuccessfulRootResponse(method, requestUri, statusCode, apiBaseUri) ||
            !Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return O2ValidationKey.ExtractFromUri(uri.AbsoluteUri).FirstOrDefault();
    }

    internal static bool IsSuccessfulRootResponse(
        string? method,
        string? requestUri,
        int statusCode,
        Uri apiBaseUri)
    {
        if (!HttpMethod.Get.Method.Equals(method, StringComparison.OrdinalIgnoreCase) ||
            statusCode is < 200 or >= 300 ||
            !Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var rootUri = new Uri(apiBaseUri, "media/folder/root");
        if (!O2SessionRequest.IsSameOrigin(uri, rootUri) ||
            !uri.AbsolutePath.Equals(rootUri.AbsolutePath, StringComparison.OrdinalIgnoreCase) ||
            !QueryContains(uri, "action", "get"))
        {
            return false;
        }

        return true;
    }

    private static bool QueryContains(Uri uri, string name, string expectedValue)
    {
        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            try
            {
                if (Uri.UnescapeDataString(pair[0]).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    Uri.UnescapeDataString(pair[1]).Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (UriFormatException)
            {
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ExtractValidationKeys(string? text)
    {
        return O2ValidationKey.ExtractFromText(text);
    }

    internal static IReadOnlyList<string> ExtractValidationKeysFromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return O2ValidationKey.ExtractFromJson(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static bool IsLatestRootResponse(long responseOrdinal, long latestObservedOrdinal)
    {
        return responseOrdinal == latestObservedOrdinal;
    }

    private static void AddValidationKey(ICollection<string> keys, string? value)
    {
        var normalized = NormalizeValidationKey(value);
        if (normalized is not null && !keys.Contains(normalized, StringComparer.Ordinal))
        {
            keys.Add(normalized);
        }
    }

    private static string? NormalizeValidationKey(string? value)
    {
        return O2ValidationKey.Normalize(value);
    }

    private static string? ReadString(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? element, string name)
    {
        if (element is null ||
            element.Value.ValueKind != JsonValueKind.Object ||
            !element.Value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    internal readonly record struct SessionValidationResult(O2Session? Session, bool HadUnavailable);

    private const string CaptureScript = """
        (() => {
          const readStorage = (storage) => {
            const out = {};
            try {
              for (let i = 0; i < storage.length; i++) {
                const key = storage.key(i);
                out[key] = storage.getItem(key);
              }
            } catch (_) {}
            return out;
          };
          const validationKeys = [];
          const addValidationKey = (value) => {
            if (typeof value !== 'string') return;
            const normalized = value.trim();
            if (normalized.length < 8 || normalized.length > 2048 || /\s/.test(normalized)) return;
            if (/^(undefined|null)$/i.test(normalized)) return;
            if (!validationKeys.includes(normalized)) validationKeys.push(normalized);
          };
          const addEncodedQueryValidationKey = (value) => {
            if (typeof value !== 'string') return;
            let decoded = value;
            try { decoded = decodeURIComponent(value); } catch (_) {}
            addValidationKey(decoded);
          };
          const scanText = (value) => {
            if (typeof value !== 'string' || !value) return;
            try {
              const url = new URL(value, location.href);
              addValidationKey(url.searchParams.get('validationkey'));
              addValidationKey(url.searchParams.get('validation_key'));
            } catch (_) {}
            const queryMatch = value.match(/(?:^|[?&])validation_?key=([^&#\s"']+)/i);
            if (queryMatch) addEncodedQueryValidationKey(queryMatch[1]);
            const normalizedJson = value.replace(/\\"/g, '"');
            const propertyMatch = normalizedJson.match(/"validation_?key"\s*:\s*"([^"\r\n]{8,2048})"/i);
            if (propertyMatch) addValidationKey(propertyMatch[1]);
          };
          const scanValue = (value, depth = 0) => {
            if (depth > 3 || value == null) return;
            if (typeof value === 'string') {
              scanText(value);
              const trimmed = value.trim();
              if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
                try { scanValue(JSON.parse(trimmed), depth + 1); } catch (_) {}
              }
              return;
            }
            if (Array.isArray(value)) {
              for (const child of value) scanValue(child, depth + 1);
              return;
            }
            if (typeof value === 'object') {
              for (const [key, child] of Object.entries(value)) {
                if (key.replace(/_/g, '').toLowerCase() === 'validationkey') addValidationKey(child);
              }
              for (const child of Object.values(value)) scanValue(child, depth + 1);
            }
          };
          const resources = [];
          try {
            for (const entry of performance.getEntriesByType('resource')) {
              if (entry && entry.name) resources.push(entry.name);
            }
          } catch (_) {}
          const seenUrls = Array.isArray(window.__o2CloudDriveSeenUrls)
            ? window.__o2CloudDriveSeenUrls.slice(-100)
            : [];
          const localState = readStorage(localStorage);
          const sessionState = readStorage(sessionStorage);
          scanValue(seenUrls.slice().reverse());
          scanValue(resources.slice(-100).reverse());
          scanValue(sessionState);
          scanValue(localState);
          scanText(location.href);
          const payload = {
            url: location.href,
            userAgent: navigator.userAgent || '',
            localStorage: localState,
            sessionStorage: sessionState,
            seenUrls,
            resources: resources.slice(-100),
            validationKeys,
            validationKey: validationKeys[0] || ''
          };
          return payload;
        })();
        """;

    private const string LoginBootstrapScript = """
        (() => {
          if (window.__o2CloudDriveLoginPatchInstalled) return;
          window.__o2CloudDriveLoginPatchInstalled = true;
          window.__o2CloudDriveSeenUrls = window.__o2CloudDriveSeenUrls || [];
          const remember = (value) => {
            try {
              const url = typeof value === 'string'
                ? value
                : value && value.url
                  ? value.url
                  : '';
              if (url && !window.__o2CloudDriveSeenUrls.includes(url)) {
                window.__o2CloudDriveSeenUrls.push(url);
                if (window.__o2CloudDriveSeenUrls.length > 200) {
                  window.__o2CloudDriveSeenUrls.shift();
                }
              }
            } catch (_) {}
          };
          const originalFetch = window.fetch;
          if (originalFetch) {
            window.fetch = function(input, init) {
              remember(input);
              return originalFetch.apply(this, arguments);
            };
          }
          try {
            const originalOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function(method, url) {
              remember(url);
              return originalOpen.apply(this, arguments);
            };
          } catch (_) {}
          const acceptCookies = () => {
            try {
              const clean = (value) => String(value || '')
                .normalize('NFD')
                .replace(/[\u0300-\u036f]/g, '')
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();
              const visible = (el) => {
                try {
                  const rect = el.getBoundingClientRect();
                  const style = getComputedStyle(el);
                  return rect.width > 0 && rect.height > 0 &&
                    style.visibility !== 'hidden' &&
                    style.display !== 'none' &&
                    style.opacity !== '0';
                } catch (_) {
                  return false;
                }
              };
              const selectors = [
                '#onetrust-accept-btn-handler',
                '#accept-recommended-btn-handler',
                '#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll',
                '[data-testid="uc-accept-all-button"]',
                '[data-testid="cookie-accept-all"]',
                'button[id*="accept" i]',
                'button[class*="accept" i]',
                'button[aria-label*="acept" i]',
                'button[aria-label*="accept" i]'
              ];
              for (const selector of selectors) {
                for (const el of Array.from(document.querySelectorAll(selector))) {
                  if (visible(el)) {
                    el.click();
                    return true;
                  }
                }
              }
              const controls = Array.from(document.querySelectorAll('button,a,input[type=button],input[type=submit],[role=button]'));
              for (const el of controls) {
                const text = clean(el.value || el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '');
                if (visible(el) && (
                  text.includes('aceptar todas') ||
                  text.includes('aceptar todo') ||
                  text.includes('permitir todas') ||
                  text.includes('accept all') ||
                  text.includes('allow all') ||
                  text === 'accept')) {
                  el.click();
                  return true;
                }
              }
            } catch (_) {}
            return false;
          };
          window.__o2CloudDriveAcceptCookies = acceptCookies;
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', acceptCookies);
          } else {
            acceptCookies();
          }
          setInterval(acceptCookies, 700);
        })();
        """;

    private const string CookieConsentScript = """
        (() => {
          try {
            if (window.__o2CloudDriveAcceptCookies) return window.__o2CloudDriveAcceptCookies();
          } catch (_) {}
          const clean = (value) => String(value || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/\s+/g, ' ')
            .trim()
            .toLowerCase();
          const visible = (el) => {
            try {
              const rect = el.getBoundingClientRect();
              const style = getComputedStyle(el);
              return rect.width > 0 && rect.height > 0 &&
                style.visibility !== 'hidden' &&
                style.display !== 'none' &&
                style.opacity !== '0';
            } catch (_) {
              return false;
            }
          };
          const roots = [];
          const visitRoot = (root) => {
            roots.push(root);
            try {
              for (const el of Array.from(root.querySelectorAll('*'))) {
                if (el.shadowRoot) visitRoot(el.shadowRoot);
              }
            } catch (_) {}
          };
          visitRoot(document);
          for (const frame of Array.from(document.querySelectorAll('iframe'))) {
            try {
              if (frame.contentDocument) visitRoot(frame.contentDocument);
            } catch (_) {}
          }
          const selectors = [
            '#onetrust-accept-btn-handler',
            '#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll',
            '[data-testid="uc-accept-all-button"]',
            '[data-testid="cookie-accept-all"]',
            '[id*="accept"][id*="all"]',
            '[class*="accept"][class*="all"]'
          ];
          const textMatches = (text) => {
            const value = clean(text);
            return value.includes('aceptar todas') ||
              value.includes('aceptar todo') ||
              value.includes('permitir todas') ||
              value.includes('accept all') ||
              value.includes('allow all') ||
              value.includes('agree all');
          };
          for (const doc of roots) {
            for (const selector of selectors) {
              for (const el of Array.from(doc.querySelectorAll(selector))) {
                if (visible(el)) {
                  el.click();
                  return true;
                }
              }
            }
            const controls = Array.from(doc.querySelectorAll('button, a, input[type=button], input[type=submit], [role=button]'));
            for (const el of controls) {
              const text = el.value || el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '';
              if (visible(el) && textMatches(text)) {
                el.click();
                return true;
              }
            }
          }
          return false;
        })();
        """;
}
