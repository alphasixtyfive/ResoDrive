# Nextcloud remote wipe: administrator guide

This guide describes ResoDrive 0.3.11. Earlier packages do not include all recovery
fixes listed in the [changelog](../CHANGELOG.md). Server commands and UI below
were checked against Nextcloud's official documentation
and `stable32` source on 21 September 2026; live server acceptance is still required.

**A confirmed wipe removes all local ResoDrive accounts, managed cache and managed
download copies in the affected Windows data directory.** Read the scope before
sending a request.

## Existing connections after an update

Existing Nextcloud app-password connections do not need to be re-added. On startup,
ResoDrive reads their saved URL, username and app password from its encrypted
rclone configuration and adds any missing entries to its protected wipe-check
list. This is local bookkeeping; it does not create a server registration or
change a password. It works offline, with server checks resuming when connected.

The host checks for a pending wipe before starting automatic drives. It checks
the saved connection list again during its normal minute-by-minute monitoring;
unchanged files do not launch another configuration reader. **Remote wipe
configured** on a drive means its local wipe-check entry exists. **Remote wipe
setup needs attention** means the saved connection could not be safely recovered.

Recovery recognizes Nextcloud WebDAV connections and generic WebDAV connections
using standard `/remote.php/dav/files/<user>` or `/remote.php/webdav` URLs. Existing
Nextcloud installation subdirectories are preserved. Public shares, HTTP URLs,
bearer-token connections and ambiguous paths are excluded. This uses the existing
app password, so a password shared across PCs retains that shared scope. Give each
PC a separate app password when first setting it up.

## Connect a new client

1. In the Nextcloud account's personal **Settings → Security → Devices & sessions**,
   create an app password named for the PC, for example `ResoDrive - Test PC`.
   Give each client its own app password; never reuse a token or the account's
   ordinary login password. See [Nextcloud's device-password guide](https://docs.nextcloud.com/server/stable/user_manual/en/session_management.html).
2. In ResoDrive connection setup, select **Nextcloud** and enter that app password.
   Use the HTTPS server address accepted by the setup profile, such as
   `https://cloud.example`. The host also recovers compatible imported connections
   as described above.
3. Complete setup and confirm the connection works. The protected
   `%LOCALAPPDATA%\rdrive\remote-wipe.dpapi` file stores registrations. If using
   `RDRIVE_DATA_DIR`, look in that directory instead. Do not open or share the file.

ResoDrive reuses a manually created app password; it does not implement
Nextcloud Login Flow v2. The registration file's existence proves only that local
registration data was saved. It does not validate the server's wipe support or
prove that every connection is enrolled. Complete the disposable test below.

## Include downloaded copies

For **Copy remote to local** and **Mirror remote to local** jobs on an enrolled
Nextcloud account, choose **Managed local copy** in the sync editor. New download
jobs on enrolled accounts use this choice by default; existing jobs keep their
previous paths and behavior. The editor shows the exact managed destination.

Managed copies live at `%LOCALAPPDATA%\rdrive\managed-sync\<job-id>` (or below the
active `RDRIVE_DATA_DIR`). ResoDrive owns this dedicated tree. A confirmed wipe
deletes everything inside it, including local edits or files manually placed
there. Removing a sync job or changing its destination does not remove old managed
copies from wipe coverage. The client never takes deletion targets from editable
sync paths; each managed job must use its assigned directory and a download mode.

**Existing external sync folders are not enrolled automatically.** Switching an
existing job to managed storage downloads to a different directory; old external
copies remain outside wipe coverage. Review those copies separately before
deploying a device. Upload-source folders, arbitrary exports and copies moved out
of managed storage are not wiped. Other WebDAV services, SFTP and connections
without a recovered Nextcloud wipe entry cannot enable managed local copies.

## Send the wipe request

### One device: the account owner's web UI

Open personal **Settings → Security → Devices & sessions**, find the exact
ResoDrive app-password entry, open its **Device settings** menu, choose
**Wipe device**, and confirm. Nextcloud marks that token for wipe. The action and
confirmation are defined in Nextcloud's [device-token UI](https://github.com/nextcloud/server/blob/stable32/apps/settings/src/components/AuthToken.vue).

Leave the pending token in place until the client acknowledges cleanup. **Revoke**
or token deletion can remove the request before an offline client receives it.
Disabling the account, changing its password, or deleting a token alone does not
authorize ResoDrive to delete data.

### Administrator command: all devices for one Nextcloud user

Nextcloud's provisioning API can mark **all eligible device tokens belonging to
the target user** for wipe. This can affect other applications and other PCs, not
just ResoDrive. Use the individual-device UI above when that broader scope is not
intended. The caller must have authority to manage the target account, and cannot
target their own account through this endpoint.

This PowerShell example uses `curl.exe` and prompts for the administrator's
password/app password so it is not included in command history. Replace the
example HTTPS base, administrator login and target user ID; percent-encode the
target ID if it contains characters requiring URL encoding. Keep any Nextcloud
installation subdirectory before `/ocs/`.

```powershell
curl.exe --include --user "admin-user" --request POST --header "OCS-APIRequest: true" --header "Accept: application/json" --header "Content-Type: application/x-www-form-urlencoded" "https://cloud.example/nextcloud/ocs/v2.php/cloud/users/disposable-user/wipe?format=json"
```

Check both the HTTP response and the returned `ocs.meta` for success. Acceptance
queues the request; it does not prove a client received it, that any eligible
tokens existed, or that cleanup completed. The [provisioning API](https://docs.nextcloud.com/server/stable/admin_manual/configuration_user/user_provisioning_api.html),
[route](https://github.com/nextcloud/server/blob/stable32/apps/provisioning_api/appinfo/routes.php)
and [controller](https://github.com/nextcloud/server/blob/stable32/apps/provisioning_api/lib/Controller/UsersController.php)
define the authentication, scope and request behavior.

On the Nextcloud server, this read-only command lists token IDs, names and types:

```sh
sudo -u www-data php /var/www/nextcloud/occ user:auth-tokens:list disposable-user
```

Adjust the web-server user and installation path for your deployment. In the
reviewed `stable32` source, the token type is `wipe` while pending and the token is
removed after acknowledgement. Token disappearance alone is not proof of cleanup:
manual revocation and server retention can also remove it. See the
[list command](https://github.com/nextcloud/server/blob/stable32/core/Command/User/AuthTokens/ListCommand.php).

`occ user:auth-tokens:delete` is a revocation command, **not a wipe command**; the
reviewed implementation has no `--wipe` option. Its `--cancel-wipe` option explicitly
discards a pending request. Check the installed version's help before using it.
See the [delete command](https://github.com/nextcloud/server/blob/stable32/core/Command/User/AuthTokens/Delete.php).

## What the client accepts

The host probes on startup and approximately every minute while running, even
when no drive is mounted. A stopped or offline client cannot receive the request
until it runs and can reach Nextcloud again. Each account uses its own app token
without shared session cookies. HTTPS origin and port must match the registered
server, redirects are not followed, and installation subdirectories are preserved.

These requests include the running ResoDrive version and available Windows
edition/release/build details in their HTTP User-Agent. See
[storage client identification](SECURITY.md#storage-client-identification) for
the fields and server-log visibility. This does not add a separate reporting API.

The [Nextcloud client protocol](https://docs.nextcloud.com/server/stable/developer_manual/client_apis/RemoteWipe/index.html)
is implemented in this order:

1. An authenticated WebDAV probe returns HTTP 401 or 403.
2. The client POSTs its app token to `index.php/core/wipe/check`.
3. Only HTTP 200 with one unambiguous JSON boolean `"wipe": true` authorizes cleanup.
   Ordinary credential rejection, network errors, redirects, malformed responses,
   timeouts and `wipe: false` leave local data intact.
4. The client persists the accepted request, stops managed work and removes local
   account data. Only after successful cleanup does it POST to
   `index.php/core/wipe/success` with the revoked token.

The two `/core/wipe/` endpoints are used by the client; calling them manually does
not request a wipe. In particular, manually calling `/success` can retire the
token without deleting any client files.

## Exact scope

This version uses one shared cache and encrypted configuration. A confirmed wipe
therefore removes **all local ResoDrive account state in the active data directory**,
including other ResoDrive connections: configuration and its protected password,
settings and backups, setup staging/recovery files, registration tokens, managed
cache, managed download copies, logs, ownership and scheduling records. All managed mounts and sync jobs
stop first. Setup states this scope. Unsynced cache data and local changes inside
managed download folders can be lost.

Deployment profiles, installed components and update installers remain. Files on
the server are not deleted. External user-selected sync destinations, downloaded exports,
Office temporary files and other copies outside ResoDrive's data directory are
not covered. Removing a drive or uninstalling the app is not a remote wipe.

Deletion is ordinary filesystem deletion, not forensic SSD shredding. DPAPI
protects stored credentials, not the VFS cache. A modified client or a person
controlling the local account can prevent enforcement or keep other copies.

## Lost or stolen devices

ResoDrive is an application-level cleanup mechanism. It cannot factory-reset a
Windows PC, force an offline device to connect, or erase copies already taken by
someone controlling the unlocked account. A pending request runs only while the
ResoDrive host can run and reach the enrolled Nextcloud server. Enable start at
sign-in as part of deployment, and test the actual client/server combination.

Protect every volume holding application data or downloaded files with
[BitLocker or managed device encryption](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/index)
before a device goes missing. This protects data at rest; it does not make an
unlocked session safe. If the requirement is to reset the whole Windows device,
enroll it in an appropriate device-management service and test its
[device-wipe procedure](https://learn.microsoft.com/en-us/intune/device-management/actions/wipe).
That is separate from Nextcloud's application-token wipe.

For a lost ResoDrive client, request **Wipe device** for its dedicated token and
retain the pending token until acknowledgement. Do not treat a queued command,
token revocation or an absent device as proof that files were removed. Follow your
organization's incident-response policy for other sessions and exposed credentials.

## Recovery and failure behavior

An accepted command is written to `remote-wipe-state.dpapi` before shutdown.
The recovery host starts before settings are loaded and retries about every
minute. A crash or reboot preserves the request; recovery resumes when the host
next starts. It never remounts an account while recovery is pending. Settings
writes and setup rollback from a pre-wipe window cannot restore revoked data.

| State | What remains | What to do |
| --- | --- | --- |
| Cleanup pending (`Requested`) | Durable request and any data not yet deleted | Close applications using cached or managed download files. Resolve locks, permissions, unverifiable process ownership or redirected managed directories, then allow retry. |
| Local cleanup finished (`Cleaned`) | The revoked token needed for acknowledgement | Keep the PC online and restore access to the Nextcloud endpoint. No account is remounted while acknowledgement is pending. |
| Complete (`Completed`) | A token-free generation marker | Reopen ResoDrive. Old accounts are absent; any new connection needs a new dedicated app password. |

Do not delete the recovery marker or restore old settings to bypass pending
cleanup. An unreadable recovery state blocks account access. A foreground launch
during recovery reports that cleanup is continuing and then closes the UI; the
background host handles retries. An idle recovery host still accepts a confirmed
installer shutdown, with recovery resuming on next launch.

Recovery stops recorded mount processes only after verifying their identity. If a
sync process survived a host crash and still runs from this data directory's
private rclone executable, recovery waits for it to exit rather than deleting
files it could recreate. It does not kill an unrecorded process. An inaccessible
candidate process also blocks completion; resolve the process or restart the
device and let recovery retry. No success acknowledgement is sent while this
check is unresolved.

HTTP 200 from `/success` records `serverAcknowledged: true`. HTTP 404 after local
cleanup releases the token but records `serverAcknowledged: false`: the server no
longer recognizes a pending token. This can follow a lost success response, manual
revocation or expiry, and must not be reported as confirmed acknowledgement. Other
responses retain the token for retry. The [server implementation](https://github.com/nextcloud/server/blob/stable32/lib/private/Authentication/Token/RemoteWipe.php)
retires the token on successful acknowledgement; [the endpoint](https://github.com/nextcloud/server/blob/stable32/core/Controller/WipeController.php)
distinguishes HTTP 200 from 404.

## Disposable live acceptance test

1. Use a disposable Windows VM or test account, a throwaway Nextcloud user and a
   dedicated app password. Keep production connections out of the entire test
   data directory. Record the ResoDrive commit/package and Nextcloud version.
2. Enroll through Nextcloud setup. Mount the drive, open a dummy file so it is
   cached, then close it. Confirm `remote-wipe.dpapi` exists without exposing its
   contents. Put a separate dummy file outside the managed directory as a scope check.
3. Request **Wipe device** for this token. Keep the host running and online. After
   a probe, expect the UI to close, mounts to disappear and account/cache files to
   be removed. Empty managed cache/log directories may be recreated.
4. Verify the server records completion and the token is retired, then reopen
   ResoDrive and verify old connections are absent. Check the outside dummy file
   and server files remain. The completion state must contain no registration/token;
   the protected marker itself remains and is not a plaintext JSON file.
5. With a fresh token/data set, hold a dummy cached file open without delete
   sharing, then request wipe. Expect cleanup to remain pending and **no success
   acknowledgement** until the handle closes and retry succeeds.
6. Repeat with an interruption after request persistence, and with the success
   endpoint temporarily unavailable. Restart/reconnect the test client and verify
   recovery finishes without loading or mounting old accounts.
7. Enroll two throwaway users on the same Nextcloud origin. Request wipe for one
   token after the other has authenticated; session cookies must not mask the
   revoked token. All local ResoDrive account data is still in scope.
8. Separately disable or revoke a throwaway account **without** a wipe request.
   Cached data must remain. Test the administrator-wide command only with an
   isolated target user whose every device is disposable.
9. Create a managed download job, download dummy files and add a dummy local edit
   inside its assigned folder. Remove the job, request wipe and verify the folder
   is still removed. Keep a separate external sync folder and verify it survives.
10. Hold a managed download file open without delete sharing and repeat the wipe.
    Check that it remains pending with no success acknowledgement across restart,
    then unlock it and verify completion. Repeat using a disposable directory
    junction and an outside sentinel; cleanup must refuse the redirected path
    and preserve the outside file.
11. Repeat with an older ResoDrive connection whose app password is saved but has
    no local wipe-check entry. Update to 0.3.11 and confirm **Remote wipe configured**
    appears without another login. Also queue a wipe before starting the updated
    host: cleanup must begin before automatic drives mount. Existing external
    sync folders must remain outside the wipe scope.

Record request acceptance, client cleanup, server acknowledgement and preserved
out-of-scope files separately. Do not treat a successful API command or missing
token as a substitute for checking the disposable client.

## What has been verified

Automated Windows tests exercise the real DPAPI store, actual temporary files,
locked handles, junction boundaries, owned child processes, the background
worker, and named-pipe shutdown during recovery. HTTP responses are simulated,
including unauthorized access, explicit authorization, errors, malformed and
oversized bodies, timeouts, cancellation and acknowledgement failure. A local
loopback transport test separately verifies cookie and redirect isolation.
Existing-connection recovery was also checked against a real rclone process using
an encrypted disposable configuration. The host startup test uses simulated
Nextcloud responses to verify that a recovered pending request stops startup
before automatic drives mount.

No live Nextcloud wipe was run during this review. These tests do **not** establish
live compatibility with a particular deployment or prove forensic erasure.
Complete the disposable acceptance test above before relying on remote wipe
operationally, including after changes to authentication, proxies or server versions.
