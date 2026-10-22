# Getting started

From download to your first game in a few minutes.

## 1. Install

1. Download **`GameroomSetup-x.y.z.exe`** from the [latest release](https://github.com/Cyssel/Gameroom/releases/latest).
2. Run it. Gameroom installs for your user only and doesn't need administrator rights.
3. Choose whether you want a desktop shortcut, and finish.

> [!NOTE]
> Windows SmartScreen may warn about an app from an unknown publisher, because the installer isn't code-signed. Click **More info → Run anyway**. If you want to be sure the file is the original, compare its SHA-256 with the one in the release notes:
> ```powershell
> Get-FileHash .\GameroomSetup-x.y.z.exe -Algorithm SHA256
> ```

**Requirements:** Windows 10 or 11, and .NET Framework 4.6.2 or later (already part of Windows 10 and 11).

## 2. First start

- **Coming from Playnite?** Gameroom offers to import your Playnite library, extensions and themes. Playnite isn't changed, and both apps can stay installed side by side.
- **Starting fresh?** Gameroom looks for the games installed on your PC right away and adds them, with covers and artwork. This takes a minute or two: games appear on the home screen as they're found.

## 3. Connect your stores (optional)

Gameroom already adds your *installed* games. To also see the games you own but haven't installed:
1. Open **main menu (G button) → Add-ons…**.
2. Install the extension of each store you use: Steam, Epic, GOG, Battle.net, EA, Ubisoft, Xbox…
3. Use **main menu → Update Game Library**.

## 4. Play

Start a game from Gameroom, or the way you always do. Gameroom notices that it's running:
- **It counts your playtime** minute by minute.
- **It backs up your saves** while you play and when you quit.
- **It shows a notification** when you unlock a Steam achievement.

Afterwards, open the game's **Game page** to see your time, sessions, achievements and backups.

## 5. Turn on cloud saves (optional)

Want your saves on every PC? Create your own free server in about 5 minutes with **[the server guide](SUPABASE.md)**, then connect it in **Account**.

## Next steps

- **[User guide](USER_GUIDE.md):** every feature, screen by screen.
- **[FAQ](FAQ.md):** answers to common questions and problems.
