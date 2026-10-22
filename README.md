# Mogu.NET

ASP.NET Core 10 port of [mogu](https://github.com/HoloArchivists/mogu), itself a fork of
[Nyaa](https://github.com/nyaadevs/nyaa). Same HTML and layout, new backend: Razor Pages,
EF Core with SQLite, Discord OAuth2 login and a built-in HTTP BitTorrent tracker.

## Layout

| Path | What |
| --- | --- |
| `src/Mogu.Core` | Entities, `MoguDbContext`, EF migrations, options, bencode / torrent parsing / magnet & .torrent generation |
| `src/Mogu.Web` | Razor Pages site, minimal API endpoints (`/api/*`, downloads), Discord auth, tracker (`Tracking/`) |
| `tools/Mogu.LegacyImport` | One-time import of the old mogu SQLite database |
| `tests/Mogu.Tests` | MSTest unit + `WebApplicationFactory` integration tests |

## Development

```sh
dotnet tool restore
dotnet build
dotnet test
cd src/Mogu.Web && dotnet run          # http://localhost:5000
```

Migrations are applied on startup (`Database:MigrateOnStartup`). Runtime data lives in
`Mogu:DataDirectory` (default `src/Mogu.Web/data/`): `mogu.db`, `info_dicts/`, `torrents/`, `keys/`.

In Development, `GET /dev/login/{username}?level=Regular|Trusted|Moderator|Administrator` signs
you in without Discord (creates the user if needed). It is not mapped in other environments.

Schema changes: edit the entities/configurations in `Mogu.Core`, then

```sh
dotnet ef migrations add <Name> --project src/Mogu.Core --output-dir Data/Migrations
```

## Configuration

Everything is in `src/Mogu.Web/appsettings.json`; override with environment variables
(`Section__Key`), user secrets in development, or `mogu.env` with docker compose.

- `Mogu` – site settings ported from the old `config.py` (`MoguOptions`): site name, maintenance /
  raid mode, upload rate limits, results per page, max pages, comment editing limit, sticky torrents,
  `DefaultTrackers` (added to every magnet/.torrent), `TrackerBlacklist` (never handed out and
  stripped from uploads), `MainAnnounceUrl` (leave empty to use the built-in tracker).
- `Discord` – OAuth2 app, guild and role ids. Users need a `LoginRoles` role; admin/moderator/trusted
  roles set the user level **on every login** (Discord is the source of truth, so level changes made
  on the site are overwritten at the next login). Register `<site>/discord/oauth2/callback` as the
  redirect URI (same path as the old site).
- `Tracker` – built-in tracker (`/announce`, `/scrape`). Set `Tracker:AnnounceUrl` to the public URL,
  e.g. `https://mogu.holopirates.moe/announce`; otherwise it's derived from the request host.
  Peers are kept in memory; seeders/leechers/completed are flushed to the database every
  `StatsFlushInterval`. Only torrents in the database that aren't banned are tracked.
- `ReverseProxy:KnownNetworks` – CIDRs of reverse proxies whose `X-Forwarded-For/Proto` headers are
  trusted (loopback is always trusted). Needed for correct client IPs, bans and the https redirect URI.

## What changed compared to mogu

- **Schema**: new, normalised schema (EF migrations) instead of the Nyaa/Sukebei table pairs. Torrent
  flags are booleans, statistics/preferences are columns, IPs are strings, users have a surrogate id
  plus `DiscordId`, no email/password columns. Torrent ids are preserved by the importer.
- **Auth**: Discord OAuth2 via ASP.NET Core's OAuth handler (state + PKCE), cookie sessions with
  persisted data-protection keys. Banned users can no longer log back in (the old callback reactivated them).
- **API**: `/api/upload`, `/api/v2/upload`, `/api/info/{id|hash}` are kept, but authenticate with a
  per-user **API token** (generate it on `/profile`): HTTP Basic `username:token` (so
  `utils/api_uploader_v2.py` keeps working) or `Authorization: Bearer <token>`. The old basic auth
  effectively accepted an empty password for every Discord user.
- **Search**: works on SQLite (the old code only had a MySQL FULLTEXT / Elasticsearch path). Terms are
  ANDed substring matches on the display name (case-insensitive, CJK friendly), with `"exact phrases"`,
  `-exclusions` and `a|b` alternatives. Elasticsearch is gone.
- **Tracker**: built in, replaces the dead external UDP tracker; stats on listings are real again.
- **Removed**: trusted applications (empty in prod, entry point was already gone; trust comes from
  Discord roles), email sending, gravatar (users only had dummy emails), recaptcha, sukebei flavour.

## Migrating production

1. Stop the old mogu container and copy its data: `mogu/mogu/database_prod.db`, `info_dicts/`, `torrents/`.
2. Import into a fresh data directory:

   ```sh
   dotnet run --project tools/Mogu.LegacyImport -- \
     --source database_prod.db --target data/mogu.db \
     --data-dir data --info-dicts old/info_dicts --torrents old/torrents
   ```

   or with the image: `docker compose run --rm -v "$PWD/old:/old:ro" --entrypoint dotnet mogu
   /import/Mogu.LegacyImport.dll --source /old/database_prod.db --target /data/mogu.db --data-dir /data
   --info-dicts /old/info_dicts --torrents /old/torrents`.

   The source is opened read-only; the tool prints old-vs-new row counts and refuses a non-empty
   target unless `--force`. `--mark-missing-torrents` clears `HasTorrent` for torrents whose info
   dict file is missing.
3. The container runs as the non-root `app` user (uid 1654): `sudo chown -R 1654:1654 data`.
4. `cp mogu.env.example mogu.env`, fill in the Discord settings, `docker compose up -d --build`.
5. nginx keeps proxying to `127.0.0.1:5000` (including `/announce` and `/scrape`). The site is behind
   Cloudflare, so nginx must restore the real client IP, otherwise IP bans, rate limits and the
   tracker's peer lists all see Cloudflare edge addresses:

   ```nginx
   # http {} block; keep the list in sync with https://www.cloudflare.com/ips/
   set_real_ip_from 173.245.48.0/20;
   # ... one line per Cloudflare IPv4/IPv6 range ...
   # plus any relay between Cloudflare and this host, e.g. set_real_ip_from <relay ip>;
   real_ip_header CF-Connecting-IP;

   # location / {}
   proxy_set_header X-Forwarded-For $remote_addr;
   proxy_set_header X-Forwarded-Proto https;
   ```

   `X-Forwarded-Proto` matters for the Discord redirect URI. Set `ReverseProxy__KnownNetworks__0` if
   nginx reaches the container from a docker network rather than loopback.

## License

GPL-3.0, see [LICENSE](LICENSE). Mogu.NET is a derivative of mogu and Nyaa (both GPL-3.0): the
templates, stylesheets, scripts and static assets are carried over from them, and the backend is a
port of their code.
