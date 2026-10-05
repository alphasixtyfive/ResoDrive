This update cleans up drive settings, transfer status and the log, and fixes a few rough edges when setting up or updating ResoDrive.

- Routine upload checks stay quiet. Transfer progress appears when there is work to show, and drive cards give activity and warnings their own space.
- Drive settings open straight away with a small loading message. The server address and port are clearly read-only, field values stay stable while loading, and drive-letter choices avoid letters already in use.
- Settings have more breathing room, shorter labels and consistent Advanced sections. Reconnect attempts use an Unlimited checkbox or a numeric limit from 1 to 100.
- Setup and sync catch invalid inputs before connecting or saving. Errors appear beside the relevant controls, and custom values are preserved when an option is switched off.
- The log shows compact entries with timestamps, useful details and room for the scrollbar. Extra event popups and oversized blocks are gone.
- Installation and updates use one progress window. Pending uploads still block installation and explain what needs attention.
- Windows keeps one app entry for repair and removal. Setup no longer leaves a separate entry behind after installing the app.

This release includes all of the changes from the withdrawn 0.3.25 update and the fixes reviewed in the local test builds.

Download **ResoDrive-Setup.exe** below. If an older version was installed using Setup, use this download for this update. It replaces the old installer entry and preserves your settings and cache. After that, you can use the in-app updater normally.
