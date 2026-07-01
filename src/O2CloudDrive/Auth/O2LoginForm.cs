using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using O2CloudDrive.Ui;

namespace O2CloudDrive.Auth;

public sealed class O2LoginForm : Form
{
    private static readonly Regex ValidationKeyRegex = new(
        "validationkey[^A-Za-z0-9._-]+([A-Za-z0-9._-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Uri _loginUri;
    private readonly O2SessionValidator _validator;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Text = "Cargando login oficial de O2 Cloud...",
    };

    private readonly System.Windows.Forms.Timer _captureTimer = new() { Interval = 2000 };
    private readonly HashSet<string> _seenUrls = new(StringComparer.OrdinalIgnoreCase);
    private bool _capturing;
    private string? _lastValidationKey;

    public O2LoginForm(Uri loginUri, O2SessionValidator validator)
    {
        _loginUri = loginUri;
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
            await CaptureAndValidateAsync();
        };
        Shown += async (_, _) => await InitializeWebViewAsync();
        FormClosed += (_, _) =>
        {
            _captureTimer.Stop();
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
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
            _webView.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LoginBootstrapScript);
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _webView.CoreWebView2.Navigate(args.Uri);
            };
            _webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                TrackUrl(args.Uri);
                _statusLabel.Text = "Login oficial de O2 Cloud cargando...";
                if (TryExtractValidationKey(args.Uri, out var validationKey))
                {
                    _lastValidationKey = validationKey;
                    BeginInvoke(async () => await CaptureAndValidateAsync(validationKey));
                }
            };
            _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                _statusLabel.Text = "Esperando telefono, contrasena y codigo SMS de O2 Cloud...";
                await AcceptCookieConsentAsync();
                await CaptureAndValidateAsync();
                if (CapturedSession is null)
                {
                    _statusLabel.Text = "Introduce telefono, contrasena y SMS. La app capturara la sesion cuando O2 la valide.";
                }
            };
            _webView.CoreWebView2.DOMContentLoaded += async (_, _) =>
            {
                await AcceptCookieConsentAsync();
                await CaptureAndValidateAsync();
            };

            _captureTimer.Start();
            _statusLabel.Text = "Abriendo login oficial de O2 Cloud...";
            _webView.CoreWebView2.Navigate(_loginUri.ToString());
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "No se pudo inicializar WebView2.";
            MessageBox.Show(this, ex.Message, "O2 Cloud Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task AcceptCookieConsentAsync()
    {
        if (_webView.CoreWebView2 is null)
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

    private async Task CaptureAndValidateAsync(string? forcedValidationKey = null)
    {
        if (_capturing || _webView.CoreWebView2 is null)
        {
            return;
        }

        _capturing = true;
        try
        {
            var session = await CaptureSessionAsync(forcedValidationKey);
            if (session is not { IsAuthenticated: true })
            {
                return;
            }

            _statusLabel.Text = "Sesion detectada. Validando con O2 Cloud...";
            var valid = await Task.Run(() => _validator.Validate(session));
            if (!valid)
            {
                _statusLabel.Text = "Sesion detectada, esperando a que O2 termine de validarla...";
                return;
            }

            CapturedSession = session;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            _statusLabel.Text = "Esperando a que O2 Cloud complete el login...";
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task<O2Session?> CaptureSessionAsync(string? forcedValidationKey)
    {
        var state = await ReadBrowserStateAsync();
        var serializedState = state?.GetRawText() ?? string.Empty;
        var validationKey = FirstNonEmpty(
            forcedValidationKey,
            _lastValidationKey,
            ReadString(state, "validationKey"),
            ExtractValidationKey(serializedState));

        if (string.IsNullOrWhiteSpace(validationKey))
        {
            return null;
        }

        var cookies = await ReadCookiesAsync(ReadString(state, "cookie"));
        var userAgent = ReadString(state, "userAgent") ?? string.Empty;

        return new O2Session
        {
            ValidationKey = validationKey,
            Cookies = cookies,
            UserAgent = userAgent,
        };
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

    private async Task<Dictionary<string, string>> ReadCookiesAsync(string? documentCookie)
    {
        var cookies = ParseCookieHeader(documentCookie);
        var webViewCookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.o2online.es/");
        foreach (var cookie in webViewCookies)
        {
            if (!cookie.Domain.Contains("o2online.es", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value))
            {
                cookies[cookie.Name] = cookie.Value;
            }
        }

        return cookies;
    }

    private static Dictionary<string, string> ParseCookieHeader(string? cookieHeader)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return cookies;
        }

        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var name = part[..index].Trim();
            var value = part[(index + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
            {
                cookies[name] = value;
            }
        }

        return cookies;
    }

    private void TrackUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        _seenUrls.Add(uri);
        if (TryExtractValidationKey(uri, out var validationKey))
        {
            _lastValidationKey = validationKey;
        }
    }

    private static bool TryExtractValidationKey(string text, out string validationKey)
    {
        validationKey = ExtractValidationKey(text) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(validationKey);
    }

    private static string? ExtractValidationKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ValidationKeyRegex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

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
          const resources = [];
          try {
            for (const entry of performance.getEntriesByType('resource')) {
              if (entry && entry.name) resources.push(entry.name);
            }
          } catch (_) {}
          const payload = {
            url: location.href,
            cookie: document.cookie || '',
            userAgent: navigator.userAgent || '',
            localStorage: readStorage(localStorage),
            sessionStorage: readStorage(sessionStorage),
            seenUrls: Array.isArray(window.__o2CloudDriveSeenUrls) ? window.__o2CloudDriveSeenUrls.slice(-100) : [],
            resources: resources.slice(-100)
          };
          const text = JSON.stringify(payload);
          const match = text.match(/validationkey[^A-Za-z0-9._-]+([A-Za-z0-9._-]{8,})/i);
          payload.validationKey = match ? match[1] : '';
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
