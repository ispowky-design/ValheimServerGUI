# ValheimServerGUI

A simple user interface for running a [Valheim](https://www.valheimgame.com/) Dedicated Server on your Windows or Linux PC.

Download the [latest release here](https://github.com/ispowky-design/ValheimServerGUI/releases/latest). This fork adds world backups, SteamCMD install, a world map and a wood-and-gold theme to [runeberry/ValheimServerGUI](https://github.com/runeberry/ValheimServerGUI).

Need help? Join our [Discord server](https://discord.gg/HBsNJTY) to ask questions or give feedback, or check out [Online Manual](https://runeberry.com/valheim-server-gui/) for answers to some common questions.

**Disclaimer:** _This is a fan-made project. Runeberry Software is not affiliated with Valheim or Iron Gate Studio in any official capacity. Use at your own risk!_

<table width="100%" align="center">
  <tr>
    <td><a href="img/Screenshot-ServerControls.png"><img src="img/Screenshot-ServerControls.png"></a></td>
    <td><a href="img/Screenshot-AdvancedControls.png"><img src="img/Screenshot-AdvancedControls.png"></a></td>
  </tr>
  <tr>
    <td><a href="img/Screenshot-ServerDetails.png"><img src="img/Screenshot-ServerDetails.png"></a></td>
    <td><a href="img/Screenshot-Players.png"><img src="img/Screenshot-Players.png"></a></td>
  </tr>
</table>

## Installation

On all platforms, you must have **Valheim Dedicated Server** installed. This comes free with your purchase of Valheim on Steam. See the installation guide [here](https://runeberry.com/valheim-server-gui/docs/installing-valheim-dedicated-server/).

### Windows

**Requirements:** Windows 10 or later. The .NET runtime is bundled, nothing else to install.

**Setup:** Download `ValheimServerGUI-<version>-win-x64-setup.exe` from the [Releases page](https://github.com/ispowky-design/ValheimServerGUI/releases/latest) and run it. It installs for your account (no admin prompt), adds a Start menu entry and, if you tick it, a desktop shortcut. The app updates itself from then on (or run the newer installer over it); your profiles and settings are kept. Prefer no install? Download `ValheimServerGUI-<version>-win-x64-portable.exe` and run it from anywhere.

The files are not code-signed, so SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**.

### Linux

**Setup:** Download `ValheimServerGUI-<version>-linux-x64.tar.gz` from the [Releases page](https://github.com/ispowky-design/ValheimServerGUI/releases/latest), extract it anywhere and run `ValheimServerGUI.App`. The .NET runtime is bundled. Includes a `.desktop` file and icon if you'd like to add it to your app menu.

### Making a release

Every push and pull request is built by GitHub Actions (`.github/workflows/build.yml`), which runs the tests and keeps the installer, portable exe and Linux tarball as downloadable artifacts. To publish a release, push a `v<version>` tag, or run the **build** workflow from the Actions tab with a version: it creates the tag and a GitHub release with the files attached.

## Features

* **It remembers!** - Stores your server info between sessions, and it can't be overwritten by Steam
* **Status updates** - Clearly shows when your server is running, starting, or stopping
* **Online players** - Show which players are online or offline, and when they arrived/left
* **Crossplay support** - Recognizes players from Steam, Xbox, PlayStation, and Nintendo platforms
* **Player management** - Manage your admin, permitted, and banned player lists from within the app
* **Steam Cloud worlds** - Easily migrate single-player Steam Cloud world saves to be used as dedicated server worlds
* **Easy IP address** - No more guessing, copy the right IP address to give to your friends straight from the app
* **Cleaner server logs** - Eliminates a lot of the noisy debug logs produced by the server
* **Input validation** - Prevents you from creating a server with bad info that would fail to launch
* **Safe shutdowns** - Safely stops the server when you close the app or shut down your computer
* **Automatic startup** - If enabled, can automatically start up your server when your computer starts
* **Minimize to tray** - Minimize this app and control your server entirely from the system tray
* **Multi-server support** - Run multiple servers at once by creating separate server profiles (see [FAQs](https://runeberry.com/valheim-server-gui/docs/frequently-asked-questions/) for details)
* **Scheduled restarts** - Restart each server at set times of day, backing up the world while it is stopped
* **Updates itself** - Downloads a new version in the background and installs it in one click from the status bar (Windows installer and portable exe), never while a server is running
* **English or French** - The interface follows your system language, or pick one under File > Preferences
* **Works with mods!** - Tested and working with server-side mods such as [Valheim Plus](https://www.nexusmods.com/valheim/mods/2323)"

## Quick guide

1. Launch ValheimServerGUI.
2. Enter your desired Server Name and Password. You don't need to change the Port in most cases.
3. Select the world you want to host, or enter a new world name.
4. Check any additional join options you want enabled:
  * **Community Server** will show your server in the server browser in-game.
  * **Enable Crossplay** will allow players on any platform to join with an Invite Code
5. Click "Start Server". When the status bar reads "Running", you're ready to play! You can copy your server's IP address or Invite Code from the Server Details tab to share with friends.

## FAQs

Why choose a dedicated server, and why use ValheimServerGUI? Find the answers on our [FAQs page](https://runeberry.com/valheim-server-gui/docs/frequently-asked-questions/).

## Contributing

Interested in contributing code to ValheimServerGUI? Check out the guidelines and full developer's guide [here](CONTRIBUTING.md).

## Support

Got suggestions, feedback, or want to report a bug? Here's how you can reach me:

* Post in the **#vsg-support** channel on our [Discord](https://discord.gg/HBsNJTY) server.
* Create a [GitHub issue](https://github.com/runeberry/ValheimServerGUI/issues/new) - Include as many details as you can for bug reports!
* Submit a bug report in the app itself (Help > Submit a Bug Report). This will include logs & machine info to help troubleshoot the problem.
* Send me an email - feedback@runeberry.com

## Donate

Do you like open-source gaming projects like this one? Want to see more of them? [Buy me a coffee](https://runeberry.com/tips) to show your support! And check out my other projects on the [main site](https://runeberry.com).
