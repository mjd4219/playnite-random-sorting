# playnite-random-sorting

Random Sorting is a Playnite plugin that lets game lists be sorted in a random order.

The plugin assigns each eligible game a generated Tag or Category, then you can sort your library by that field. By default it uses Tags with the prefix `z[Random] `. Collision-free labels are enabled by default, so each randomized game receives a unique shuffled rank label such as `z[Random] 00001`.

## Features

- Assign random sort labels as either Tags or Categories.
- Run manually, when Playnite starts, and/or when a game starts.
- Filter eligible games by a required Playnite Feature.
- Optionally include uninstalled games and hidden games.
- Avoid overlapping automatic runs by cancelling, skipping, or queueing one rerun.
- Use collision-free rank labels, or switch back to legacy five-digit random labels.
- Clean up unused plugin-created labels safely.
- Use manual progress dialogs for assignment and cleanup.
- Tune automatic/manual database batch sizes and enable detailed timing logs.

## Cleanup

Random Sorting only cleans labels that start with the configured prefix and are not used by any game. It does not remove unrelated user-created Tags or Categories.

Automatic cleanup is optional and disabled by default for startup/game-start runs. Manual cleanup is recommended after large setting changes, such as changing the feature filter or switching hidden/uninstalled game inclusion.

## Performance Notes

Large Playnite libraries can require many database updates. Automatic runs are chunked to keep Playnite responsive, and heavy planning work is kept off the UI thread where safe. If Playnite Fullscreen feels sluggish during automatic updates, lower the automatic update batch size or disable automatic cleanup.
