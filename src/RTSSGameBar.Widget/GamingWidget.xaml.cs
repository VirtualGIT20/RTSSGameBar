using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using RTSSGameBar.Protocol;
using RTSSGameBar.Widget.Ipc;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RTSSGameBar.Widget
{
    public sealed partial class GamingWidget : Page
    {
        private readonly PipeClient _pipeClient = new PipeClient();
        private readonly DispatcherTimer _refreshTimer;
        private static readonly TimeSpan VisibleRefreshInterval = TimeSpan.FromSeconds(5);

        private HelperLauncher _helperLauncher;
        private XboxGameBarWidget _widget;
        private RtssStatus _status;
        private bool _refreshing;
        private int _pendingCommands;
        private int _frameCommitGeneration;
        private int _zoomCommitGeneration;
        private bool _frameCommitPending;
        private bool _frameWriterRunning;
        private bool _zoomCommitPending;
        private bool _zoomWriterRunning;
        private int _committedFrameLimit = 60;
        private int _desiredFrameLimit = 60;
        private int _committedOsdZoom = 1;
        private int _desiredOsdZoom = 1;
        private bool _widgetVisibilityHooked;
        private bool _widgetOpacityHooked;
        private bool _widgetThemeHooked;
        private bool _coreWindowVisibilityHooked;
        private string _lastRenderedStatusKey;
        private bool _syncingControls;

        private static readonly SolidColorBrush LedGreen = new SolidColorBrush(Color.FromArgb(255, 52, 199, 89));
        private static readonly SolidColorBrush LedAmber = new SolidColorBrush(Color.FromArgb(255, 255, 185, 0));
        private static readonly SolidColorBrush LedRed = new SolidColorBrush(Color.FromArgb(255, 255, 69, 58));
        private static readonly SolidColorBrush LedGray = new SolidColorBrush(Color.FromArgb(255, 120, 120, 120));

        public GamingWidget()
        {
            InitializeComponent();

            _helperLauncher = new HelperLauncher(_pipeClient);

            // Keep Game Bar / UWP input and B/Back handling native. Only XYFocus neighbors are
            // assigned, and only between controls that are currently enabled. This keeps the normal
            // column deterministic while preserving the direct path to Integration when RTSS
            // controls are disabled by Install/UpdateRequired state.

            _refreshTimer = new DispatcherTimer { Interval = VisibleRefreshInterval };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += GamingWidget_Loaded;
            Unloaded += GamingWidget_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            ApplyRequestedWidgetAppearance();
            base.OnNavigatedTo(e);
        }

        private async void GamingWidget_Loaded(object sender, RoutedEventArgs e)
        {
            HookWindowVisibility();
            HookWidgetAppearance();
            DetailText.Text = "Starting local bridge…";

            // Yield once so the lightweight shell can paint before helper discovery begins.
            await Task.Delay(1);

            var launch = await _helperLauncher.EnsureRunningAsync();
            if (!launch.Success)
            {
                SetError(launch.ToString());
                ApplyStatus(null);
                return;
            }

            await RefreshStatusAsync();

            ConfigureRefreshTimerForCurrentVisibility();
        }

        private void GamingWidget_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            UnhookWindowVisibility();
            UnhookWidgetAppearance();
            _pipeClient.Disconnect();
        }

        private async void RefreshTimer_Tick(object sender, object e)
        {
            // Visibility events normally stop the timer. Re-check here as a cheap race/fallback
            // guard so a queued tick can never turn into hidden RTSS polling.
            if (!IsCurrentlyVisible())
            {
                _refreshTimer.Stop();
                return;
            }

            if (_pendingCommands == 0 && !_frameCommitPending && !_frameWriterRunning && !_zoomCommitPending && !_zoomWriterRunning)
                await RefreshStatusAsync(false);
        }

        private async Task<bool> RefreshStatusAsync(bool showErrors = true)
        {
            if (_refreshing || _pendingCommands > 0 || _frameCommitPending || _frameWriterRunning || _zoomCommitPending || _zoomWriterRunning)
                return false;

            _refreshing = true;
            try
            {
                var response = await _pipeClient.SendAsync(new RtssRequest { Command = RtssCommand.GetStatus }, 2600);
                if (!response.Success)
                    throw new InvalidOperationException(response.ErrorMessage ?? response.ErrorCode ?? "Status request failed.");

                _status = response.Status;
                CaptureCommittedValues(_status);
                ApplyStatus(_status);
                if (showErrors)
                    SetError(null);
                return true;
            }
            catch (Exception ex)
            {
                _pipeClient.Disconnect();
                var recovered = await _helperLauncher.EnsureRunningAsync();
                if (recovered.Success)
                {
                    try
                    {
                        var retry = await _pipeClient.SendAsync(new RtssRequest { Command = RtssCommand.GetStatus }, 2600);
                        if (retry.Success)
                        {
                            _status = retry.Status;
                            CaptureCommittedValues(_status);
                            ApplyStatus(_status);
                            if (showErrors)
                                SetError(null);
                            return true;
                        }
                    }
                    catch { }
                }

                ApplyStatus(null);
                if (showErrors)
                    SetError(ex.Message);
                return false;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void CaptureCommittedValues(RtssStatus status)
        {
            if (status?.FrameLimit != null)
            {
                _committedFrameLimit = status.FrameLimit.Value;
                if (!_frameCommitPending && !_frameWriterRunning)
                    _desiredFrameLimit = _committedFrameLimit;
            }
            if (status?.OsdZoom != null)
            {
                _committedOsdZoom = status.OsdZoom.Value;
                if (!_zoomCommitPending && !_zoomWriterRunning)
                    _desiredOsdZoom = _committedOsdZoom;
            }
        }

        private void ApplyStatus(RtssStatus status)
        {
            var renderKey = BuildRenderStatusKey(status);
            if (string.Equals(renderKey, _lastRenderedStatusKey, StringComparison.Ordinal))
                return;
            _lastRenderedStatusKey = renderKey;

            if (status == null)
            {
                RtssStatusLed.Fill = LedGray;
                IntegrationStatusLed.Fill = LedGray;
                RtssStateText.Text = "Unavailable";
                IntegrationStateText.Text = "Unknown";
                DetailText.Text = "The local helper is not connected.";
                IntegrationVersionText.Text = string.Empty;

                _syncingControls = true;
                try
                {
                    FrameLimitSlider.Value = 0;
                    FrameLimitValueText.Text = "--";
                    FrameLimitPresetComboBox.SelectedIndex = -1;
                    LimiterTypeComboBox.SelectedIndex = -1;
                    LimiterEnabledToggle.IsOn = false;
                    OverlayToggle.IsOn = false;
                    OsdZoomSlider.Value = 1;
                    OsdZoomValueText.Text = "--";
                    OsdPositionComboBox.SelectedIndex = -1;
                }
                finally
                {
                    _syncingControls = false;
                }

                SetControlAvailability(false);
                RtssActionButton.IsEnabled = false;
                IntegrationActionButton.IsEnabled = false;
                UpdateFocusGraph();
                return;
            }

            RtssStateText.Text = !status.Installed ? "Not installed" : !status.Running ? "Stopped" : "Running";
            IntegrationStateText.Text = IntegrationStateDisplayName(status.IntegrationState);
            RtssStatusLed.Fill = !status.Installed ? LedRed : status.Running ? LedGreen : LedGray;
            IntegrationStatusLed.Fill = IntegrationLedBrush(status.IntegrationState);
            DetailText.Text = status.Detail ?? string.Empty;

            _syncingControls = true;
            try
            {
                if (status.FrameLimit.HasValue)
                {
                    var frameLimit = Math.Max(0, Math.Min(1000, status.FrameLimit.Value));
                    FrameLimitSlider.Value = frameLimit;
                    FrameLimitValueText.Text = frameLimit == 0 ? "Unlimited" : frameLimit + " FPS";
                    FrameLimitPresetComboBox.SelectedIndex = PresetIndexForFrameLimit(frameLimit);
                }
                else
                {
                    FrameLimitValueText.Text = "--";
                    FrameLimitPresetComboBox.SelectedIndex = -1;
                }

                LimiterTypeComboBox.SelectedIndex = status.LimiterType.HasValue
                    ? Math.Max(-1, Math.Min(3, (int)status.LimiterType.Value))
                    : -1;
                LimiterEnabledToggle.IsOn = status.LimiterEnabled == true;
                OverlayToggle.IsOn = status.OverlayVisible == true;

                if (status.OsdZoom.HasValue)
                {
                    var zoom = Math.Max(1, Math.Min(8, status.OsdZoom.Value));
                    OsdZoomSlider.Value = zoom;
                    OsdZoomValueText.Text = zoom + "×";
                }
                else
                {
                    OsdZoomValueText.Text = "--";
                }

                OsdPositionComboBox.SelectedIndex = PositionComboIndex(status.OsdPosition);
            }
            finally
            {
                _syncingControls = false;
            }

            var installedVersion = string.IsNullOrWhiteSpace(status.PluginVersion) ? "unknown" : status.PluginVersion;
            var bundledVersion = string.IsNullOrWhiteSpace(status.BundledPluginVersion) ? ProtocolConstants.BundledPluginVersion : status.BundledPluginVersion;
            IntegrationVersionText.Text = status.PluginInstalled
                ? "Installed: " + installedVersion + " · Bundled: " + bundledVersion
                : "Bundled plugin: " + bundledVersion;

            var controllable = status.Running && status.PluginConnected && !status.PluginUpdateAvailable;
            SetControlAvailability(controllable);

            RtssActionButton.Content = status.Running ? "Close" : "Start";
            var integrationActionRequired = status.IntegrationState == RtssIntegrationState.NotInstalled
                || status.IntegrationState == RtssIntegrationState.UpdateRequired
                || status.IntegrationState == RtssIntegrationState.Incompatible;
            RtssActionButton.IsEnabled = status.Installed
                && !integrationActionRequired
                && (!status.Running || controllable);

            switch (status.IntegrationState)
            {
                case RtssIntegrationState.NotInstalled:
                    IntegrationActionButton.Content = "Install";
                    IntegrationActionButton.IsEnabled = true;
                    break;
                case RtssIntegrationState.UpdateRequired:
                case RtssIntegrationState.Incompatible:
                    IntegrationActionButton.Content = "Update";
                    IntegrationActionButton.IsEnabled = true;
                    break;
                case RtssIntegrationState.Connected:
                case RtssIntegrationState.Disabled:
                case RtssIntegrationState.RtssStopped:
                case RtssIntegrationState.Error:
                    IntegrationActionButton.Content = "Remove";
                    IntegrationActionButton.IsEnabled = status.PluginInstalled;
                    break;
                default:
                    IntegrationActionButton.Content = "Unavailable";
                    IntegrationActionButton.IsEnabled = false;
                    break;
            }

            UpdateFocusGraph();
        }

        private static int PresetIndexForFrameLimit(int value)
        {
            switch (value)
            {
                case 0: return 0;
                case 30: return 1;
                case 40: return 2;
                case 60: return 3;
                case 90: return 4;
                case 120: return 5;
                case 144: return 6;
                case 165: return 7;
                case 240: return 8;
                case 360: return 9;
                default: return -1;
            }
        }

        private static string BuildRenderStatusKey(RtssStatus status)
        {
            if (status == null)
                return "<null>";

            return string.Join("|",
                status.Installed,
                status.Running,
                status.PluginInstalled,
                status.PluginConnected,
                status.PluginUpdateAvailable,
                status.IntegrationState,
                status.FrameLimit,
                status.LimiterType,
                status.LimiterEnabled,
                status.OverlayVisible,
                status.OsdZoom,
                status.OsdPosition,
                status.PluginVersion ?? string.Empty,
                status.BundledPluginVersion ?? string.Empty,
                status.Detail ?? string.Empty);
        }

        private void SetControlAvailability(bool controllable)
        {
            FrameLimitSlider.IsEnabled = controllable;
            FrameLimitPresetComboBox.IsEnabled = controllable;
            LimiterTypeComboBox.IsEnabled = controllable;
            LimiterEnabledToggle.IsEnabled = controllable;
            OverlayToggle.IsEnabled = controllable;
            OsdZoomSlider.IsEnabled = controllable;
            OsdPositionComboBox.IsEnabled = controllable;
        }

        private static int PositionComboIndex(RtssOsdPosition? position)
        {
            if (!position.HasValue)
                return -1;

            switch (position.Value)
            {
                case RtssOsdPosition.TopLeft: return 0;
                case RtssOsdPosition.TopCenter: return 1;
                case RtssOsdPosition.TopRight: return 2;
                case RtssOsdPosition.MiddleLeft: return 3;
                case RtssOsdPosition.MiddleRight: return 4;
                case RtssOsdPosition.BottomLeft: return 5;
                case RtssOsdPosition.BottomCenter: return 6;
                case RtssOsdPosition.BottomRight: return 7;
                default: return -1;
            }
        }

        private void UpdateFocusGraph()
        {
            // Never redirect focus in code. We only update XYFocus neighbors when status changes.
            // When Install/Update is the blocking action, Refresh stays mouse/touch-clickable but is
            // removed from controller/tab focus. Down is anchored on Integration to neutralize a
            // duplicate ingress Down; Up stays native so the user can exit the widget normally.
            var integrationActionBlocking = _status != null
                && IntegrationActionButton.IsEnabled
                && (_status.IntegrationState == RtssIntegrationState.NotInstalled
                    || _status.IntegrationState == RtssIntegrationState.UpdateRequired
                    || _status.IntegrationState == RtssIntegrationState.Incompatible);

            RefreshButton.IsTabStop = !integrationActionBlocking;

            var ordered = new Control[]
            {
                FrameLimitSlider,
                FrameLimitPresetComboBox,
                LimiterTypeComboBox,
                LimiterEnabledToggle,
                OverlayToggle,
                OsdZoomSlider,
                OsdPositionComboBox,
                RtssActionButton,
                IntegrationActionButton,
                RefreshButton
            };

            var enabled = new List<Control>();
            foreach (var control in ordered)
            {
                control.XYFocusUp = null;
                control.XYFocusDown = null;
                if (control.IsEnabled && control.Visibility == Visibility.Visible
                    && (!integrationActionBlocking || control != RefreshButton))
                {
                    enabled.Add(control);
                }
            }

            if (integrationActionBlocking)
            {
                // Keep Down anchored on the blocking action so a duplicate ingress Down cannot
                // advance to Refresh. Leave Up unset so native Game Bar navigation can exit the
                // widget normally instead of trapping focus on Install/Update.
                IntegrationActionButton.XYFocusDown = IntegrationActionButton;
                return;
            }

            for (var i = 0; i < enabled.Count; i++)
            {
                enabled[i].XYFocusUp = i > 0 ? enabled[i - 1] : null;
                enabled[i].XYFocusDown = i + 1 < enabled.Count ? enabled[i + 1] : null;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshStatusAsync();
        }

        private async void RtssActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_status?.Installed != true)
                return;

            if (_status.Running)
            {
                await ExecuteCommandAsync(new RtssRequest { Command = RtssCommand.StopRtss }, null, "Closing RTSS…", false);
                await Task.Delay(350);
                await RefreshStatusAsync(false);
                return;
            }

            var minimized = await TryMinimizeWidgetAsync();
            try
            {
                await ExecuteCommandAsync(new RtssRequest { Command = RtssCommand.StartRtss }, null, "Starting RTSS…", false, 15000);
                await Task.Delay(900);
            }
            finally
            {
                if (minimized)
                    await TryRestoreWidgetAsync();
                await RefreshStatusAsync(false);
            }
        }

        private async void IntegrationActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_status == null)
                return;

            RtssCommand command;
            string title;
            string primary;
            string operation;
            string content;

            if (_status.IntegrationState == RtssIntegrationState.NotInstalled)
            {
                command = RtssCommand.InstallIntegration;
                title = "Install RTSS integration?";
                primary = "Continue";
                operation = "Waiting for Windows approval to install the RTSS integration…";
                content = "Windows will ask for administrator approval while this widget remains open. The setup copies only RTSSGameBarPlugin.dll into RTSS\\Plugins\\Client. RTSS is closed for the file operation; if another application immediately respawns RTSS during UAC, that raced instance may be terminated so the approved install can finish. No ACL, service or scheduled task is created.";
            }
            else if (_status.IntegrationState == RtssIntegrationState.UpdateRequired || _status.IntegrationState == RtssIntegrationState.Incompatible)
            {
                command = RtssCommand.UpdateIntegration;
                title = "Update RTSS integration?";
                primary = "Continue";
                operation = "Waiting for Windows approval to update the RTSS integration…";
                content = "Windows will ask for administrator approval while this widget remains open. The setup replaces only RTSSGameBarPlugin.dll. RTSS is closed for the file operation; if another application immediately respawns RTSS during UAC, that raced instance may be terminated so the approved update can finish.";
            }
            else if (_status.PluginInstalled)
            {
                command = RtssCommand.RemoveIntegration;
                title = "Remove RTSS integration?";
                primary = "Continue";
                operation = "Waiting for Windows approval to remove the RTSS integration…";
                content = "Windows will ask for administrator approval while this widget remains open. The setup removes only RTSSGameBarPlugin.dll. RTSS is closed for the file operation; if another application immediately respawns RTSS during UAC, that raced instance may be terminated so removal can finish. It does not modify RTSS profiles, ACLs, services or scheduled tasks.";
            }
            else
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primary,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var wasRunning = _status.Running;

            // When the integration plugin is connected, close RTSS gracefully before elevation.
            // If the plugin is unavailable (clean install, removed integration, incompatible/update
            // state), do not spend ~6 seconds on the non-elevated window-close fallback: the
            // approved elevated maintenance phase already re-checks and closes/race-proofs RTSS.
            if (wasRunning && _status.PluginConnected)
            {
                OperationText.Text = "Preparing RTSS…";
                try
                {
                    var prepare = await _pipeClient.SendAsync(new RtssRequest { Command = RtssCommand.StopRtss }, 5000);
                    if (prepare.Success)
                        await Task.Delay(250);
                }
                catch
                {
                    // Continue to elevation. The elevated maintenance phase re-checks RTSS
                    // after UAC and handles an Afterburner/race respawn before touching the DLL.
                }
                SetError(null);
            }

            var success = false;
            string operationError = null;
            try
            {
                // Keep the widget open. The helper captures Game Bar's foreground HWND and
                // supplies it to ShellExecuteEx so Windows can correctly own/position UAC.
                success = await ExecuteCommandAsync(new RtssRequest { Command = command }, null, operation, false, 95000);
                if (!success)
                    operationError = ErrorText.Text;
            }
            finally
            {
                // Restore the pre-operation RTSS state even when UAC is cancelled or the setup
                // reports a failure. Stopping RTSS is preparatory work and must never leave a
                // previously-running RTSS instance off merely because the file operation failed.
                if (wasRunning)
                {
                    OperationText.Text = "Restoring RTSS…";
                    try
                    {
                        await Task.Delay(220);
                        var restart = await _pipeClient.SendAsync(new RtssRequest { Command = RtssCommand.StartRtss }, 15000);
                        if (!restart.Success)
                        {
                            var restartError = restart.ErrorMessage ?? restart.ErrorCode ?? "RTSS restart failed.";
                            SetError(string.IsNullOrWhiteSpace(operationError)
                                ? restartError
                                : operationError + Environment.NewLine + "RTSS restore also failed: " + restartError);
                        }
                        else if (!success && !string.IsNullOrWhiteSpace(operationError))
                        {
                            SetError(operationError);
                        }
                        await Task.Delay(900);
                    }
                    catch (Exception ex)
                    {
                        SetError(string.IsNullOrWhiteSpace(operationError)
                            ? "RTSS restore failed: " + ex.Message
                            : operationError + Environment.NewLine + "RTSS restore also failed: " + ex.Message);
                    }
                }

                await Task.Delay(180);
                await RefreshStatusAsync(false);
                if (_pendingCommands == 0)
                    OperationText.Text = string.Empty;
            }
        }

        private async Task<bool> TryMinimizeWidgetAsync()
        {
            if (_widget == null)
                return false;

            try
            {
                await _widget.MinimizeAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task TryRestoreWidgetAsync()
        {
            if (_widget == null)
                return;

            try
            {
                await _widget.RestoreAsync();
                await Task.Delay(220);
            }
            catch { }
        }

        private async void FrameLimitSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingControls || _status?.FrameLimit == null || !FrameLimitSlider.IsEnabled)
                return;

            var value = Math.Max(0, Math.Min(1000, (int)Math.Round(e.NewValue)));
            await QueueFrameLimitAsync(value);
        }

        private async void FrameLimitPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingControls || _status?.FrameLimit == null || !FrameLimitPresetComboBox.IsEnabled)
                return;

            var item = FrameLimitPresetComboBox.SelectedItem as ComboBoxItem;
            int fps;
            if (item != null && int.TryParse(item.Tag?.ToString(), out fps))
                await CommitFrameLimitImmediateAsync(fps);
        }

        private async void LimiterTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingControls || _status?.LimiterType == null || !LimiterTypeComboBox.IsEnabled)
                return;

            var selected = LimiterTypeComboBox.SelectedIndex;
            if (selected < 0 || selected > 3 || selected == (int)_status.LimiterType.Value)
                return;

            await SetLimiterTypeAsync((RtssLimiterType)selected);
        }

        private async void LimiterEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingControls || _status?.LimiterEnabled == null || !LimiterEnabledToggle.IsEnabled)
                return;
            if (LimiterEnabledToggle.IsOn == _status.LimiterEnabled.Value)
                return;

            await SetLimiterEnabledAsync(LimiterEnabledToggle.IsOn);
        }

        private async void OverlayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingControls || _status?.OverlayVisible == null || !OverlayToggle.IsEnabled)
                return;
            if (OverlayToggle.IsOn == _status.OverlayVisible.Value)
                return;

            await SetOsdVisibleAsync(OverlayToggle.IsOn);
        }

        private async void OsdZoomSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingControls || _status?.OsdZoom == null || !OsdZoomSlider.IsEnabled)
                return;

            var value = Math.Max(1, Math.Min(8, (int)Math.Round(e.NewValue)));
            await SetOsdZoomFromUiAsync(value);
        }

        private async void OsdPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingControls || _status == null || !OsdPositionComboBox.IsEnabled)
                return;

            var item = OsdPositionComboBox.SelectedItem as ComboBoxItem;
            int value;
            if (item == null || !int.TryParse(item.Tag?.ToString(), out value) || value < 0 || value > 7)
                return;

            var position = (RtssOsdPosition)value;
            if (_status.OsdPosition == position)
                return;

            await SetOsdPositionAsync(position);
        }

        private async Task QueueFrameLimitAsync(int value)
        {
            if (_status?.FrameLimit == null || !FrameLimitSlider.IsEnabled)
                return;

            // While a write is pending/in flight, the UI's desired value is authoritative.
            // Never base a repeated Slider move on an older read-back that can arrive meanwhile.
            var current = (_frameCommitPending || _frameWriterRunning)
                ? _desiredFrameLimit
                : _status.FrameLimit.Value;
            var next = Math.Max(0, Math.Min(1000, value));
            if (next == current)
                return;

            _desiredFrameLimit = next;
            _status.FrameLimit = next;
            ApplyStatus(_status);

            // Debounce physical repeat/drag bursts. UI changes immediately, but RTSS only gets
            // the latest requested value after the user pauses. A single writer serializes IPC.
            var generation = ++_frameCommitGeneration;
            _frameCommitPending = true;
            await Task.Delay(320);
            if (generation != _frameCommitGeneration)
                return;

            _frameCommitPending = false;
            await FlushFrameLimitAsync();
        }

        private async Task CommitFrameLimitImmediateAsync(int value)
        {
            ++_frameCommitGeneration;
            _frameCommitPending = false;
            _desiredFrameLimit = value;
            if (_status != null)
            {
                _status.FrameLimit = value;
                ApplyStatus(_status);
            }
            await FlushFrameLimitAsync();
        }

        private async Task FlushFrameLimitAsync()
        {
            if (_frameWriterRunning)
                return;

            _frameWriterRunning = true;
            try
            {
                // Latest-wins writer: at most one SetFrameLimit is in flight. If the user changes
                // the value during a write, keep showing the desired value and send the newest
                // value next (after its debounce has expired). Older responses never overwrite it.
                while (!_frameCommitPending && _desiredFrameLimit != _committedFrameLimit)
                {
                    var target = _desiredFrameLimit;
                    var success = await ExecuteCommandAsync(
                        new RtssRequest { Command = RtssCommand.SetFrameLimit, IntValue = target },
                        null,
                        "Applying frame limit…",
                        false,
                        3000,
                        () => { });

                    if (!success)
                    {
                        ++_frameCommitGeneration;
                        _frameCommitPending = false;
                        _desiredFrameLimit = _committedFrameLimit;
                        if (_status != null)
                        {
                            _status.FrameLimit = _committedFrameLimit;
                            ApplyStatus(_status);
                        }
                        break;
                    }

                    _committedFrameLimit = target;
                    if (_status != null)
                    {
                        // A newer Slider value may have arrived while this target was being written.
                        _status.FrameLimit = _desiredFrameLimit;
                        ApplyStatus(_status);
                    }
                }
            }
            finally
            {
                _frameWriterRunning = false;
            }
        }

        private async Task SetOsdZoomFromUiAsync(int value)
        {
            if (_status?.OsdZoom == null || !OsdZoomSlider.IsEnabled)
                return;

            var current = (_zoomCommitPending || _zoomWriterRunning)
                ? _desiredOsdZoom
                : _status.OsdZoom.Value;
            var next = Math.Max(1, Math.Min(8, value));
            if (next == current)
                return;

            _desiredOsdZoom = next;
            _status.OsdZoom = next;
            ApplyStatus(_status);

            var generation = ++_zoomCommitGeneration;
            _zoomCommitPending = true;
            await Task.Delay(180);
            if (generation != _zoomCommitGeneration)
                return;

            _zoomCommitPending = false;
            await FlushOsdZoomAsync();
        }

        private async Task FlushOsdZoomAsync()
        {
            if (_zoomWriterRunning)
                return;

            _zoomWriterRunning = true;
            try
            {
                while (!_zoomCommitPending && _desiredOsdZoom != _committedOsdZoom)
                {
                    var target = _desiredOsdZoom;
                    var success = await ExecuteCommandAsync(
                        new RtssRequest { Command = RtssCommand.SetOsdZoom, IntValue = target },
                        null,
                        "Applying OSD size…",
                        false,
                        3000,
                        () => { });

                    if (!success)
                    {
                        ++_zoomCommitGeneration;
                        _zoomCommitPending = false;
                        _desiredOsdZoom = _committedOsdZoom;
                        if (_status != null)
                        {
                            _status.OsdZoom = _committedOsdZoom;
                            ApplyStatus(_status);
                        }
                        break;
                    }

                    _committedOsdZoom = target;
                    if (_status != null)
                    {
                        _status.OsdZoom = _desiredOsdZoom;
                        ApplyStatus(_status);
                    }
                }
            }
            finally
            {
                _zoomWriterRunning = false;
            }
        }

        private async Task SetLimiterTypeAsync(RtssLimiterType limiterType)
        {
            if (_status?.LimiterType == null || !LimiterTypeComboBox.IsEnabled)
                return;
            if (_status.LimiterType.Value == limiterType)
                return;

            await ExecuteCommandAsync(
                new RtssRequest { Command = RtssCommand.SetLimiterType, IntValue = (int)limiterType },
                status => status.LimiterType = limiterType,
                "Applying limiter type…");
        }

        private async Task SetLimiterEnabledAsync(bool enabled)
        {
            await ExecuteCommandAsync(
                new RtssRequest { Command = RtssCommand.SetLimiterEnabled, BoolValue = enabled },
                status => status.LimiterEnabled = enabled,
                "Applying limiter state…");
        }

        private async Task SetOsdVisibleAsync(bool visible)
        {
            await ExecuteCommandAsync(
                new RtssRequest { Command = RtssCommand.SetOverlayVisible, BoolValue = visible },
                status => status.OverlayVisible = visible,
                "Applying OSD state…");
        }

        private async Task SetOsdPositionAsync(RtssOsdPosition position)
        {
            await ExecuteCommandAsync(
                new RtssRequest { Command = RtssCommand.SetOsdPosition, IntValue = (int)position },
                status => status.OsdPosition = position,
                "Applying OSD position…");
        }

        private async Task<bool> ExecuteCommandAsync(
            RtssRequest request,
            Action<RtssStatus> optimisticMutation,
            string operationText,
            bool applyReturnedStatus = true,
            int timeoutMs = 3500,
            Action rollbackOverride = null)
        {
            var previous = CloneStatus(_status);
            if (_status != null && optimisticMutation != null)
            {
                optimisticMutation(_status);
                ApplyStatus(_status);
            }

            _pendingCommands++;
            OperationText.Text = operationText ?? string.Empty;
            SetError(null);
            try
            {
                var response = await _pipeClient.SendAsync(request, timeoutMs);
                if (!response.Success)
                    throw new InvalidOperationException(response.ErrorMessage ?? response.ErrorCode ?? "RTSS command failed.");

                if (applyReturnedStatus && response.Status != null)
                {
                    _status = response.Status;
                    CaptureCommittedValues(_status);
                    ApplyStatus(_status);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (rollbackOverride != null)
                    rollbackOverride();
                else if (previous != null)
                {
                    _status = previous;
                    ApplyStatus(_status);
                }
                SetError(ex.Message);
                return false;
            }
            finally
            {
                _pendingCommands = Math.Max(0, _pendingCommands - 1);
                if (_pendingCommands == 0)
                    OperationText.Text = string.Empty;
            }
        }

        private void ResetScrollToTop()
        {
            if (WidgetScrollViewer == null)
                return;

            try
            {
                WidgetScrollViewer.ChangeView(null, 0.0, null, true);
            }
            catch
            {
                // Returning to the top is a convenience only; never fail activation for it.
            }
        }

        private void ApplyRequestedWidgetAppearance()
        {
            if (_widget == null || WidgetBackground == null)
                return;

            try
            {
                // The actual surface color comes from the same Game Bar/UWP theme resource used
                // by the other widget panels. RequestedTheme lets that resource resolve correctly.
                RequestedTheme = _widget.RequestedTheme;
            }
            catch
            {
                // Keep the current app theme if Game Bar does not expose a requested theme.
            }

            try
            {
                var opacity = _widget.RequestedOpacity;
                WidgetBackground.Opacity = Math.Max(0.0, Math.Min(1.0, opacity));
            }
            catch
            {
                WidgetBackground.Opacity = 1.0;
            }
        }

        private void HookWidgetAppearance()
        {
            if (_widget == null)
                return;

            if (!_widgetOpacityHooked)
            {
                try
                {
                    _widget.RequestedOpacityChanged += Widget_RequestedOpacityChanged;
                    _widgetOpacityHooked = true;
                }
                catch { }
            }

            if (!_widgetThemeHooked)
            {
                try
                {
                    _widget.RequestedThemeChanged += Widget_RequestedThemeChanged;
                    _widgetThemeHooked = true;
                }
                catch { }
            }

            ApplyRequestedWidgetAppearance();
        }

        private void UnhookWidgetAppearance()
        {
            if (_widget == null)
                return;

            if (_widgetOpacityHooked)
            {
                try
                {
                    _widget.RequestedOpacityChanged -= Widget_RequestedOpacityChanged;
                }
                catch { }
                _widgetOpacityHooked = false;
            }

            if (_widgetThemeHooked)
            {
                try
                {
                    _widget.RequestedThemeChanged -= Widget_RequestedThemeChanged;
                }
                catch { }
                _widgetThemeHooked = false;
            }
        }

        private async void Widget_RequestedOpacityChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, ApplyRequestedWidgetAppearance);
        }

        private async void Widget_RequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, ApplyRequestedWidgetAppearance);
        }

        private void HookWindowVisibility()
        {
            // XboxGameBarWidget.Visible is the most precise signal for "the user can see this
            // widget now" because it combines Game Bar display mode, widget window state and
            // pinning. Use CoreWindow visibility only as a fallback if the Game Bar object is
            // unavailable for some reason.
            if (_widget != null)
            {
                if (!_widgetVisibilityHooked)
                {
                    try
                    {
                        _widget.VisibleChanged += Widget_VisibleChanged;
                        _widgetVisibilityHooked = true;
                    }
                    catch { }
                }

                if (_widgetVisibilityHooked)
                    return;
            }

            if (_coreWindowVisibilityHooked)
                return;
            try
            {
                Window.Current.CoreWindow.VisibilityChanged += CoreWindow_VisibilityChanged;
                _coreWindowVisibilityHooked = true;
            }
            catch { }
        }

        private void UnhookWindowVisibility()
        {
            if (_widgetVisibilityHooked && _widget != null)
            {
                try
                {
                    _widget.VisibleChanged -= Widget_VisibleChanged;
                }
                catch { }
            }
            _widgetVisibilityHooked = false;

            if (_coreWindowVisibilityHooked)
            {
                try
                {
                    Window.Current.CoreWindow.VisibilityChanged -= CoreWindow_VisibilityChanged;
                }
                catch { }
            }
            _coreWindowVisibilityHooked = false;
        }

        private async void Widget_VisibleChanged(XboxGameBarWidget sender, object args)
        {
            if (sender == null)
                return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                ConfigureRefreshTimerForCurrentVisibility();
                if (!sender.Visible)
                    return;

                ResetScrollToTop();
                _ = RefreshStatusAsync(false);
            });
        }

        private async void CoreWindow_VisibilityChanged(CoreWindow sender, VisibilityChangedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                ConfigureRefreshTimerForCurrentVisibility();
                if (!args.Visible)
                    return;

                ResetScrollToTop();
                _ = RefreshStatusAsync(false);
            });
        }

        private void ConfigureRefreshTimerForCurrentVisibility()
        {
            if (!IsCurrentlyVisible())
            {
                _refreshTimer.Stop();
                return;
            }

            _refreshTimer.Interval = VisibleRefreshInterval;
            _refreshTimer.Start();
        }

        private bool IsCurrentlyVisible()
        {
            if (_widget != null)
            {
                try { return _widget.Visible; }
                catch { }
            }

            try
            {
                var coreWindow = Window.Current?.CoreWindow;
                return coreWindow != null && coreWindow.Visible;
            }
            catch
            {
                return false;
            }
        }

        private static SolidColorBrush IntegrationLedBrush(RtssIntegrationState state)
        {
            switch (state)
            {
                case RtssIntegrationState.Connected:
                    return LedGreen;
                case RtssIntegrationState.UpdateRequired:
                case RtssIntegrationState.Disabled:
                case RtssIntegrationState.Incompatible:
                    return LedAmber;
                case RtssIntegrationState.Error:
                case RtssIntegrationState.RtssNotInstalled:
                    return LedRed;
                default:
                    return LedGray;
            }
        }

        private static RtssStatus CloneStatus(RtssStatus source)
        {
            if (source == null)
                return null;
            return new RtssStatus
            {
                Installed = source.Installed,
                Running = source.Running,
                FrameLimit = source.FrameLimit,
                LimiterEnabled = source.LimiterEnabled,
                OverlayVisible = source.OverlayVisible,
                Detail = source.Detail,
                LimiterType = source.LimiterType,
                PluginInstalled = source.PluginInstalled,
                PluginConnected = source.PluginConnected,
                OsdZoom = source.OsdZoom,
                OsdPosition = source.OsdPosition,
                PluginUpdateAvailable = source.PluginUpdateAvailable,
                IntegrationState = source.IntegrationState,
                PluginVersion = source.PluginVersion,
                BundledPluginVersion = source.BundledPluginVersion
            };
        }

        private static string IntegrationStateDisplayName(RtssIntegrationState state)
        {
            switch (state)
            {
                case RtssIntegrationState.RtssNotInstalled: return "RTSS missing";
                case RtssIntegrationState.NotInstalled: return "Not installed";
                case RtssIntegrationState.UpdateRequired: return "Update required";
                case RtssIntegrationState.RtssStopped: return "Ready";
                case RtssIntegrationState.Disabled: return "Disabled";
                case RtssIntegrationState.Incompatible: return "Incompatible";
                case RtssIntegrationState.Connected: return "Connected";
                case RtssIntegrationState.Error: return "Error";
                default: return "Unknown";
            }
        }

        private void SetError(string message)
        {
            ErrorText.Text = message ?? string.Empty;
        }
    }
}
