This small native WixStdBA BAFunctions extension uses the pinned official WiX 5.0.2
API headers. WixStdBA retains the installer UI, elevation, progress and lifecycle.
The extension reads MSI registration and its package-owned mode component before
preserving the installed choice. An explicit 0/1 override is validated again at
planning; an equal-version opposite choice is rejected. MSI also enforces the
equal-version boundary for direct package installation.

`ResoDriveSuppressLaunch=1` lets a coordinating installer finish profile creation
before opening the app. It clears the standard Burn `LaunchTarget` variable via
the native engine API, so WixStdBA hides its disabled Launch button. The public
default is 0, preserving its normal Open ResoDrive button. Values other than 0/1
are rejected before planning. It does not change the selected payload or install
behavior.

The capability notice checks the documented `QueueUserAPC2` export and records
the Setup process's shadow-stack policy. Missing exports are reported as missing
capabilities. Export presence does not prove that all runtime APC features work;
Setup's policy does not establish the future application's effective policy.
This extension never queues an APC, changes security settings, installs software
or automatically selects the reduced-protection build.

Build with `build.ps1 -OutputDirectory <path>`. Only its DLL is embedded in Setup;
its PDB is retained with release symbols. ApiDependencies.csproj restores headers
with a lock file and produces no managed payload. See the official WiX
[BAFunctions payload contract](https://docs.firegiant.com/wix/schema/wxs/payload/)
and [WiX 5.0.2 sample](https://github.com/wixtoolset/wix/tree/v5.0.2/src/ext/Bal/Samples/bafunctions).
