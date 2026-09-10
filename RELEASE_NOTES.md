# RTSS Game Bar v1.0.2

Stability release focused on Xbox Game Bar widget lifecycle and local IPC reliability.

Highlights:

- The Helper named-pipe server now accepts multiple persistent Widget sessions. A stale Game Bar widget process can no longer monopolize the pipe and cause a replacement widget to fail with an IPC timeout.
- Game Bar visibility, theme, opacity, refresh, load, and unload paths are hardened against asynchronous lifecycle races and invalidated WinRT/COM objects.
- Status LED brushes are recreated for the active XAML view instead of caching `SolidColorBrush` dependency objects across Game Bar view teardown/recreation. This addresses the observed `InvalidComObjectException` (`0x80131527`).
- Targeted local diagnostics remain for IPC failures/timeouts and lifecycle exceptions, while verbose per-request success tracing used during investigation has been removed.
- The native x64 integration Setup introduced in v1.0.1 is unchanged.
- Widget/Helper IPC remains protocol v19.
- RTSS plugin IPC remains protocol v6 and the bundled plugin remains v1.0.0.

RTSS is required and is not bundled. RTSS Game Bar is an independent third-party project.

For self-signed GitHub sideload builds, import the supplied public `RTSSGameBar-Signing.cer` into `LocalMachine\TrustedPeople` before installing the signed app package. The private PFX is never distributed.
