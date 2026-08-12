from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors = []

required = [
    'LICENSE',
    'README.md',
    'CHANGELOG.md',
    'RELEASE_NOTES.md',
    'SECURITY.md',
    'CONTRIBUTING.md',
    '.github/workflows/static-check.yml',
    'RTSSGameBar.sln',
    'src/RTSSGameBar.Protocol/Protocol.cs',
    'src/RTSSGameBar.Helper/Program.cs',
    'src/RTSSGameBar.Helper/Ipc/PipeServer.cs',
    'src/RTSSGameBar.Helper/Rtss/RtssController.cs',
    'src/RTSSGameBar.Helper/RtssPlugin/RtssPluginClient.cs',
    'src/RTSSGameBar.Helper/Integration/IntegrationManager.cs',
    'src/RTSSGameBar.Setup/RTSSGameBar.Setup.vcxproj',
    'src/RTSSGameBar.Setup/Setup.cpp',
    'src/RTSSGameBar.Setup/RTSSGameBar.Setup.rc',
    'src/RTSSGameBar.Setup/app.manifest',
    'src/RTSSGameBar.Widget/Package.appxmanifest',
    'src/RTSSGameBar.Widget/GamingWidget.xaml',
    'src/RTSSGameBar.Widget/GamingWidget.xaml.cs',
    'src/RTSSGameBar.RTSSPlugin/RTSSGameBar.RTSSPlugin.vcxproj',
    'src/RTSSGameBar.RTSSPlugin/RTSSGameBarPlugin.h',
    'src/RTSSGameBar.RTSSPlugin/RTSSGameBarPlugin.cpp',
    'scripts/build-release-package.cmd',
    'scripts/create-signing-cert.ps1',
    'scripts/sign-release-package.ps1',
    'scripts/trust-signing-cert.ps1',
    'scripts/install-local-package.ps1',
    'scripts/prepare-github-release.ps1',
    'docs/ARCHITECTURE.md',
    'docs/PACKAGING.md',
    'docs/PUBLISHING.md',
    'docs/PRIVACY.md',
    'docs/RELEASE_CHECKLIST.md',
    'docs/TESTING.md',
]
for rel in required:
    if not (ROOT / rel).is_file():
        errors.append('missing: ' + rel)

xml_files = (
    list((ROOT / 'src').rglob('*.xaml'))
    + list((ROOT / 'src').rglob('*.csproj'))
    + list((ROOT / 'src').rglob('*.vcxproj'))
    + list((ROOT / 'src').rglob('*.manifest'))
)
for path in xml_files:
    try:
        ET.parse(path)
    except Exception as exc:
        errors.append(f'invalid XML: {path.relative_to(ROOT)}: {exc}')

manifest = (ROOT / 'src/RTSSGameBar.Widget/Package.appxmanifest').read_text(encoding='utf-8')
for needle in [
    'Name="VirtualGIT20.RTSSGameBar"',
    'Publisher="CN=VirtualGIT20"',
    'Version="1.0.0.0"',
    '<PublisherDisplayName>VirtualGIT20</PublisherDisplayName>',
    'DisplayName="RTSS Game Bar"',
    'Name="microsoft.gameBarUIExtension"',
    'Category="windows.fullTrustProcess"',
    'Name="runFullTrust"',
]:
    if needle not in manifest:
        errors.append('manifest missing: ' + needle)
if 'Name="allowElevation"' in manifest:
    errors.append('manifest unexpectedly declares allowElevation')
for stale in ['RTSSGameBar.POC', 'RTSSGameBar POC', 'DisplayName="RTSS Control"']:
    if stale in manifest:
        errors.append('stale public manifest metadata remains: ' + stale)

main_page = (ROOT / 'src/RTSSGameBar.Widget/MainPage.xaml').read_text(encoding='utf-8')
if 'select RTSS Game Bar.' not in main_page:
    errors.append('standalone launch page still uses stale public widget name')

assembly_info = (ROOT / 'src/RTSSGameBar.Widget/Properties/AssemblyInfo.cs').read_text(encoding='utf-8')
for needle in [
    'AssemblyCompany("VirtualGIT20")',
    'AssemblyProduct("RTSS Game Bar")',
    'AssemblyVersion("1.0.0.0")',
    'AssemblyFileVersion("1.0.0.0")',
]:
    if needle not in assembly_info:
        errors.append('widget assembly metadata missing: ' + needle)

for rel in [
    'src/RTSSGameBar.Protocol/RTSSGameBar.Protocol.csproj',
    'src/RTSSGameBar.Helper/RTSSGameBar.Helper.csproj',
]:
    text = (ROOT / rel).read_text(encoding='utf-8')
    for needle in ['<Version>1.0.0</Version>', '<AssemblyVersion>1.0.0.0</AssemblyVersion>',
                   '<FileVersion>1.0.0.0</FileVersion>', '<Company>VirtualGIT20</Company>',
                   '<Product>RTSS Game Bar</Product>']:
        if needle not in text:
            errors.append(f'public assembly metadata missing in {rel}: {needle}')

protocol = (ROOT / 'src/RTSSGameBar.Protocol/Protocol.cs').read_text(encoding='utf-8')
for needle in [
    'Version = 19', 'RTSSGameBar.v19', 'RTSSGameBar.RTSSPlugin.v6',
    'RtssPluginProtocolVersion = 6', 'BundledPluginVersion = "1.0.0"',
    'MiddleLeft = 6', 'MiddleRight = 7', 'SetOsdZoom', 'SetOsdPosition',
    'RtssOsdPosition', 'StopRtss', 'InstallIntegration', 'UpdateIntegration',
    'RemoveIntegration', 'RtssIntegrationState', 'PluginUpdateAvailable', 'OsdZoom'
]:
    if needle not in protocol:
        errors.append('protocol missing: ' + needle)
if 'RTSSGameBar.POC' in protocol:
    errors.append('development POC pipe name remains in public protocol')

helper_src = '\n'.join(p.read_text(encoding='utf-8', errors='replace') for p in (ROOT / 'src/RTSSGameBar.Helper').rglob('*.cs'))
for obsolete in ['RtssNativeApi', 'RTSSHooks64.dll', 'SetIntProfilePropertyVerified']:
    if obsolete in helper_src:
        errors.append('obsolete direct-helper RTSS backend remains: ' + obsolete)
for needle in [
    'SetFrameLimit', 'SetLimiterType', 'SetLimiterEnabled', 'SetOverlayVisible',
    'SetOsdZoom', 'SetOsdPosition', 'StopRtss', 'IntegrationManager', 'ShellExecuteEx',
    'GetForegroundWindow', 'lpVerb = "runas"', 'SeeMaskNoCloseProcess',
    'IsUpdateAvailable', 'RtssPluginClient', 'RequestRtssWindowClose'
]:
    if needle not in helper_src:
        errors.append('helper/integration behavior missing: ' + needle)
if 'Process.Start(new ProcessStartInfo' in (ROOT / 'src/RTSSGameBar.Helper/Integration/IntegrationManager.cs').read_text(encoding='utf-8'):
    errors.append('integration elevation regressed to Process.Start instead of owner-aware ShellExecuteEx')
if 'Local\\RTSSGameBar.Helper.v19.Singleton' not in helper_src:
    errors.append('public helper mutex does not match protocol v19')
if 'Helper.POC' in helper_src:
    errors.append('development POC helper mutex/name remains')

setup = (ROOT / 'src/RTSSGameBar.Setup/Setup.cpp').read_text(encoding='utf-8')
for needle in [
    'ExitSuccess = 0', 'ExitRtssStillRunning = 20', 'ExitRtssNotFound = 21',
    'ExitBundledPluginMissing = 22', 'ExitFileOperationFailed = 23',
    'L"install"', 'L"update"', 'L"remove"',
    'IsRtssRunning', 'CopyFileW', 'DeleteFileW', 'RTSSGameBarPlugin.dll',
    'EnsureRtssStoppedForMaintenance', 'TerminateProcess', 'RegOpenKeyExW',
    'KEY_WOW64_32KEY', 'KEY_WOW64_64KEY', 'WM_CLOSE',
    'restart is delegated to the normal helper'
]:
    if needle not in setup:
        errors.append('native setup behavior missing: ' + needle)
for forbidden in ['taskkill', 'Set-Acl', 'ScheduledTask', 'QueueDeferredRestart', 'RunDeferredRestart', 'mscoree.dll', '_CorExeMain']:
    if forbidden in setup:
        errors.append('setup contains forbidden/removed behavior: ' + forbidden)
setup_project = (ROOT / 'src/RTSSGameBar.Setup/RTSSGameBar.Setup.vcxproj').read_text(encoding='utf-8')
for needle in ['Debug|x64', 'Release|x64', 'MachineX64', '<PlatformToolset>v145</PlatformToolset>',
               '<RuntimeLibrary>MultiThreaded</RuntimeLibrary>', '<TargetName>RTSSGameBar.Setup</TargetName>']:
    if needle not in setup_project:
        errors.append('native setup project config missing: ' + needle)
setup_resource = (ROOT / 'src/RTSSGameBar.Setup/RTSSGameBar.Setup.rc').read_text(encoding='utf-8')
for needle in ['FILEVERSION 1,0,0,0', 'PRODUCTVERSION 1,0,0,0', 'VirtualGIT20', 'RTSS Game Bar Integration Setup']:
    if needle not in setup_resource:
        errors.append('native setup version resource missing: ' + needle)
setup_manifest = (ROOT / 'src/RTSSGameBar.Setup/app.manifest').read_text(encoding='utf-8')
for needle in ['requireAdministrator', 'supportedOS', 'RTSSGameBar.Setup']:
    if needle not in setup_manifest:
        errors.append('setup manifest missing: ' + needle)

plugin = (ROOT / 'src/RTSSGameBar.RTSSPlugin/RTSSGameBarPlugin.cpp').read_text(encoding='utf-8')
for needle in [
    'kPluginVersion = "1.0.0"', 'RTSSGameBar.RTSSPlugin.v6', 'PONG|protocol=6',
    'STATE|protocol=6', 'pluginVersion=%s', 'capabilities=%s', 'GET_STATE',
    'SET_FRAME_LIMIT|value=', 'SET_SYNC_LIMITER|value=', 'SET_LIMITER_ENABLED|value=',
    'SET_OSD_VISIBLE|value=', 'SET_OSD_ZOOM|value=', 'SET_OSD_POSITION|value=',
    'PositionX', 'PositionY', 'CoordinateSpace', 'SetOsdPositionVerified',
    'ResolveOsdPresetPosition', 'DetectOsdPositionPreset', 'CLOSE_RTSS',
    'GetProcAddress(hooks, "SetProfileProperty")', 'GetProcAddress(hooks, "SaveProfile")',
    'GetProcAddress(hooks, "UpdateProfiles")', 'GetProcAddress(hooks, "SetFlags")',
    'SetIntPropertyVerified', 'SetFlagVerified', 'ZoomRatio', 'EnumWindows'
]:
    if needle not in plugin:
        errors.append('plugin backend missing: ' + needle)
for forbidden in ['CreateProcess', 'ShellExecute', 'DeleteFile', 'MoveFile']:
    if forbidden in plugin:
        errors.append('plugin has unexpected generic process/file behavior: ' + forbidden)

widget_xaml = (ROOT / 'src/RTSSGameBar.Widget/GamingWidget.xaml').read_text(encoding='utf-8')
widget_cs = (ROOT / 'src/RTSSGameBar.Widget/GamingWidget.xaml.cs').read_text(encoding='utf-8')
for needle in [
    'Text="RTSS Game Bar"', 'FrameLimitSlider', 'FrameLimitPresetComboBox', 'LimiterTypeComboBox',
    'LimiterEnabledToggle', 'OverlayToggle', 'OsdZoomSlider', 'OsdPositionComboBox',
    'RtssActionButton', 'IntegrationActionButton', 'RefreshButton',
    '<Slider ', '<ComboBox ', '<ToggleSwitch ', 'Symbol="Sync"', 'Text="v1.0.0"'
]:
    if needle not in widget_xaml:
        errors.append('controller UI missing: ' + needle)
for stale in [
    'FrameLimitValueButton', 'DecreaseLimitButton', 'IncreaseLimitButton',
    'PresetUnlimitedButton', 'Preset30Button', 'Preset60Button', 'Preset90Button',
    'Preset120Button', 'Text="RTSS Control"', 'LimiterTypeButton', 'LimiterEnabledButton', 'OsdButton',
    'OsdZoomValueButton', 'DecreaseOsdZoomButton', 'IncreaseOsdZoomButton',
    'StepButtonStyle', 'PresetButtonStyle'
]:
    if stale in widget_xaml:
        errors.append('obsolete multi-target controller UI remains: ' + stale)
for needle in [
    'QueueFrameLimitAsync', 'FlushFrameLimitAsync', '_frameWriterRunning', '_desiredFrameLimit',
    'SetOsdZoomFromUiAsync', 'FlushOsdZoomAsync', '_zoomWriterRunning', '_desiredOsdZoom',
    'SetOsdPositionAsync', 'OsdPositionComboBox_SelectionChanged', '_syncingControls',
    '_frameCommitGeneration', '_zoomCommitGeneration', 'InstallIntegration', 'UpdateIntegration',
    'RemoveIntegration', 'StopRtss', 'VisibleChanged += Widget_VisibleChanged',
    'RefreshStatusAsync(false)', 'VisibleRefreshInterval', 'BuildRenderStatusKey',
    'IsCurrentlyVisible', 'coreWindow.Visible', 'ConfigureRefreshTimerForCurrentVisibility'
]:
    if needle not in widget_cs:
        errors.append('widget behavior missing: ' + needle)
if 'if (wasRunning && _status.PluginConnected)' not in widget_cs:
    errors.append('widget integration pre-stop is not gated on PluginConnected')
if 'TryMinimizeForElevationAsync' in widget_cs or 'Game Bar will move out of the foreground' in widget_cs:
    errors.append('stale minimize-before-UAC behavior/text remains')
for needle in [
    'BackRequested', 'SystemNavigationManager', 'TryMoveFocus', 'TryFocusAsync', 'TryExitToGameBar',
    'VirtualKey.GamepadB', 'Page_PreviewKeyDown', 'GamingWidget_GettingFocus', '_controllerEntryDownPending'
]:
    if needle in widget_cs:
        errors.append('custom Game Bar focus/B workaround remains: ' + needle)
if 'XYFocusKeyboardNavigation="Enabled"' in widget_xaml:
    errors.append('page forces keyboard XY focus instead of native Game Bar/gamepad behavior')
if 'B back/close' not in widget_xaml:
    errors.append('controller footer missing native B behavior')
if 'XYFocusDown=' in widget_xaml or 'XYFocusUp=' in widget_xaml:
    errors.append('static XAML XYFocus links remain; graph must be status-aware in code')
for needle in [
    'UpdateFocusGraph()', 'var ordered = new Control[]', 'control.IsEnabled',
    'control.Visibility == Visibility.Visible', 'control.XYFocusUp = null',
    'control.XYFocusDown = null', 'enabled[i].XYFocusUp', 'enabled[i].XYFocusDown'
]:
    if needle not in widget_cs:
        errors.append('status-aware focus graph missing: ' + needle)
focus_region = widget_cs[widget_cs.find('private void UpdateFocusGraph()'):]
for control_name in [
    'FrameLimitSlider', 'FrameLimitPresetComboBox', 'LimiterTypeComboBox',
    'LimiterEnabledToggle', 'OverlayToggle', 'OsdZoomSlider', 'OsdPositionComboBox',
    'RtssActionButton', 'IntegrationActionButton', 'RefreshButton'
]:
    if control_name not in focus_region:
        errors.append('focus order missing control: ' + control_name)
for forbidden in ['e.Handled = true', 'e.Handled=true', 'Focus(FocusState', 'FocusManager.TryMoveFocus']:
    if forbidden in widget_cs:
        errors.append('navigation must not consume or redirect input: ' + forbidden)
if 'integrationActionRequired' not in widget_cs or '&& !integrationActionRequired' not in widget_cs:
    errors.append('blocking integration state no longer omits RTSS Start/Close')
if 'SetControlAvailability(controllable);' not in widget_cs or widget_cs.count('UpdateFocusGraph();') < 2:
    errors.append('focus graph is not rebuilt after status/availability changes')

for label in ['Top left', 'Top center', 'Top right', 'Middle left', 'Middle right', 'Bottom left', 'Bottom center', 'Bottom right']:
    if f'Content="{label}"' not in widget_xaml:
        errors.append('OSD position preset missing: ' + label)
if 'setProperty("CoordinateSpace"' not in plugin or 'mutableCoordinateSpace = 0' not in plugin:
    errors.append('native OSD position no longer writes CoordinateSpace=0')
for needle in [
    'case 0: x =  1; y =  1', 'case 1: x =  0; y =  1', 'case 2: x = -1; y =  1',
    'case 3: x =  1; y = -1', 'case 4: x =  0; y = -1', 'case 5: x = -1; y = -1',
    'case 6: x =  1; y =  0', 'case 7: x = -1; y =  0'
]:
    if needle not in plugin:
        errors.append('native RTSS OSD mapping missing: ' + needle)
for needle in [
    'if (x ==  1 && y ==  1) return 0', 'if (x ==  0 && y ==  1) return 1',
    'if (x == -1 && y ==  1) return 2', 'if (x ==  1 && y == -1) return 3',
    'if (x ==  0 && y == -1) return 4', 'if (x == -1 && y == -1) return 5',
    'if (x ==  1 && y ==  0) return 6', 'if (x == -1 && y ==  0) return 7'
]:
    if needle not in plugin:
        errors.append('native RTSS OSD read-back detection mismatch: ' + needle)
for stale in ['GetActiveMonitorWidth', 'kOsdEdgeMargin']:
    if stale in plugin:
        errors.append('stale resolution-dependent OSD mapping remains: ' + stale)

plugin_client = (ROOT / 'src/RTSSGameBar.Helper/RtssPlugin/RtssPluginClient.cs').read_text(encoding='utf-8')
if 'if (value < 0 || value > 7)' not in plugin_client:
    errors.append('helper plugin client rejects middle OSD positions')
if 'ParseIntValue(request, "SET_OSD_POSITION|value=", 0, 7, value)' not in plugin:
    errors.append('RTSS plugin parser rejects middle OSD positions')

for label, tag in [
    ('Unlimited', 0), ('30 FPS', 30), ('40 FPS', 40), ('60 FPS', 60), ('90 FPS', 90),
    ('120 FPS', 120), ('144 FPS', 144), ('165 FPS', 165), ('240 FPS', 240), ('360 FPS', 360)
]:
    if f'Content="{label}" Tag="{tag}"' not in widget_xaml:
        errors.append('common limiter preset missing: ' + label)
for value, index in [(0,0),(30,1),(40,2),(60,3),(90,4),(120,5),(144,6),(165,7),(240,8),(360,9)]:
    if f'case {value}: return {index};' not in widget_cs:
        errors.append(f'frame limiter preset index mismatch: {value} -> {index}')

for needle in [
    'RefreshButton.IsTabStop = !integrationActionBlocking', 'control != RefreshButton',
    'IntegrationActionButton.XYFocusDown = IntegrationActionButton',
    'RtssIntegrationState.NotInstalled', 'RtssIntegrationState.UpdateRequired',
    'RtssIntegrationState.Incompatible'
]:
    if needle not in widget_cs:
        errors.append('blocking Integration focus guard missing: ' + needle)
if 'IntegrationActionButton.XYFocusUp = IntegrationActionButton' in widget_cs:
    errors.append('Integration XYFocusUp must stay unset so controller Up can exit')

for needle in ['if (!IsCurrentlyVisible())', '_refreshTimer.Stop();', 'coreWindow.Visible', 'TimeSpan.FromSeconds(5)']:
    if needle not in widget_cs:
        errors.append('visibility-aware polling requirement missing: ' + needle)
if 'pluginReadClock.ElapsedMilliseconds >= 100' not in helper_src:
    errors.append('slow RTSS read warning threshold is not 100 ms')
if 'processes = Process.GetProcessesByName("RTSS");' not in helper_src or 'process.Dispose();' not in helper_src:
    errors.append('helper RTSS process enumeration does not dispose Process objects')
log_src = (ROOT / 'src/RTSSGameBar.Helper/Platform/Log.cs').read_text(encoding='utf-8')
for needle in ['RotateAfterBytes', 'helper.previous.log', 'PrepareLogFile()', 'if (_prepared)']:
    if needle not in log_src:
        errors.append('helper log rotation cleanup missing: ' + needle)

for stale in [
    'FocusDiagnosticsEnabled', 'FocusableControl_GotFocus', 'GamingWidget_KeyDown', 'GamingWidget_KeyUp',
    'FlushInputTraceAsync', 'TraceStartupLocal', '_inputDiagnosticsTimer', 'AddHandler(UIElement.KeyDownEvent',
    'AddHandler(UIElement.KeyUpEvent'
]:
    if stale in widget_cs or stale in widget_xaml:
        errors.append('temporary input/focus diagnostics remain: ' + stale)
for stale in ['WidgetTrace', 'PingRtssPlugin']:
    if stale in protocol or stale in helper_src:
        errors.append('dead diagnostic/probe IPC command remains: ' + stale)
for stale in ['ApiAvailable', 'HelperElevated', 'InstallDirectory', 'RawFlags', 'PluginInfo', 'PluginCapabilities',
              'OsdPositionX', 'OsdPositionY', 'OsdCoordinateSpace', 'TextValue']:
    if stale in protocol:
        errors.append('unused widget/helper protocol field remains: ' + stale)

plugin_project = (ROOT / 'src/RTSSGameBar.RTSSPlugin/RTSSGameBar.RTSSPlugin.vcxproj').read_text(encoding='utf-8')
for needle in ['Debug|Win32', 'Release|Win32', 'MachineX86', '<PlatformToolset>v145</PlatformToolset>']:
    if needle not in plugin_project:
        errors.append('plugin project config missing: ' + needle)
widget_project = (ROOT / 'src/RTSSGameBar.Widget/RTSSGameBar.Widget.csproj').read_text(encoding='utf-8')
if '<PackageReference Include="Microsoft.Gaming.XboxGameBar"><Version>7.3.2506120</Version></PackageReference>' not in widget_project:
    errors.append('widget is not pinned to Xbox Game Bar SDK 7.3.2506120')
for needle in ['BuildAndStageDesktopComponents', 'RTSSGameBar.Setup.vcxproj', 'RTSSGameBar.RTSSPlugin.vcxproj',
               'Integration\\RTSSGameBar.Setup.exe', 'Integration\\RTSSGameBarPlugin.dll']:
    if needle not in widget_project:
        errors.append('widget staging config missing: ' + needle)
solution = (ROOT / 'RTSSGameBar.sln').read_text(encoding='utf-8')
for needle in ['RTSSGameBar.Setup', 'RTSSGameBar.RTSSPlugin']:
    if needle not in solution:
        errors.append('solution missing project: ' + needle)

create_cert = (ROOT / 'scripts/create-signing-cert.ps1').read_text(encoding='utf-8')
trust_cert = (ROOT / 'scripts/trust-signing-cert.ps1').read_text(encoding='utf-8')
install_local = (ROOT / 'scripts/install-local-package.ps1').read_text(encoding='utf-8')
prepare_release = (ROOT / 'scripts/prepare-github-release.ps1').read_text(encoding='utf-8')
for needle in ["[string]$Subject = 'CN=VirtualGIT20'", 'RTSSGameBar-Signing.pfx', 'RTSSGameBar-Signing.cer',
               '1.3.6.1.5.5.7.3.3', '-KeyLength 3072', '-HashAlgorithm SHA256', 'ValidityYears = 5', 'Never commit or publish it']:
    if needle not in create_cert:
        errors.append('signing certificate helper missing: ' + needle)
if 'Cert:\\LocalMachine\\TrustedPeople' not in trust_cert:
    errors.append('trust helper must use LocalMachine\\TrustedPeople')
if 'TrustedRoot' in trust_cert or 'Root\\' in trust_cert:
    errors.append('trust helper must not use Trusted Root')
for needle in ["$PackageName = 'VirtualGIT20.RTSSGameBar'", 'RTSSGameBar-Signing.pfx', 'trust-signing-cert.ps1']:
    if needle not in install_local:
        errors.append('local install helper missing public identity/signing reference: ' + needle)
if "Signing certificate Subject must be CN=VirtualGIT20" not in (ROOT / 'scripts/sign-release-package.ps1').read_text(encoding='utf-8'):
    errors.append('sign helper does not enforce the public publisher subject')
for needle in ['RTSSGameBar-Signing.cer', 'CN=VirtualGIT20', 'SHA256SUMS.txt', "'.pfx', '.p12', '.key'"]:
    if needle not in prepare_release:
        errors.append('GitHub release helper missing safety/output check: ' + needle)

gitignore = (ROOT / '.gitignore').read_text(encoding='utf-8')
for needle in ['**/*.pfx', '**/*.p12', '**/*.key', 'artifacts/']:
    if needle not in gitignore:
        errors.append('gitignore missing private/generated artifact rule: ' + needle)

workflow = (ROOT / '.github/workflows/static-check.yml').read_text(encoding='utf-8')
if 'python scripts/static_check.py' not in workflow:
    errors.append('GitHub Actions workflow does not run static checker')

# No development POC/RC names should remain in source code or product metadata.
source_text = '\n'.join(
    p.read_text(encoding='utf-8', errors='replace')
    for p in (ROOT / 'src').rglob('*') if p.is_file() and p.suffix.lower() in {'.cs', '.cpp', '.h', '.xaml', '.xml', '.manifest', '.csproj', '.vcxproj'}
)
for stale in ['RTSSGameBar.POC', 'Helper.POC', 'RC10', 'v0.0.20']:
    if stale in source_text:
        errors.append('development identifier remains in source/product metadata: ' + stale)

license_text = (ROOT / 'LICENSE').read_text(encoding='utf-8')
if 'MIT License' not in license_text or 'Copyright (c) 2026 VirtualGIT20' not in license_text:
    errors.append('MIT license/public copyright metadata missing')

if errors:
    print('Static checks FAILED:')
    for error in errors:
        print(' -', error)
    sys.exit(1)

print('Static checks passed.')
print(f'Checked {len(xml_files)} XML project/manifest/XAML files.')
print('RTSS Game Bar v1.0.0: public identity VirtualGIT20.RTSSGameBar; Widget/Helper protocol v19; RTSS plugin v1.0.0 on protocol v6.')
