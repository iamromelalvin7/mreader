MDreader
========
A lightweight Markdown reader/editor for Windows, with a raw markdown
editing view, a formatting toolbar, and a rendered Preview mode.

Version: 1.0.0


SYSTEM REQUIREMENTS
--------------------
- Windows 10 (64-bit) or Windows 11
- ~150 MB free disk space
- No other software required to install first -- .NET, the Windows App
  runtime, and all other dependencies are bundled inside the installer.
- Preview mode uses the Microsoft Edge WebView2 Runtime, which is built
  into Windows 11 and most up-to-date Windows 10 systems by default.
  If it's ever missing, Windows Update will install it automatically;
  everything except Preview mode will still work without it.


HOW TO INSTALL
---------------
1. Double-click MDreaderSetup.exe.
2. Windows SmartScreen may show a blue "Windows protected your PC"
   warning because this installer isn't digitally signed by a
   commercial certificate authority. This is expected for a small,
   independently distributed app -- it does not mean the file is
   unsafe. Click "More info", then "Run anyway" to continue.
3. Follow the setup wizard (choose an install folder, optionally add a
   desktop shortcut).
4. If you don't have administrator rights on this PC, you can install
   for just your own user account instead -- open a terminal in this
   folder and run:
       MDreaderSetup.exe /CURRENTUSER
5. When setup finishes, launch MDreader from the Start Menu or the
   desktop shortcut.


HOW TO UNINSTALL
------------------
Use either:
- Settings > Apps > Installed apps > MDreader > Uninstall, or
- Start Menu > MDreader > Uninstall MDreader

This removes all installed program files automatically.


SUPPORT
--------
This is a personal/independent project, distributed as-is.
