# Urban 888

Urban 888 is a Dalamud plugin for running FFXIV venue raffles.

The plugin tracks paid `/random` attempts for a configured target player. Each purchase adds gil to the saved sales ledger and jackpot, and the target player wins when they roll the configured winning number, default `888`.

## Current features

- Main, Statistics, and Config tabs.
- Target-player raffle tracking.
- Saved sales ledger with one row per sale.
- Configurable base pot, price per random, and winning random.
- Optional target-only roll counting.
- Optional stop when winner is found.
- Optional `/say` notices for halfway and final roll notices.
- Shout game advertisement using the configured price.

## Development

Open `Urban888.sln` in Visual Studio or Rider.

Build output is created under:

```text
Urban888/bin/x64/Debug/
```

For local Dalamud testing, add the built plugin DLL from the output folder to Dalamud Dev Plugin Locations.
