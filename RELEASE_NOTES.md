# RTSS Game Bar v1.0.1

Maintenance release focused on Windows 11 25H2 integration compatibility.

Highlights:

- Replaced the elevated .NET Framework integration setup executable with a native x64 Win32 implementation.
- Preserved the existing Integration `Install` / `Update` / `Remove` command-line and exit-code contract.
- Avoids the packaged/elevated CLR bootstrap hang reproduced on Windows 11 25H2 builds 26200.8973 and 26200.9168.
- Integration Update, Remove, and Install were validated on build 26200.9168 with no lingering setup process.
- Widget/Helper IPC remains protocol v19.
- RTSS plugin IPC remains protocol v6 and the bundled plugin remains v1.0.0.

RTSS is required and is not bundled. RTSS Game Bar is an independent third-party project.

For self-signed GitHub sideload builds, import the supplied public `RTSSGameBar-Signing.cer` into `LocalMachine\TrustedPeople` before installing the signed app package. The private PFX is never distributed.
