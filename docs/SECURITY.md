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

## Local file cache and account revocation

Credential encryption does **not** encrypt rclone's VFS file cache. Cached file
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

Because older versions use one shared rclone cache for all mounts, a confirmed
wipe currently stops all ResoDrive mounts and sync jobs and removes the complete
local ResoDrive account state (settings, encrypted rclone configuration, cache,
ownership state, scheduler state and logs). The protected wipe token is retained
only until cleanup succeeds and the client acknowledges `/index.php/core/wipe/success`.
The setup flow must use a dedicated Nextcloud app password created for this client;
ordinary account passwords are not suitable for remote wipe. Existing accounts
must be reconnected through setup to create a registration.

Unreachable clients cannot receive remote wipe, and client-side cleanup cannot
prevent a hostile local user from retaining previously copied data. Ordinary file
deletion is not forensic erasure on an SSD, and Office or other applications can
create copies outside ResoDrive.
