using Microsoft.Web.WebView2.Core;

namespace Quickcamp;

public sealed class LoginWindow : Form
{
    private const string LoginUrl = "https://bandcamp.com/login";
    private readonly Panel _host = new() { Dock = DockStyle.Fill };
    private CoreWebView2Controller? _browser;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private readonly string _dataDir;
    private readonly Func<string, Task<bool>> _isValid;
    private static CoreWebView2Environment? _env;
    private static bool _loaderPathSet;
    private string? _lastTried;
    private bool _checking;
    public string? Identity { get; private set; }
    public LoginWindow(string dataDir, Func<string, Task<bool>> isValid)
    {
        _dataDir = dataDir;
        _isValid = isValid;

        Text = "Log in to Bandcamp";
        Icon = Assets.AppIcon;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Controls.Add(_host);
        _host.Resize += (_, _) => { if (_browser is not null) _browser.Bounds = _host.ClientRectangle; };
        Move += (_, _) => _browser?.NotifyParentWindowPositionChanged();

        _poll.Tick += async (_, _) => await CheckForLoginAsync();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SizeToFitScreen();
        try
        {
            DeleteFolder(_dataDir);
            if (!_loaderPathSet)
            {
                var loaderFolder = Assets.ExtractWebView2Loader(Path.GetDirectoryName(_dataDir)!);
                CoreWebView2Environment.SetLoaderDllFolderPath(loaderFolder);
                _loaderPathSet = true;
            }
            _env = await CoreWebView2Environment.CreateAsync(null, _dataDir);
            var dataDir = _dataDir;
            _env.BrowserProcessExited += (_, _) => DeleteFolder(dataDir);
            _browser = await _env.CreateCoreWebView2ControllerAsync(_host.Handle);
            _browser.Bounds = _host.ClientRectangle;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "The login window needs the Microsoft Edge WebView2 Runtime, which isn't installed.\n\n" +
                "Get it from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.",
                "Quickcamp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        var core = _browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            core.Navigate(args.Uri);
        };
        core.NavigationCompleted += async (_, _) =>
        {
            try { await core.ExecuteScriptAsync(DismissCookieBannerScript); } catch { /* page went away */ }
            await CheckForLoginAsync();
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(DismissCookieBannerScript);

        core.Navigate(LoginUrl);
        _browser.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        _poll.Start();
    }
    private void SizeToFitScreen()
    {
        var screen = (Owner is null ? Screen.PrimaryScreen! : Screen.FromControl(Owner)).WorkingArea;
        int width = Math.Max(1150, LogicalToDeviceUnits(760));
        int height = Math.Max(960, LogicalToDeviceUnits(640));
        Size = new Size(Math.Min(width, screen.Width - 40), Math.Min(height, screen.Height - 40));
        MinimumSize = Size;
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
    }

    private const string DismissCookieBannerScript = """
        (() => {
          if (window.__qcBannerWatch) return;
          window.__qcBannerWatch = true;
          const wanted = /^\s*(accept necessary only|necessary only|reject all)\s*$/i;

          const findIn = (root) => {
            let best = null;
            for (const el of root.querySelectorAll('*')) {
              if (el.shadowRoot) { const hit = findIn(el.shadowRoot); if (hit) return hit; }
              const text = el.innerText ?? el.textContent ?? '';
              if (text.length < 40 && wanted.test(text)) best = el;
            }
            return best;
          };

          const press = (el) => {
            const r = el.getBoundingClientRect();
            const opts = { bubbles: true, cancelable: true, view: window, clientX: r.x + r.width / 2, clientY: r.y + r.height / 2, button: 0 };
            for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup'])
              el.dispatchEvent(new (type.startsWith('pointer') ? PointerEvent : MouseEvent)(type, opts));
            el.click();
          };

          const tryClick = () => {
            const el = findIn(document);
            if (!el || el.getBoundingClientRect().width === 0) return false;
            press(el);
            return true;
          };

          const start = () => {
            if (tryClick()) return;
            const watcher = new MutationObserver(() => { if (tryClick()) watcher.disconnect(); });
            watcher.observe(document.documentElement, { childList: true, subtree: true, attributes: true });
            const poll = setInterval(() => { if (tryClick()) { clearInterval(poll); watcher.disconnect(); } }, 500);
            setTimeout(() => { clearInterval(poll); watcher.disconnect(); }, 20000);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
          else start();
        })();
        """;

    private async Task CheckForLoginAsync()
    {
        if (_checking || Identity is not null || _browser is null) return;
        _checking = true;
        try
        {
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync("https://bandcamp.com");
            var identity = cookies.FirstOrDefault(c => c.Name == "identity")?.Value;

            if (!string.IsNullOrEmpty(identity) && identity != _lastTried)
            {
                _lastTried = identity;
                if (await _isValid(identity))
                {
                    Identity = identity;
                    _poll.Stop();
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
        }
        catch
        {}
        finally
        {
            _checking = false;
        }
    }
    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch
        {}
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _browser?.Close();
        }
        base.Dispose(disposing);
    }
}
