# Frequently asked questions

**General**
- [Is Gameroom really free?](#is-gameroom-really-free)
- [Does it replace Steam, Epic or other launchers?](#does-it-replace-steam-epic-or-other-launchers)
- [Can I use Gameroom and Playnite at the same time?](#can-i-use-gameroom-and-playnite-at-the-same-time)
- [What languages is it available in?](#what-languages-is-it-available-in)
- [Does Gameroom send my data anywhere?](#does-gameroom-send-my-data-anywhere)
- [Do I need a Supabase server?](#do-i-need-a-supabase-server)
- [Windows SmartScreen or my antivirus warns about the installer](#windows-smartscreen-or-my-antivirus-warns-about-the-installer)

**Games and playtime**
- [A game I have installed wasn't added](#a-game-i-have-installed-wasnt-added)
- [A game I uninstalled doesn't appear. Is that right?](#a-game-i-uninstalled-doesnt-appear-is-that-right)
- [Something that isn't a game was added](#something-that-isnt-a-game-was-added)
- [A game has no cover](#a-game-has-no-cover)
- [My playtime isn't counted](#my-playtime-isnt-counted)

**Saves and achievements**
- [Where are my save backups?](#where-are-my-save-backups)
- [How do I get my saves back after reinstalling?](#how-do-i-get-my-saves-back-after-reinstalling)
- [My saves aren't backed up](#my-saves-arent-backed-up)
- [The achievement notification doesn't show](#the-achievement-notification-doesnt-show)
- [A game shows the wrong achievements, or none](#a-game-shows-the-wrong-achievements-or-none)

**Updates and uninstalling**
- [How do I update, or uninstall?](#how-do-i-update-or-uninstall)

---

## General

### Is Gameroom really free?

Yes, and it always will be. Gameroom is open source under the MIT license, with no ads, no store, no subscriptions and no mandatory account. If you want to support the project, you can [buy me a coffee](https://www.paypal.com/donate/?business=Cysselx4%40gmail.com&item_name=Gameroom&currency_code=USD).

### Does it replace Steam, Epic or other launchers?

No. Gameroom brings your games together in one place and starts them through their own launcher when they need one (Steam, Riot, Battle.net, a game's own launcher…). Your purchases, friends and store features stay where they are.

### Can I use Gameroom and Playnite at the same time?

Yes. Gameroom has its own data folder, its own executables and its own `gameroom://` links, so it doesn't touch Playnite. On first start it can copy your Playnite library, extensions and themes.

### What languages is it available in?

Gameroom's own screens are in **English** and **Spanish**. The parts that come from Playnite (library, settings, fullscreen mode) are available in many more languages. Change it in **Settings → General → Language**.

### Does Gameroom send my data anywhere?

No telemetry, no tracking, no account needed. Gameroom only connects to:
- Steam's public API and metadata services, for artwork and achievements.
- Playnite's add-on database, when you browse add-ons.
- GitHub, to check for updates.
- **Your own server**, if you connect one.

### Do I need a Supabase server?

No. It's only for accounts and cloud saves. Everything else works without it. If you want your backups in the cloud without a server, you can also set the backup folder to a OneDrive, Google Drive or Dropbox folder in **Settings → Gameroom**.

### Windows SmartScreen or my antivirus warns about the installer

The installer isn't code-signed (certificates are expensive for a free project), so Windows doesn't know the publisher. Click **More info → Run anyway**.

To verify that the file is the original, compare its SHA-256 with the one in the release notes:
```powershell
Get-FileHash .\GameroomSetup-x.y.z.exe -Algorithm SHA256
```
You can also build Gameroom yourself from this repository.

---

## Games and playtime

### A game I have installed wasn't added

Gameroom only adds things it can recognize as games, to keep apps out of your library. Try these:
1. Use **main menu → Update Game Library → Find games installed on this PC**. It looks again at everything, including apps it skipped before.
2. Just **start the game**. Gameroom recognizes it while it runs and adds it, keeping the playtime of that session. A card over the game tells you when it's added.
3. Add it by hand: **main menu → Add Game → Manually…**.

### A game I uninstalled doesn't appear. Is that right?

Yes. Windows often keeps the entry, and the desktop shortcut, of games whose folder was deleted. Gameroom checks that the game's files actually exist, and ignores those leftovers. Reinstall the game and Gameroom adds it again. It keeps its playtime and backups if it was in your library before.

### Something that isn't a game was added

Remove it from the library (right-click → *Remove*). Gameroom remembers that and won't add it again. To make sure live detection never treats a program as a game, add its executable to **Settings → Gameroom → Executables never treated as games**.

### A game has no cover

Covers come from Steam first and IGDB second, matched by the game's name. Some games use an internal name that differs from the store name. For example, *Endfield* is sold as *Arknights: Endfield*. To fix it:
1. Right-click the game → **Edit details**, and give it its full name.
2. Right-click it → **Download Metadata**, or **Download cover and details from Steam**.

### My playtime isn't counted

- Check that **Settings → Gameroom → Detect games started outside of Gameroom and record their playtime** is on.
- Sessions shorter than 60 seconds are ignored by default.
- The game must be in your library and installed, with its install folder or executable known. If you moved the game, use **Edit details** to update its folder, or start it once from Gameroom.
- Emulated games started outside Gameroom can't be detected live, because the running program is the emulator. Start them from Gameroom.

---

## Saves and achievements

### Where are my save backups?

By default in `%AppData%\Gameroom\SaveBackups`, as ZIP files. You can choose another folder in **Settings → Gameroom → Backup folder**. Use **game menu → Save backups → Open backup folder** to go straight to a game's backups.

### How do I get my saves back after reinstalling?

Install the game again (and Gameroom, if you reinstalled Windows). When a game has backups but no saves on the PC, Gameroom offers to restore them with a notification. You can also restore at any time with **game menu → Save backups → Restore latest backup**, or pick an older backup in the save backups window.

After reinstalling Windows, your backups are where you left them:
- **With an account:** sign in, and Gameroom downloads your backups.
- **With a synced folder** (OneDrive, Google Drive, Dropbox): set the backup folder to it again.

### My saves aren't backed up

Open **game menu → Save backups → Manage backups and save locations…**:
- **No save locations listed?** Play the game once with *Discover save folders by watching files games write while running* turned on, or click **Add folder…**.
- **A location is unchecked?** Check it.
- **Backups off for this game?** Make sure *Don't back up this game automatically* is off.

### The achievement notification doesn't show

- Windows doesn't let any app draw over games in **exclusive fullscreen**. Switch the game to **borderless** or windowed fullscreen.
- For games running through Steam, Gameroom leaves the notification to Steam. You can turn Gameroom's on in **Settings → Gameroom → Also show it for games running through Steam**.
- Try **Test popup** in **Settings → Gameroom** to check that it works.

### A game shows the wrong achievements, or none

Gameroom looks the game up on Steam by its AppID, or by its name. If it picked the wrong game, or didn't find it:
1. Open its **Achievements** window.
2. Enter the right Steam AppID (the number in the address of the game's Steam store page).
3. Click **Use AppID**.

Games that aren't on Steam don't have Steam achievements.

---

## Updates and uninstalling

### How do I update, or uninstall?

- **Updates:** Gameroom tells you when there's a new version and updates itself. You can also run **Check for Gameroom updates** from the Start menu.
- **Uninstalling:** use Windows' *Installed apps*. The uninstaller asks whether to delete your library, playtime and backups, or keep them for when you install Gameroom again.
