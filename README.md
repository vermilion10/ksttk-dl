# ksttk-dl

Asset downloader and extractor for a certain browser game, as a desktop app
for browsing and a command-line tool for bulk downloads. Fan-made, not
affiliated with the game's developers or publishers. This repository contains
no game assets; everything is downloaded from the game's CDN to your computer
when you use the tools. Use for research and personal archival in accordance
with the game's ToS.

## Desktop app

The Windows app (`ksttk-dl-app`) browses the game's assets and downloads only what
you open:

- **Ships.** The ship library as cards in their rarity frames, with filters
  by ship type, search by English name, reading or id, an abyssal view, and a
  seasonal view of past seasonal CGs (kept on the server at card size only
  once their season ends).
  Opening a ship fetches its CGs, cards and banners and every voice line, with
  the English quote beside each line and playback built in.
- **装備 Equipment** and **家具 Furniture.** Galleries filtered by equipment
  category or room slot; each item page shows every picture it has.
- **海域 Maps.** Every sortie map, its background fetched when selected.
- **部屋 Room.** A port room builder. Choose a floor, wall, window, wall
  object, chest and desk, the secretary ship and the time of day for the view
  outside; each piece is placed exactly where the game client places it, and
  animated furniture shows its first frame. Every layer can be hidden, faded
  or dragged (arrow keys move it by 1 px, Shift by 10). Rooms can be saved,
  loaded and randomized, and exported as a 1200×720 or 2400×1440 PNG.
  The secretary's CG can be the current one (seasonal art while a season
  runs) or the original, each also damaged. During a season the original is
  enlarged from the card-sized art, so its size and place are estimates; each
  layer has a Size slider for that. Ships with a special remodel also offer
  that animation's art, and earlier CGs that ksttk-dl kept in `output/history`
  when a newer version replaced them are listed too. Past seasonal CGs from
  the server's archive can stand in the room as well, enlarged from their
  card size. Furniture that changes with the secretary, such as the umbrella
  stand, follows the chosen ship. The *Port UI* layer draws the port interface in any of its
  skins, in the game's own font, with an editable admiral name, rank, counts,
  resources and clock.
- **画面 UI.** Every interface sprite sheet and fixed image by screen, with its
  raw path; a sheet is shown whole and cut into its named frames.
- **音響 Audio.** Port and battle music, fanfares, sound effects, title calls
  and NPC, event and abyssal lines, playable in place, with their raw paths.
- **同期 Sync.** Game data refresh, world selection, a connection test that
  reports the address the CDN sees, whole-category downloads with progress,
  and *Organize output*, which produces the same named folders as `extract`.
- **設定 Settings.** Folders, connection (direct, the built-in Japanese relay,
  or a proxy), rare variants, parallel downloads, and a day or night theme that
  can follow Windows.

Gallery pictures are fetched as they scroll into view, in batches. Everything
lands in the same raw mirror and download index as the command-line tool, so
the two can share folders and neither downloads a file twice.

Build and run:

```sh
dotnet build src/KsttkDl.App -c Release
src/KsttkDl.App/bin/Release/net8.0-windows/ksttk-dl-app.exe
```

Settings are kept in `%LOCALAPPDATA%\ksttk-dl\settings.json` and saved rooms in
`rooms.json` beside it. The default data and
output folders are `Documents\ksttk-dl\data` and `Documents\ksttk-dl\output`.

## Asset discovery

Entity assets use paths like:

```
kcs2/resources/<category>/<type>/<id>_<hash>[_key].<ext>
```

The hash is deterministic, it combines the entity ID and `"<category>_<type>"`
through a 100-entry lookup table. This makes the catalog enumerable offline.
Asset rules for friendly ships, abyssals, and seasonal variants are ported from
[KCCacheProxy](https://github.com/Tibowl/KCCacheProxy)'s
`src/proxy/preload.js`.

Voice filenames use KC3Kai's `workingDiffs` table. Voice labels come from
kc3-translations' `tools/convert-quotes.py`.

## Requirements

- .NET 8 SDK
- Windows 10 or later for the desktop app; the command-line tool runs anywhere
  .NET 8 does
- A network path into Japan. The CDN only serves Japanese IP addresses. Use
  `--jp` for the built-in Japanese relay, `--proxy` for your own proxy, or run
  ksttk-dl from a Japanese connection

## Usage

```sh
dotnet build -c Release

ksttk-dl refresh-data
ksttk-dl worlds
ksttk-dl download --world 17 [--categories ships,equipment,maps,audio,ui,furniture] [--include-rare]
ksttk-dl extract [--categories ...] [--include-rare]
```

Run `refresh-data` before the first download and after game updates. All other
commands read from `data/`.

Every command accepts `--data <dir>` and `--output <dir>`.

### Single items

`get` downloads one ship, piece of equipment or furniture, or one map instead
of a whole category:

```sh
ksttk-dl get ship "<name>" --world 1            # pictures and voice lines
ksttk-dl get ship 131 --world 1 --no-voices
ksttk-dl get equipment 9 --world 1
ksttk-dl get furniture 1 --world 1
ksttk-dl get map 1-1 --world 1                  # or a whole area: get map 7
```

Ships and equipment can be named in English or Japanese, or by id; a partial
name works when it matches one entry. `--extract` organizes the category into
the output tree afterwards, as `extract` would. `get` also accepts
`--include-rare`, `--host`, `--jp` and `--proxy`.

### Built-in Japanese relay

Pass `--jp` to any command to connect through a Japanese
[VPN Gate](https://www.vpngate.net/) relay. No VPN client, system-wide VPN,
administrator rights or proxy is needed: ksttk-dl speaks OpenVPN to the relay
itself and only its own requests go through it.

```sh
ksttk-dl relay                         # connect and show the address the CDN sees
ksttk-dl download --world 17 --jp
ksttk-dl download --world 17 --jp --switch-relay
```

- ksttk-dl remembers the relay that worked (in `data/relay.json`) and tries it
  first next time, so the CDN keeps seeing the same address.
- `--switch-relay` skips the remembered relay and picks the next best one. Use
  it when a relay is slow or keeps failing.
- Volunteer-run relays are preferred. VPN Gate's own `public-vpn-*` relays
  limit each session to a few hundred kbit/s, so they are only used when no
  volunteer relay connects.
- Relays sometimes refuse a login; ksttk-dl moves on to the next one. If a relay
  drops during a download, the next request connects to a new one and the
  failed files are retried.
- Relays are run by volunteers for the VPN Gate academic experiment and their
  speed varies, typically a few Mbit/s. Downloading only the categories you
  need keeps runs short.

`--jp` and `--proxy` cannot be combined.

### Proxy

Every command accepts `--proxy <url>` to send its requests through a proxy
with a Japanese exit:

```sh
ksttk-dl download --world 17 --proxy socks5://127.0.0.1:1080
ksttk-dl refresh-data --proxy http://user:pass@proxy.example:8080
```

Supported schemes are `http`, `https`, `socks4`, `socks4a` and `socks5`. A
bare `host:port` is treated as an HTTP proxy. Credentials in the URL are used
for proxy authentication; percent-encode special characters in them. Without
`--proxy`, ksttk-dl follows the standard `HTTP_PROXY`, `HTTPS_PROXY` and
`ALL_PROXY` environment variables.

### Download options

- `--world <id>` selects a configured CDN world.
- `--host <host>` uses a CDN hostname instead of `--world`.
- `--categories <list>` limits categories.
- `--include-rare` includes rare and seasonal assets.
- `--concurrency <n>` sets parallel requests. Default: `6`.
- `--dry-run` prints the catalog without downloading.
- `--retry-missing` retries previously recorded 404s.

`download` stores the raw mirror in `output/raw/`. Existing files are skipped,
and expected 404s are recorded in the index. A complete run is about 65,000
assets and tens of GB. Run one category and one process at a time because runs
share the index file.

`extract` reads `output/raw/` and writes the organized output beside it. It is
fully offline and accepts `--overwrite`.

## Output

```text
output/
├── raw/                                # mirror of kcs2/... and the download cache
├── ships/
│   ├── <id>_<EnglishName>/             # full.png, full_dmg.png, card.png, banner.png, ...
│   ├── seasonal/
│   │   └── <id>_<key>/                 # seasonal CGs without master-data names
│   └── ships.json
├── equipment/
│   └── <id>_<EnglishName>/             # card.png, item_character.png, ... + equipment.json
├── furniture/
│   └── <id>_<JapaneseTitle>/           # normal.png, movable.png, scripts.json + furniture.json
├── maps/
│   └── <area>/
│       └── <no>/                       # background.png, info.json, image/<frame>.png + maps.json
├── audio/
│   ├── voice/
│   │   ├── <shipId>_<EnglishName>/    # Intro.mp3, Library.mp3, Poke(1).mp3, ... + voice.json
│   │   └── {npc,event,abyssal}/
│   ├── bgm/
│   │   └── {port,battle,fanfare}/
│   ├── se/
│   └── titlecall/
└── ui/
    └── <category>/
        └── <atlas>/
            └── <frame>.png             # unpacked TexturePacker atlases

```

English names come from kc3-translations. Each category writes a sidecar with
the Japanese name, numeric ID, translated name, output folder, and files:

```json
"10": {
  "japanese": "白雪",
  "english": "Shirayuki",
  "yomi": "しらゆき",
  "folder": "10_Shirayuki",
  "translated": true,
  "files": ["card.png", "..."]
}
```

Remodel and seasonal names are resolved by removing known Japanese prefixes and
suffixes, translating the base name, and restoring the modifiers. Furniture
keeps its Japanese title because no translation source is available. Seasonal
ship CGs have no `api_mst_ship` row and are stored under `ships/seasonal/` with
an explanatory `note`.

Voice sidecars map friendly labels to the original filenames and translated
quotes:

```json
"Library.mp3": {
  "originalFilename": "144688.mp3",
  "voiceLine": 25,
  "quoteText": "The first battleship born under the ..."
}
```

## Source layout

- `src/KsttkDl.Core/Naming`: hash function, voice tables, name resolution
- `src/KsttkDl.Core/MasterData`: `api_start2`, translation parsing, `refresh-data`
- `src/KsttkDl.Core/Catalog`: asset enumeration
- `src/KsttkDl.Core/Download`: HTTP client, retries, resumable index
- `src/KsttkDl.Core/Fetch`: per-entity catalog lookups and single-item downloads
- `src/KsttkDl.Core/Room`: port room layout (slot anchors, outside view,
  secretary placement) and saved rooms
- `src/KsttkDl.Core/Tunnel`: VPN Gate relay list, OpenVPN client and userspace
  TCP/IP stack behind `--jp`
- `src/KsttkDl.Core/Extraction`: atlas unpacking and organized output
- `src/KsttkDl.Cli`: System.CommandLine front end
- `src/KsttkDl.App`: WPF desktop app