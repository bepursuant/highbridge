using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Hosting;
using Highbridge.Jobs;
using Highbridge.Pcsc;
using Highbridge.Scanning;
using Highbridge.Server;
using Highbridge.Support;
using Highbridge.Twain;

namespace Highbridge.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private readonly WindowsFormsSynchronizationContext _ui;
    private readonly FileLoggerProvider _logProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly HighbridgeRuntime _runtime;
    private readonly WebApplication _server;
    private readonly SingleInstanceChannel _channel = new();
    private readonly NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly string _exePath = Environment.ProcessPath ?? Application.ExecutablePath;
    private ApprovedSitesForm? _sitesForm;
    private DevicesForm? _devicesForm;
    private bool _disposed;

    public TrayApplicationContext(AppOptions options)
    {
        _options = options;
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        _logProvider = new FileLoggerProvider(AppPaths.LogDirectory);
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logProvider).SetMinimumLevel(LogLevel.Information));
        _logger = _loggerFactory.CreateLogger("highbridge");

        IScannerBackend backend = options.UseMockScanner
            ? new MockScannerBackend()
            : new TwainScannerBackend(_loggerFactory.CreateLogger<TwainScannerBackend>());
        ICardReaderBackend cardReader = options.UseMockScanner
            ? new MockCardReaderBackend()
            : new PcscCardReaderBackend(_loggerFactory.CreateLogger<PcscCardReaderBackend>());
        if (!options.UseMockScanner)
        {
            _logger.LogInformation("{Environment}", TwainScannerBackend.DescribeEnvironment());
        }

        _runtime = new HighbridgeRuntime(
            backend,
            cardReader,
            new DialogConsentService(_ui),
            new SettingsStore(HighbridgePaths.UserSettingsFile),
            HighbridgePolicy.Load(HighbridgePaths.MachinePolicyFile),
            loggerFactory: _loggerFactory);
        _runtime.Jobs.JobFinished += job => _ui.Post(_ => OnJobFinished(job), null);

        _server = HighbridgeServer.Build(_runtime, b =>
        {
            b.Logging.ClearProviders();
            b.Logging.AddProvider(_logProvider);
            b.UseQuietLogging();
        });

        _trayIcon = AppIcon.CreateTrayIcon();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "highbridge",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowDevices();
        Application.ThreadException += (_, e) => _logger.LogError(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        EnsureProtocolRegistration();
        StartServer();
        LogStartup();

        _channel.Start(args =>
        {
            _ui.Post(_ => OnForwardedArguments(args), null);
            return Task.CompletedTask;
        });

        HandleUrls(options.Urls);
    }

    // WinForms disposes the context when the message loop ends, and Main's using disposes it again.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _logger.LogInformation("highbridge stopping");
            _tray.Visible = false;
            _tray.Dispose();
            _trayIcon.Dispose();
            Task.Run(() => _channel.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                Task.Run(() => _server.StopAsync(stopTimeout.Token)).GetAwaiter().GetResult();
            }

            _runtime.Dispose();
            (_runtime.Backend as IDisposable)?.Dispose();
            (_runtime.CardReader as IDisposable)?.Dispose();
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        string scanners = _options.UseMockScanner ? "mock scanner" : "TWAIN";
        string readers = _options.UseMockScanner ? "mock card reader" : "PC/SC";
        menu.Items.Add(new ToolStripMenuItem($"highbridge {HighbridgeServer.AppVersion} - 127.0.0.1:{_runtime.Port} ({scanners}, {readers})") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Devices...", null, (_, _) => ShowDevices());
        menu.Items.Add("Approved websites...", null, (_, _) => ShowApprovedSites());

        var autoStart = new ToolStripMenuItem("Start when I sign in") { Checked = WindowsRegistration.IsAutoStartEnabled(), CheckOnClick = true };
        autoStart.CheckedChanged += (_, _) => WindowsRegistration.SetAutoStart(autoStart.Checked, _exePath);
        menu.Items.Add(autoStart);

        menu.Items.Add("Open log folder", null, (_, _) => Process.Start(new ProcessStartInfo(_logProvider.LogDirectory) { UseShellExecute = true }));
        if (!_options.UseMockScanner)
        {
            menu.Items.Add("Diagnose scanner...", null, (_, _) => StartTool("--diagnose-scanner"));
            menu.Items.Add("Test scan...", null, (_, _) => StartTool("--test-scan"));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void StartTool(string tool)
    {
        _logger.LogInformation("Starting {Tool} from the tray menu", tool);
        try
        {
            Process.Start(new ProcessStartInfo(_exePath, tool) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogError(ex, "Could not start {Tool}", tool);
        }
    }

    private void EnsureProtocolRegistration()
    {
        try
        {
            if (!WindowsRegistration.IsProtocolRegistered(_exePath))
            {
                WindowsRegistration.RegisterProtocol(_exePath);
                _logger.LogInformation("Registered highbridge:// handler for {Path}", _exePath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not register the highbridge:// handler");
        }
    }

    private void StartServer()
    {
        try
        {
            Task.Run(() => _server.StartAsync()).GetAwaiter().GetResult();
            _logger.LogInformation("Listening on http://127.0.0.1:{Port}", _runtime.Port);
            if (!_options.Background && _options.Urls.Count == 0)
            {
                Notify("highbridge is running", "Websites you approve can now scan documents and read cards.", ToolTipIcon.Info);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not listen on port {Port}", _runtime.Port);
            Notify("highbridge could not start", $"Port {_runtime.Port} is already in use, maybe by highbridge in another signed-in Windows session. Close the other program, then restart highbridge.", ToolTipIcon.Error);
        }
    }

    private void OnForwardedArguments(string[] args)
    {
        if (args is [AppOptions.ExitCommand])
        {
            _logger.LogInformation("Exiting at the request of another highbridge process (installer)");
            ExitThread();
            return;
        }

        HandleUrls(args);
    }

    private void HandleUrls(IEnumerable<string> urls)
    {
        foreach (string url in urls)
        {
            var result = _runtime.HandleProtocolUrl(url);
            switch (result.Outcome)
            {
                case ProtocolOutcome.OriginNotPaired:
                    Notify("Request blocked", "A website asked to use a scanner or card reader but is not approved. Open the site and use its button to approve it first.", ToolTipIcon.Warning);
                    break;
                case ProtocolOutcome.Invalid:
                case ProtocolOutcome.Rejected:
                    Notify("Request ignored", result.Detail ?? "The highbridge link was not valid.", ToolTipIcon.Warning);
                    break;
            }
        }
    }

    private void OnJobFinished(ScanJob job)
    {
        if (job.Status == ScanJobStatus.Failed)
        {
            Notify("Scan failed", job.ErrorMessage ?? "The scanner reported an error.", ToolTipIcon.Error);
        }
    }

    private void LogStartup()
    {
        var process = Process.GetCurrentProcess();
        _logger.LogInformation(
            "highbridge {Version} started. pid={Pid} session={Session} bitness={Bits} machine={Machine} user={User} os={Os} runtime={Runtime} port={Port} scanners={Scanners} readers={Readers} cardReader={Policy} log={Log}",
            HighbridgeServer.AppVersion,
            process.Id,
            process.SessionId,
            Environment.Is64BitProcess ? "x64" : "x86",
            Environment.MachineName,
            Environment.UserName,
            Environment.OSVersion.VersionString,
            RuntimeInformation.FrameworkDescription,
            _runtime.Port,
            _runtime.Backend.Name,
            _runtime.CardReader.Name,
            _runtime.Policy.CardReaderEnabled ? "enabled" : "disabled",
            _logProvider.FilePath);
        _ = ListReadersAtStartup();
    }

    private async Task ListReadersAtStartup()
    {
        try
        {
            // Scanners stay unlisted until Devices is opened. Opening TWAIN at every sign-in can wake a driver.
            await ListDevicesAsync("Card readers at startup", listScanners: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list card readers at startup");
        }
    }

    private async Task<SupportSnapshot> ListDevicesAsync(string title, bool listScanners = true)
    {
        var snapshot = await DeviceInventory.CollectAsync(
            _runtime.Backend,
            _runtime.CardReader,
            HighbridgeServer.AppVersion,
            _runtime.Port,
            _runtime.Policy.CardReaderEnabled,
            listScanners: listScanners).ConfigureAwait(false);
        _logger.LogInformation("{Title}:{NewLine}{Report}", title, Environment.NewLine, SupportReport.Format(snapshot));
        return snapshot;
    }

    private Task<SupportSnapshot> LoadDevicesAsync() => ListDevicesAsync("Devices");

    private void ShowDevices()
    {
        if (_devicesForm is { IsDisposed: false })
        {
            _devicesForm.Activate();
            _devicesForm.RefreshDevices();
            return;
        }

        _devicesForm = new DevicesForm(LoadDevicesAsync, _logProvider.LogDirectory);
        _devicesForm.Show();
    }

    private void ShowApprovedSites()
    {
        if (_sitesForm is { IsDisposed: false })
        {
            _sitesForm.Activate();
            return;
        }

        _sitesForm = new ApprovedSitesForm(_runtime.Origins);
        _sitesForm.Show();
    }

    private void Notify(string title, string text, ToolTipIcon icon) => _tray.ShowBalloonTip(5000, title, text, icon);
}
