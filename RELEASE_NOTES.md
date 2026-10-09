This candidate improves recovery when an update has installed but ResoDrive cannot finish moving its data folder.

- Account checks request only the Windows permissions needed to read process identity, avoiding unnecessary access failures.
- Processes closing during inspection can finish exiting without being mistaken for a live process with an unknown account. Live background work and pending uploads remain protected.
- Migration failures retain sanitized diagnostic details and an error ID for support.

This candidate has not been published. The reported Windows 10 installation recovered before diagnostics were collected, so its original failing process is not established.
