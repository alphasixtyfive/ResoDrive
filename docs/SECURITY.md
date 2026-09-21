# Security design

Report suspected vulnerabilities privately through the repository's GitHub
Security Advisory form. Do not open a public issue containing exploit details,
credentials, private service addresses, configuration files, or unredacted logs.

- The manager runs as the interactive user. WinFsp is a separate machine-level
  prerequisite; ResoDrive links only to its official release page.
- Managed processes are identified by mount ID, PID, process creation time,
  canonical executable path, source, and target before any stop operation.
- External rclone processes are visible but never terminated by default.
- Manager-owned rclone arguments are constructed internally. User tuning options
  pass through a strict token policy at import, save, load, and launch time.
- Provider configuration is encrypted by rclone with a generated password stored
  using Windows CurrentUser DPAPI. Moving the application for the same user keeps
  access; copying the data to another Windows user or machine requires reconnection.
- Adjacent profiles are editable but strictly validated as HTTPS destinations.
  Setup displays the exact endpoint before credentials are sent. Executable update
  origins are not profile-configurable.
- Mirror destinations reject roots, protected locations, mount targets, overlapping
  jobs, and reparse-point changes. Mirror runs require explicit confirmation.
- The initial rclone download is pinned and checksum-verified. Runtime rclone
  updates use rclone's official signed self-update flow, are explicit, and are
  staged and version-checked before replacement. Interrupted downloads resume from
  a partial file, but the complete archive must still match the pinned checksum.
- ResoDrive application update checks follow the build-configured GitHub latest
  release link without using the REST API, accept only stable semantic versions,
  and download only HTTPS assets under the configured repository path. Installation
  requires confirmation, SHA-256 verification, strict helper/path validation, and
  Windows elevation. A durable helper records the MSI result and reopens the app.
  Partial application downloads are reusable only if the completed MSI passes the
  published SHA-256 check.
- The public setup bundle contains the MSI and downloads a pinned .NET Desktop
  Runtime package only when the required runtime is missing. Direct MSI deployment
  is intended for managed machines where that prerequisite is already present.
- UI logs automatically redact common secrets, credential-bearing URLs, host
  names, and absolute paths. Free-form error text cannot be classified perfectly,
  so review log contents before sharing them.
- Diagnostic exports use an allowlist of component versions, numeric performance
  options, enumerated states and UI error IDs. They do not include raw configuration,
  raw exception messages, process arguments, server addresses, paths or account names.
- Mount upload statistics are fetched from authenticated IPv4 loopback control
  endpoints with proxies and redirects disabled, a three-second deadline, and a
  bounded response size. An unavailable result is never presented as zero uploads.

## Storage client identification

WebDAV setup, Nextcloud access/wipe checks and managed rclone storage requests
identify the client with a `User-Agent` such as:

```text
ResoDrive/0.3.11 (Windows 11; Edition: Professional; Release: 24H2; Build: 10.0.26100.9457; ClientArchitecture: x64; OSArchitecture: x64)
```

This is an example, not a minimum supported build. The product version comes from
the running build. Windows details are read locally without elevation and cached
for the process lifetime; restart ResoDrive to refresh them after OS changes.
Missing or unreadable optional registry details are omitted. Edition identifiers
are preserved, including LTSC variants; Windows Server is distinguished from
Windows 10/11 when its installation type is available. No hostname, account name,
device identifier, file path or installed-software inventory is added to the
header. Existing authenticated requests still carry the credentials they require.

The additional OS details are sent to configured storage services. GitHub update
checks and component downloads do not use this enriched header. An inherited
`RCLONE_USER_AGENT` override is respected by managed rclone processes. SFTP does
not use HTTP User-Agent headers.

Server or reverse-proxy access logs can record the header when configured to
include it. It does not add version columns to Nextcloud's device list, rename
existing app-password entries, or create automatic update warnings. This uses
ordinary HTTP client identification; no companion app or reporting endpoint is
required. See [Nextcloud's desktop client identification](https://github.com/nextcloud/desktop/blob/master/src/common/utility.cpp)
and [rclone's user-agent flag](https://rclone.org/docs/#user-agent-string).

## Local file cache and account revocation

Credential encryption does **not** encrypt rclone's VFS cache or managed download copies. Cached file
contents remain local after disconnecting, revoking server access, upgrading or
uninstalling. Use separate Windows accounts and disk encryption such as BitLocker
to protect stored data. Neither protects it from someone controlling the unlocked
user session. Ordinary deletion is not a promise of forensic erasure on an SSD,
and copies created by Office or other applications can exist outside ResoDrive.

Nextcloud accounts provisioned through this version keep a DPAPI-protected
remote-wipe registration. The background host probes the account's WebDAV
endpoint and only contacts `/index.php/core/wipe/check` after an authenticated
request returns 401 or 403. Local data is removed only when that endpoint returns
the explicit JSON value `{"wipe":true}`. An ordinary credential rejection,
disconnected server, timeout, malformed response, or disabled account without an
active wipe request leaves local data intact.

Because ResoDrive currently uses one shared rclone cache for all mounts, a confirmed
wipe currently stops all ResoDrive mounts and sync jobs and removes the complete
local ResoDrive account state (settings, encrypted rclone configuration, cache, managed download copies,
ownership state, scheduler state and logs). The protected wipe token is retained
only until cleanup succeeds and `/index.php/core/wipe/success` returns HTTP 200,
or returns 404 because the token can no longer be acknowledged. A 404 releases
the token without claiming server-confirmed completion.
The setup flow must use a dedicated Nextcloud app password created for this client;
ordinary account passwords are not suitable for remote wipe. On startup and when
saved connections change, ResoDrive recovers the local wipe-check list from the
existing encrypted rclone configuration. This also covers older/imported Nextcloud
app-password connections using standard authenticated DAV URLs. It reuses their
saved credentials without creating, rotating or changing a password or contacting
an enrollment API. The recovered list is Windows-protected, and a pending wipe is
checked before automatic drives start. The drive row shows whether local setup is
configured or needs attention. This status does not certify server compatibility.

Enrollment reads are serialized with setup and wipe. Configuration inspection is
limited to the selected remotes, kept in memory and excluded from diagnostics.
Redirects, insecure URLs, public-share endpoints, bearer authentication and
ambiguous paths are not enrolled automatically. Each PC still needs its own app
password: recovering a shared token does not make it device-specific.

Accepted wipes are persisted before shutdown. Cleanup includes backups and setup
recovery files, and must finish before acknowledgement. An interrupted wipe or
failed acknowledgement resumes without loading old accounts. A token-free
completion marker prevents a stale UI session from restoring account state.
See [the administrator guide and test procedure](REMOTE-WIPE.md) for the exact
scope, recovery behavior and limits of the automated verification.

Enrolled Nextcloud download jobs can use a dedicated managed local copy under
`managed-sync/<job-id>` in the active data directory. New enrolled download jobs
default to managed storage; existing external folders remain unchanged. Backend
checks require the assigned path, a download direction and Nextcloud enrollment.
Upload originals and external sync folders are never implicitly enrolled. Managed
copies remain in wipe scope after a job is removed, because cleanup deletes the
fixed managed tree independently of settings. Locked, inaccessible or redirected
managed data prevents acknowledgement. Switching to managed storage does not
remove existing external copies.

Unreachable clients cannot receive remote wipe, and client-side cleanup cannot
prevent a hostile local user from retaining previously copied data. Ordinary file
deletion is not forensic erasure on an SSD, and Office or other applications can
create copies outside ResoDrive.
