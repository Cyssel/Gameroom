# Contributing to Gameroom

Thanks for wanting to make Gameroom better! Bug reports, ideas, translations and code are all welcome.

## Reporting bugs and suggesting features

- Check the [open issues](https://github.com/Cyssel/Gameroom/issues) first, in case it's already reported.
- Use the **Bug report** or **Feature request** template. For bugs, attach `gameroom.log` from `%AppData%\Gameroom`, and remove anything personal first.
- Found a security problem? Please follow the [security policy](SECURITY.md) instead of opening a public issue.

## Building

You need:
- A .NET SDK. It can be a portable one.
- Visual Studio Build Tools 2022 with MSBuild.

```powershell
.\build\dev-build.ps1 -Restore   # first time: restores NuGet packages
.\build\dev-build.ps1            # builds the app into build\Debug
.\build\dev-test.ps1             # builds and runs Gameroom's tests
```

Run your build next to an installed Gameroom by setting the `GAMEROOM_DEV_INSTANCE` environment variable to any value.

## Where things are

| Path | Contents |
|---|---|
| `source/Playnite/Gameroom` | Gameroom's services: live detection, sessions, save backups (`Saves`), achievements (`Achievements`), cloud (`Cloud`), updates (`Updates`), installed games (`InstalledGames`), media. |
| `source/Playnite.DesktopApp` | User interface: Home, Account and game page views, Gameroom windows, and styles in `GameroomResources.xaml`. |
| `source/Playnite/Localization/Gameroom` | Gameroom's texts, `en_US.xaml` and `es_ES.xaml`. |
| `source/Tests/Gameroom.Tests` | Gameroom's tests (NUnit). |
| `supabase/setup.sql` | Database of the optional cloud server. |
| `build/` | Build, test and release scripts, and the Inno Setup installer. |

Everything else comes from [Playnite](https://github.com/JosefNemec/Playnite). Keep the Playnite SDK and database format compatible: Playnite extensions and themes must keep working.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep changes focused, and follow the style of the code around them.
3. Add or update tests in `source/Tests/Gameroom.Tests` when you change behavior, and run `.\build\dev-test.ps1`.
4. Add new texts to **both** `en_US.xaml` and `es_ES.xaml`.
5. Source files use **CRLF** line endings.
6. Describe what you changed and how you tested it in the pull request.

## Translations

Gameroom's own texts live in `source/Playnite/Localization/Gameroom`. To add a language:
1. Copy `en_US.xaml` to the new language code (for example `fr_FR.xaml`), and translate the values.
2. Add the code to the available languages in `source/Playnite/Gameroom/GameroomLocalization.cs`.

## License

By contributing, you agree that your contributions are licensed under the [MIT license](LICENSE.md).
