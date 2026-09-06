using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HumanToolCall;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly BackendService _backend;
    private readonly TunnelSupervisor _tunnel;
    private readonly InteractionBroker _broker;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _backendStateItem;
    private readonly ToolStripMenuItem _tunnelStateItem;
    private readonly ToolStripMenuItem _pendingItem;
    private readonly ToolStripMenuItem _openUiItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startBackendItem;
    private readonly ToolStripMenuItem _stopBackendItem;
    private readonly ToolStripMenuItem _startTunnelItem;
    private readonly ToolStripMenuItem _stopTunnelItem;
    private readonly ToolStripMenuItem _closeItem;
    private bool _busy;
    private bool _closing;
    private long _actionsCompleted;

    internal TrayApplicationContext(
        AppConfig config,
        BackendService backend,
        TunnelSupervisor tunnel,
        InteractionBroker broker)
    {
        _config = config;
        _backend = backend;
        _tunnel = tunnel;
        _broker = broker;

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _backendStateItem = Disabled("Backend: Off");
        _tunnelStateItem = Disabled("Tunnel: Off");
        _pendingItem = Disabled("Pending: 0");
        _menu.Items.AddRange([_backendStateItem, _tunnelStateItem, _pendingItem, new ToolStripSeparator()]);

        _openUiItem = new ToolStripMenuItem("Open UI");
        _openUiItem.Click += OpenUiItem_Click;
        _statusItem = new ToolStripMenuItem("Status");
        _statusItem.Click += StatusItem_Click;
        _menu.Items.AddRange([_openUiItem, _statusItem, new ToolStripSeparator()]);

        _startBackendItem = new ToolStripMenuItem("Start Backend");
        _startBackendItem.Click += StartBackendItem_Click;
        _stopBackendItem = new ToolStripMenuItem("Stop Backend");
        _stopBackendItem.Click += StopBackendItem_Click;
        _startTunnelItem = new ToolStripMenuItem("Start Tunnel");
        _startTunnelItem.Click += StartTunnelItem_Click;
        _stopTunnelItem = new ToolStripMenuItem("Stop Tunnel");
        _stopTunnelItem.Click += StopTunnelItem_Click;
        _menu.Items.AddRange([
            _startBackendItem,
            _stopBackendItem,
            _startTunnelItem,
            _stopTunnelItem,
            new ToolStripSeparator()
        ]);

        _closeItem = new ToolStripMenuItem("Close | 0");
        _closeItem.Click += CloseItem_Click;
        _menu.Items.Add(_closeItem);

        // Force a WinForms handle on the UI thread so broker callbacks can marshal safely.
        _ = _menu.Handle;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HumanToolCall",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _trayIcon.DoubleClick += OpenUiItem_Click;

        _broker.interactionAdded += brokerInteractionAdded;
        _broker.progressAdded += brokerProgressAdded;

        Publish(null);
        _ = RefreshStatusAsync(countAction: false);
    }

    internal void OpenBrowser()
    {
        if (!_backend.IsRunning)
        {
            ShowMessage("Backend is off", "Start the backend before opening the browser UI.", ToolTipIcon.Info);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _backend.BrowserUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowMessage("Could not open browser", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenUiItem_Click(object? sender, EventArgs e) => OpenBrowser();

    private async void StatusItem_Click(object? sender, EventArgs e) =>
        await RefreshStatusAsync(countAction: true);

    private async void StartBackendItem_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            await _backend.StartAsync();
            return await Task.Run(() => _tunnel.Status());
        });
    }

    private async void StopBackendItem_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            await _backend.StopAsync();
            return await Task.Run(() => _tunnel.Status());
        });
    }

    private async void StartTunnelItem_Click(object? sender, EventArgs e)
    {
        if (!_backend.IsRunning)
        {
            ShowMessage("Backend is off",
                "Start Backend first. The configured tunnel forwards to this local MCP server.", ToolTipIcon.Info);
            return;
        }

        await RunOperationAsync(() => Task.Run(() => _tunnel.Start()));
    }

    private async void StopTunnelItem_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(() => Task.Run(() => _tunnel.Stop()));

    private async void CloseItem_Click(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        SetBusy(true);

        try
        {
            if (_config.StopTunnelOnExit)
            {
                await Task.Run(() => _tunnel.Stop());
            }

            await _backend.StopAsync();
        }
        catch
        {
            // Exit still proceeds; external tunnel state can be reconciled on next launch.
        }
        finally
        {
            ExitThread();
        }
    }

    private async Task RunOperationAsync(Func<Task<TunnelStatus>> operation)
    {
        if (_busy || _closing)
        {
            return;
        }

        SetBusy(true);
        TunnelStatus? status = null;
        try
        {
            status = await operation();
        }
        catch (Exception ex)
        {
            status = new TunnelStatus { State = TunnelState.Error, Details = ex.Message };
            ShowMessage("HumanToolCall", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            IncrementActions();
            SetBusy(false);
            Publish(status);
        }
    }

    private async Task RefreshStatusAsync(bool countAction)
    {
        if (_busy || _closing)
        {
            return;
        }

        SetBusy(true);
        TunnelStatus? status = null;
        try
        {
            status = await Task.Run(() => _tunnel.Status());
        }
        catch (Exception ex)
        {
            status = new TunnelStatus { State = TunnelState.Error, Details = ex.Message };
        }
        finally
        {
            if (countAction)
            {
                IncrementActions();
            }

            SetBusy(false);
            Publish(status);
        }
    }

    private void brokerInteractionAdded(PendingInteractionView interaction)
    {
        DispatchToUi(() =>
        {
            Publish(null);
            if (_config.NotifyOnQuestions)
            {
                string title = interaction.kind == "choosePath" ? "ChatGPT needs a decision" : "ChatGPT has a question";
                ShowMessage(title, "Open HumanToolCall to respond.", ToolTipIcon.Info);
            }
        });
    }

    private void brokerProgressAdded(ProgressReportView report)
    {
        if (!_config.NotifyOnProgressReports)
        {
            return;
        }

        DispatchToUi(() => ShowMessage("ChatGPT progress", report.summary, ToolTipIcon.Info));
    }

    private void DispatchToUi(Action action)
    {
        try
        {
            if (_menu.IsDisposed) return;
            if (_menu.InvokeRequired) _menu.BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Publish(TunnelStatus? tunnelStatus)
    {
        _backendStateItem.Text = "Backend: " + (_backend.IsRunning ? "On" : "Off");
        if (tunnelStatus is not null)
        {
            _tunnelStateItem.Text = "Tunnel: " + tunnelStatus.State;
            _tunnelStateItem.ToolTipText = tunnelStatus.Details;
        }

        _pendingItem.Text = "Pending: " + _broker.PendingCount;
        _openUiItem.Enabled = _backend.IsRunning && !_busy;
        _startBackendItem.Enabled = !_backend.IsRunning && !_busy;
        _stopBackendItem.Enabled = _backend.IsRunning && !_busy;
        _startTunnelItem.Enabled = _backend.IsRunning && !_busy;
        _stopTunnelItem.Enabled = !_busy;
        _statusItem.Enabled = !_busy;

        string tooltip =
            $"HumanToolCall - Backend {(_backend.IsRunning ? "On" : "Off")} - {_tunnelStateItem.Text} - Pending {_broker.PendingCount}";
        _trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor.Current = busy ? Cursors.WaitCursor : Cursors.Default;
        Publish(null);
    }

    private void IncrementActions()
    {
        if (_actionsCompleted < long.MaxValue) _actionsCompleted++;
        _closeItem.Text = "Close | " + _actionsCompleted;
    }

    private void ShowMessage(string title, string text, ToolTipIcon icon)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = text.Length <= 240 ? text : text[..240];
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(4000);
    }

    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    protected override void ExitThreadCore()
    {
        _broker.interactionAdded -= brokerInteractionAdded;
        _broker.progressAdded -= brokerProgressAdded;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}