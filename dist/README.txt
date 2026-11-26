3 on 3 NHL Arcade - PC port
===========================

This is a native PC port of 3 on 3 NHL Arcade (Xbox 360, XBLA). It contains no game
code or data: you need your own copy of the game's Xbox Live Arcade package. The
launcher builds the PC version from your copy, on your PC. Nothing is uploaded.

This is a pre-alpha test build. Expect bugs; when you report one, include the
version shown in the launcher and the files in the logs folder.

Setup
-----
1. Unzip this folder somewhere you can write to (not Program Files), such as
   Documents or another drive.
2. Run "NHL 3on3 Launcher.exe".
3. Drop your game onto the launcher window, or use "Browse for file" /
   "Choose folder". Any of these works:
     - the whole "58410975" folder,
     - the package file inside 58410975\000D0000\ (its name is a long string of
       letters and digits),
     - an already unpacked copy (a folder with default.xex in it).
   A package next to the launcher is found on its own.
4. Click "Build game". The launcher checks your copy, unpacks it into a "game"
   folder, recompiles it and builds nhl3on3.exe. This takes about 5-30 minutes,
   once, and you can keep using your PC meanwhile.
   The game is compiled with Microsoft's free C++ Build Tools. If they aren't
   installed, the launcher installs them first: Windows asks for permission and
   Microsoft's installer shows its progress (a few GB to download, about 10 GB on
   disk). The build also needs about 4 GB free on your system drive while it runs.
5. Click Play. Afterwards you can also start nhl3on3.exe directly.

If the build stops, the launcher shows why. "Try again" picks up where it
stopped; the full log is logs\build.log.

Keep the "buildkit" folder: the launcher needs it to rebuild the game, for
example after you update to a new release.

Controls
--------
Xbox and PlayStation controllers work. If a PlayStation controller is not detected,
close Steam (Steam Input can take over the controller) and start the game again.

Achievements
------------
Choose Achievements in the main menu, or press F7 in-game, to see them. Unlocks are saved in Documents\nhl3on3\achievements.
The two online achievements (Let's Play, Team Player) can't be earned yet.

Settings
--------
nhl3on3.toml holds the settings the game needs; command-line flags override it.

Internal resolution: add "resolution_scale = 2" to nhl3on3.toml (or start the game
with --resolution_scale=2) to render at 2560x1440 instead of 1280x720. 2 holds
60 fps on a recent graphics card; 3 is close but can dip below 60 in matches.
Press F3 in-game to show the frame rate.

Known issues
------------
- Online play (Xbox Live menu) is not available.
- In some matches the scoreboard clock and score stay blank for the whole match.

Licenses are in the licenses folder.

Legal
-----
This is an unofficial fan project. It is not affiliated with or endorsed by
Electronic Arts, EA Sports, the NHL or Microsoft. 3 on 3 NHL Arcade and all of
its assets belong to Electronic Arts; NHL names and logos are trademarks of the
National Hockey League. No game files are included.
