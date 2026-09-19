# Nextcloud remote wipe: administrator guide

## Enrollment and trigger

Updating ResoDrive does not enroll existing connections. Reconnect a Nextcloud
account using ResoDrive's connection setup and a dedicated app password for that
client. Do not use the user's ordinary password or a token shared with another
application. A protected `remote-wipe.dpapi` file records the enrollment; its
presence alone does not prove that the server will accept the token.

Nextcloud's **remote-wipe action for that device/app token** is the trigger.
Disabling a user, changing a password, deleting a token, a network outage or a
server error does not authorize deletion. ResoDrive probes on host startup and
approximately every minute while running. Following HTTP 401/403, it requires
HTTP 200 with the JSON boolean `{"wipe":true}` from the same HTTPS origin.
See the [official Nextcloud protocol](https://docs.nextcloud.com/server/stable/developer_manual/client_apis/RemoteWipe/index.html).

## Exact scope

This version uses one shared cache and encrypted configuration. A confirmed wipe
therefore removes **all local ResoDrive account state**, including other ResoDrive
connections: configuration and its protected password, settings and backups,
setup staging/recovery files, registration tokens, managed cache, logs, ownership
and scheduling records. All managed work stops first. Setup states this scope.

Deployment profiles, installed components and update installers remain. Files on
the server are not deleted. User-selected sync destinations, downloaded exports,
Office temporary files and other copies outside ResoDrive's data directory are
not covered. Removing a drive or uninstalling the app is not a remote wipe.

Deletion is ordinary filesystem deletion, not forensic SSD shredding. DPAPI
protects stored credentials, not the VFS cache. An offline/stopped/modified client
cannot guarantee remote enforcement; protect the device separately.

## Recovery and failure behavior

An accepted command is written to `remote-wipe-state.dpapi` before shutdown.
The recovery host starts before settings are loaded and retries about every
minute. A crash or reboot preserves the request. It never remounts an account
while recovery is pending. Settings writes and setup rollback from a pre-wipe
window cannot restore revoked data.

Locked files, unverifiable mount ownership, redirected data/cache directories,
or failed deletion keep cleanup pending. Close applications using cached files
and resolve the filesystem problem; do not delete the recovery marker to bypass
it. Only after local cleanup succeeds does the client acknowledge
`/index.php/core/wipe/success`. A failed acknowledgement retains only the revoked
token needed to retry; successful acknowledgement replaces it with a token-free
completion marker. Nextcloud can return 404 after retiring an acknowledged token.
After local cleanup, that response also releases the token, but the completion
marker records `serverAcknowledged: false` instead of claiming an acknowledgement.
This handles a lost response or a crash between server acknowledgement and local
commit. An idle recovery host still accepts a confirmed installer
shutdown, with recovery resuming on next launch.

## Safe live test

1. Use a disposable Windows VM or Windows test account, a throwaway Nextcloud
   user and a dedicated app password. Do not put production accounts in the same
   ResoDrive data directory: the wipe scope is the entire managed directory.
2. Connect through ResoDrive setup. Mount the drive, open a dummy file so it is
   cached, then close the file. Confirm `remote-wipe.dpapi` exists without opening
   or sharing its contents.
3. Use Nextcloud's device/app-token remote-wipe action. Keep the client online.
   Expect the ResoDrive UI to close, mounts to disappear and local account/cache
   files to be removed after the next probe.
4. Check that Nextcloud reports completion, then reopen ResoDrive. It should have
   no old connections. The completion marker must no longer contain a token.
5. Repeat with a dummy cached file held open without delete sharing. Expect no
   server acknowledgement until the handle is closed and the retry succeeds.
6. Repeat while interrupting the test client after it records the request, and
   with the acknowledgement endpoint temporarily unreachable. Recovery must
   finish after restarting/reconnecting, without mounting old accounts.
7. Separately disable a throwaway account without requesting wipe. Cached data
   must remain; this is an essential negative test.

## What has been verified

Automated Windows tests exercise the real DPAPI store, actual temporary files,
locked handles, junction boundaries, owned child processes, the background
worker, and named-pipe shutdown during recovery. HTTP responses are simulated,
including unauthorized access, explicit authorization, errors, malformed and
oversized bodies, timeouts, cancellation and acknowledgement failure.

These tests do **not** establish live compatibility with a particular Nextcloud
deployment or prove forensic erasure. Complete the disposable live test above
before relying on remote wipe operationally. On the development PC inspected on
19 September 2026, no `remote-wipe.dpapi` registration was present.
