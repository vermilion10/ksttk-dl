# ksttk-dl

Asset downloader and extractor for a certain browser game. Fan-made, not affiliated with the game's
developers or publishers. Use for research and personal archival in accordance with the game's
ToS.

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
- A network path into Japan. The CDN only serves Japanese IP addresses, so
  either run ksttk-dl from a Japanese connection or pass `--proxy`

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

Both `download` and `extract` accept `--data <dir>` and `--output <dir>`.

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
- `src/KsttkDl.Core/Extraction`: atlas unpacking and organized output
- `src/KsttkDl.Cli`: System.CommandLine front end