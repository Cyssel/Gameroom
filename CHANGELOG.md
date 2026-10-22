# Changelog

All notable changes to Gameroom. Gameroom is based on Playnite 10.63.

## 1.5.0 — First public release

- **Your own cloud server.** Accounts and cloud saves run on each user's own Supabase project:
  - Connect it in **Account**. Nothing is sent anywhere until a server is set up, and secret keys are refused.
  - [`supabase/setup.sql`](supabase/setup.sql) prepares the database, and [the server guide](docs/SUPABASE.md) explains every step.
- **Updates from GitHub Releases.** The installer is checked with the SHA-256 published by GitHub.
- **New `gameroom://` links** for play sessions, save backups, play statistics, settings and a sample achievement notification.
- **English first:** the installer defaults to English. Dates, days and months follow Gameroom's language instead of Windows' region.
- **Documentation:** getting started, user guide, FAQ and server guide.
- **Games you play are added right away.** A game started from anywhere is recognized while it runs and added to the library, with a card over the game: *Added to your library*. Before, it waited in the notifications, where it went unnoticed. This applies to games with a recognizable engine or Steam files, and removed games aren't added again.
- Games added while running get their icon, platform, and a cover from Steam or IGDB.
- Fixes:
  - "1 session" instead of "1 sessions".
  - Month names in the play statistics chart.
  - Achievement icons of recent Steam games, which moved to a new Steam server.

## 1.4.1

- **More ways to find games:** desktop and Start menu shortcuts, portable games in the top-level folders of your drives (and inside `Games` folders), games installed straight into a drive root, and Xbox / Microsoft Store games.
- Ragnarok Online clients are recognized.

## 1.4.0

- **Installed games are added automatically** from Windows' installed apps and store manifests: Steam, Epic, Riot, Battle.net, EA, HoYoPlay, repacks, games inside launchers, and any game with a recognizable engine.
- Removed games are never added again, leftovers of uninstalled games are ignored, and reinstalled games keep their history.
- New games get an icon, the PC platform, and artwork from Steam or IGDB.
- The home screen updates by itself when games are added or their artwork arrives.
- Launchers that hold a game's folder no longer count as the game running.

## 1.3.1

- No more flicker or jumping items when pointing at and selecting games.

## 1.3.0

- **Console-style home** with full-screen artwork, official logos, an ambient color and a carousel of recent games.
- **A page for every game** with playtime, activity, achievements, rarest achievement, saves and details.
- Official artwork from Steam (transparent logos, library heroes) and animated transitions.

## 1.2.0

- **Steam achievements for all your games,** also for games not installed from Steam, with progress from the Steam client and Steam emulators.
- **In-game achievement notification,** like Steam's.

## 1.1.0

- **Accounts and cloud saves.**
- New design, with the Home and Account sections.

## 1.0.1

- One click restore of the latest save backup.
- New installations start in the Windows display language.

## 1.0.0

- **Live game detection:** playtime for library games started outside Gameroom, counted minute by minute.
- **Play sessions history** and **play statistics**.
- **Save backups** while playing and after playing, with restore after reinstalling, and save folders learned while playing.
- Cover and details from the Steam store, without extensions.
- Own installer and updater, own data folder and `gameroom://` links, Playnite library import, and Gameroom's visual identity.
