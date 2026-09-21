# Nextcloud remote wipe: administrator guide

This guide describes the current source on `main`. Fixes under **Unreleased** in
the [changelog](../CHANGELOG.md) are not in existing release packages. Server
commands and UI below were checked against Nextcloud's official documentation
and `stable32` source on 21 September 2026; live server acceptance is still required.

**A confirmed wipe removes all local ResoDrive accounts and managed cache in the
affected Windows data directory.** Read the scope before sending a request.

## Enroll a client

1. In the Nextcloud account's personal **Settings → Security → Devices & sessions**,
   create an app password named for the PC, for example `ResoDrive - Test PC`.
   Give each client its own app password; never reuse a token or the account's
   ordinary login password. See [Nextcloud's device-password guide](https://docs.nextcloud.com/server/stable/user_manual/en/session_management.html).
2. In ResoDrive connection setup, select **Nextcloud** and enter that app password.
   Use the HTTPS server base URL, including any installation subdirectory, such
   as `https://cloud.example/nextcloud`. A generic WebDAV or imported connection
   does not enroll itself. Updating ResoDrive also does not enroll older accounts;
   reconnect them through setup.
3. Complete setup and confirm the connection works. The protected
   `%LOCALAPPDATA%\rdrive\remote-wipe.dpapi` file stores registrations. If using
   `RDRIVE_DATA_DIR`, look in that directory instead. Do not open or share the file.

ResoDrive currently enrolls a manually created app password; it does not implement
Nextcloud Login Flow v2. The registration file's existence proves only that local
registration data was saved. It does not validate the server's wipe support or
prove that every connection is enrolled. Complete the disposable test below.

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
cache, logs, ownership and scheduling records. All managed mounts and sync jobs
stop first. Setup states this scope. Unsynced data in the managed cache can be lost.

Deployment profiles, installed components and update installers remain. Files on
the server are not deleted. User-selected sync destinations, downloaded exports,
Office temporary files and other copies outside ResoDrive's data directory are
not covered. Removing a drive or uninstalling the app is not a remote wipe.

Deletion is ordinary filesystem deletion, not forensic SSD shredding. DPAPI
protects stored credentials, not the VFS cache. A modified client or a person
controlling the local account can prevent enforcement or keep other copies.

## Recovery and failure behavior

An accepted command is written to `remote-wipe-state.dpapi` before shutdown.
The recovery host starts before settings are loaded and retries about every
minute. A crash or reboot preserves the request; recovery resumes when the host
next starts. It never remounts an account while recovery is pending. Settings
writes and setup rollback from a pre-wipe window cannot restore revoked data.

| State | What remains | What to do |
| --- | --- | --- |
| Cleanup pending (`Requested`) | Durable request and any data not yet deleted | Close applications using cached files. Resolve locks, permissions, unverifiable process ownership or redirected data/cache directories, then allow retry. |
| Local cleanup finished (`Cleaned`) | The revoked token needed for acknowledgement | Keep the PC online and restore access to the Nextcloud endpoint. No account is remounted while acknowledgement is pending. |
| Complete (`Completed`) | A token-free generation marker | Reopen ResoDrive. Old accounts are absent; any new connection needs a new dedicated app password. |

Do not delete the recovery marker or restore old settings to bypass pending
cleanup. An unreadable recovery state blocks account access. A foreground launch
during recovery reports that cleanup is continuing and then closes the UI; the
background host handles retries. An idle recovery host still accepts a confirmed
installer shutdown, with recovery resuming on next launch.

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

No live Nextcloud wipe was run during this review. These tests do **not** establish
live compatibility with a particular deployment or prove forensic erasure.
Complete the disposable acceptance test above before relying on remote wipe
operationally, including after changes to authentication, proxies or server versions.
