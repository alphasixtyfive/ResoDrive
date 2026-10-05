# UI input and visual acceptance

Read this guide before changing an editable setting, form, input parser, or save
handler. These are engineering requirements, not optional polish. A successful
build alone does not establish that the interaction is correct.

## Define the input contract first

For each editable field, identify its type, allowed values, bounds, units, empty
state, stored representation, and existing shared validator. Apply the same
contract when typing, pasting, loading existing settings, and saving. Preserve
supported custom values even when they are absent from the preset list.

- Use a checkbox or selection for modes such as Unlimited. Never place a keyword
  in a numeric input or expect the user to know a storage sentinel. Reconnect
  attempts use a separate Unlimited checkbox (stored as 0) and a finite number
  from 1 to 100.
- Reject missing or unknown required selections. Do not silently choose a sync
  direction, cache mode, or destructive operation because parsing failed.
- Never clamp, truncate, or replace invalid active input with a default. In
  particular, a numeric MaxLength can turn pasted 1000 into valid-looking 100.
  Parse the complete value and check the range and overflow explicitly.
- Use invariant, explicit numeric parsing. Units, timestamps, and advanced
  options must follow the supported backend syntax; accepting arbitrary strings
  only defers the error until an operation runs.
- Normalize only documented benign forms, such as surrounding spaces in a name.
  Reject control characters and malformed paths before normalization can hide
  them. Do not silently correct traversal, a malformed URL, or a mistyped port.
- Validate resource selections against available and configured resources.
  Retain an existing drive's own letter; do not offer unchecked alternatives
  when drive detection fails. Recheck conflicts at the save/operation boundary.

## Validate before side effects or closing

Input filtering improves editing but is not a validation boundary. Every Connect,
Save, import, and persistence path must validate the full candidate settings.
Reuse the domain validators and mapper rather than maintaining weaker UI rules.
Check catalog conflicts as well as individual records. Invalid data must not
close the editor, start provisioning, write settings, or launch backend work.
Keep persistence validation even when the UI validates successfully.

The current editable-input inventory is:

| Surface | Input contract / validation boundary |
| --- | --- |
| Drive and Setup | Names: required, at most 128 characters, no control characters or duplicate drive names. Remote folders: shared remote-path rules, at most 2048 characters, no traversal/backslashes/repeated separators. |
| Drive and Setup | Drive letter: available D–Z choice; retain the existing drive's own letter. Boolean controls store booleans. Connection metadata in Drive settings is read-only. |
| Drive recovery | Reconnect and Unlimited are independent checkbox states; an active finite cap is a whole number 1–100. Disabled values and toggled drafts are preserved. |
| Manual Setup | Required supported storage/authentication selections. SFTP host: valid DNS name or IP; port: whole number 1–65535. HTTPS URL: no embedded credentials, query, fragment, backslashes, or controls; Nextcloud requires the server origin. |
| Setup credentials | Shared username rules (at most 256 characters) and secret normalization/validation (at most 2048 characters). A selected key file is checked for existence/access by provisioning before remote work. Never include secrets in validation feedback or logs. |
| Sync | Required supported drive and operation; unique job name on its drive; shared remote/local path validators. Local folders must be absolute, without reserved syntax, traversal, or a volume root. Managed copies retain their separate ownership checks. |
| Sync schedule | Active interval: whole number 5–1440 minutes. Preserve a disabled saved interval, including the supported manual-schedule representation. |
| Cache and advanced options | Required supported cache mode; backend-compatible sizes/durations and approved typed options. Preserve custom and inactive values. Reject overflow and missing, duplicate, unsupported, or manager-owned options. |
| Main Settings and import | The two application preferences are booleans. Settings/import still validate schema, individual records, and catalog conflicts at persistence; UI preflight does not replace this boundary. |

Give short, actionable inline feedback, associated with the affected control.
Announce changed errors through the accessibility live region and focus the
offending field when submitting. Explain the allowed range or format. An error
must not replace a loading/resource warning or produce repeated popups while
typing. Disable Save for known invalid active numeric input and still validate
inside the handler.

Show one concise actionable error at a time in a form footer. Keep the underlying
validation exhaustive, but do not join dozens of issues or echo an unbounded
input string outside the scroll surface. Test many invalid advanced options and
an oversized unknown option name; Cancel and Save must remain visible.

Disabled controls are not permission to discard their values. Preserve valid
stored numbers and custom options while their feature is disabled, and preserve
the editing draft when toggling off and back on. Ignore an invalid inactive draft
only if the save keeps the last valid stored value and does not silently change
the setting. An explicit mode such as Unlimited must map through its control.

## Loading and layout

Open dialogs promptly with a clear loading state. Populate dependent values once
before enabling editing; a late callback must not overwrite the user's input.
Cancel work when the window closes and keep Save unavailable until required
detection completes. Preserve field and button positions as loading finishes.

Align labels, controls, and disclosure headers using the shared form styles.
Show read-only values as visibly disabled. Keep controls visible when toggled
unless hiding them materially simplifies the form; changing enabled state often
avoids unnecessary layout movement. Reserve natural scrollbar space, wrap error
messages, and ensure the editor can scroll without hiding Cancel or Save.

## Required verification

Add focused regression coverage for a discovered input bug, including the real
save/candidate boundary rather than only mirroring the visual implementation.
For affected inputs, cover:

- Empty and intermediate editing states; minimum, maximum, outside bounds, and
  overflow; malformed text, control characters, and unsupported units.
- Typing and paste, including replacing selected text and pasting an oversized
  number without truncation; programmatic assignments that bypass filtering.
- Loading and saving existing custom values, disabled values, missing selections,
  and off/on toggles without accidental changes.
- Invalid submission leaving the form open without persistence/provisioning;
  valid submission producing the exact intended stored value.
- Async loading, cancellation, reopening, and failure paths without value resets
  or incorrectly enabled Save.

Render or run the actual compiled UI with normal and invalid values. Inspect the
minimum supported window size and 100%, 150%, and 200% display scaling. Check
alignment, wrapping, disabled appearance, scrolling, keyboard focus, and the
positions of Cancel and Save. A mockup, XML assertion, or passing parser test is
not a substitute for this visual check. Record which checks actually ran.

## Publication gate

Follow [the release procedure](RELEASING.md). When the user asks to see the UI
before publication, show screenshots or the running candidate first and leave
the release held for their review. Earlier permission to publish does not
override a later request to hold or preview. Do not publish while a known input
or interaction defect is unresolved. Keep published tags immutable and use a
new version for a corrected build.

For the October 2026 correction, verify separate Unlimited/finite controls,
strict attempts and port limits, preserved disabled sync intervals, required
mode selections, and validated cache/sync advanced options. The withdrawn
0.3.25 release remains held until its replacement is reviewed and accepted.
