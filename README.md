# Relative Artist Mode

An [OpenTabletDriver](https://github.com/OpenTabletDriver/OpenTabletDriver) output mode for Linux that moves the cursor like Relative Mode while keeping pen pressure, tilt, and the eraser.

## Why?

I write a lot with my tablet, and mapping the whole screen to it makes handwriting way too messy, so I use relative mode. The problem is that OTD's Relative Mode just emulates a mouse, so pressure doesn't work at all, while Artist Mode has pressure but only works as an absolute mapping. This is a [known issue](https://github.com/OpenTabletDriver/OpenTabletDriver/issues/3849) that's been sitting in the backlog. libinput only treats a device as a tablet if it reports absolute coordinates, so pressure from a relative device gets dropped before it ever reaches your apps.

This plugin gets around that by doing the relative math itself, as opposed to sending relative motion. It keeps track of where the pen should be and sends that position to OTD's own Artist Mode virtual tablet, so the compositor sees a normal pen with pressure that happens to move relatively. Nothing in libinput, the compositor, or OTD needs to be patched.

Only Linux is supported, because Windows (through [VoiDPlugins](https://github.com/X9VoiD/VoiDPlugins)' Windows Ink relative mode) and macOS already have relative mode with pressure.

## Dependencies

- OpenTabletDriver 0.6.7.0 or newer
- PipeWire and xdg-desktop-portal with cursor metadata support (only for the Screen cast cursor sync, see below)

## Installation

Download the zip from the latest release and drag it into OTD's Plugin Manager, or use `File` > `Install Plugin...` in the Plugin Manager. If you'd rather do it by hand, extract every DLL from the zip into `~/.config/OpenTabletDriver/Plugins/RelativeArtist/` and restart the daemon:

```bash
systemctl --user restart opentabletdriver
```

Note that `otd installplugin` won't replace a plugin the daemon already has loaded, so for updates either use the Plugin Manager or copy the DLLs over by hand like above.

## Usage

Pick `Relative Artist Mode` as your output mode. Sensitivity, rotation, and reset time come from the normal Relative Mode settings, so if you already had relative mode set up the way you like, it'll feel the same.

The plugin's own settings are in the Filters tab under `Relative Artist Mode Settings`, because OTD's Output tab only ever shows its built-in Relative Mode editor and has no way of displaying settings from an output mode plugin. Enable it there to change anything; it doesn't actually filter or touch your reports, it just holds the settings. Without it, the plugin uses the defaults.

**Keep cursor visible when lifted:** Keeps the pen reported as in range after you lift it off the tablet, so the cursor (and your app's brush outline) stays on screen instead of disappearing. On by default.

**Cursor sync:** How the pen picks back up after you move the cursor with your mouse. OTD's Relative Mode sends normal mouse movement, so the compositor adds it to the same cursor your mouse moves. This plugin sends an absolute position instead, so without some kind of sync, the pen jumps back to wherever it last was the moment you put it down.
- `None`: The pen always continues from its own last position
- `Mouse tracking`: Reads your mice directly from `/dev/input` and applies their movement to the pen's position
- `Screen cast`: Follows the real cursor position exactly by asking your desktop for it through a screen share (recommended)

**Mouse speed multiplier:** Only used by `Mouse tracking`. With libinput's flat acceleration profile, set this to 1 plus your pointer speed setting (a pointer speed of 0.2 means a multiplier of 1.2).

### Screen cast

When `Screen cast` is enabled, your desktop WILL show a screen sharing request the first time the daemon starts, and a screen sharing indicator stays visible while it's running. The plugin never looks at or saves what's on your screen; screen sharing is the only standard way for a program to read the true cursor position on Wayland, and that position is the only thing it uses. The screen share is set up so that your compositor attaches the cursor position to the stream as metadata, and the plugin reads that while ignoring the video entirely.

On KDE, note that Plasma turns on Do Not Disturb by itself whenever any screen share is active, so your notifications will be held back the whole time the daemon is running. The plugin has no way of opting out of this, so if you want your notifications back, go to `System Settings` > `Notifications` > `Do Not Disturb` and uncheck the option to enable it while screen sharing. This is a global setting, though, so it also won't kick in when you're sharing your screen for real (streaming on Discord, etc.).

If your desktop lets you remember the choice, it'll start silently from then on, since the plugin saves the portal's restore token in `~/.config/OpenTabletDriver/RelativeArtist/`. KDE and GNOME both support this. wlroots-based compositors (Sway, Hyprland, etc.) have been hit or miss with cursor metadata, so if the pen still jumps after using your mouse there, use `Mouse tracking` instead.

### Mouse tracking

This one doesn't need any permission, but it only stays in sync if pointer acceleration is disabled (the flat profile in your desktop's mouse settings), because the plugin has no way of knowing how your compositor accelerates the cursor. Make sure the setting actually applied to the mouse you use, since some desktops keep separate settings per device. Anything else that moves the cursor (a touchpad, an app warping the pointer, etc.) also won't be picked up.

## Building

Everything builds in Docker, so you don't need the .NET SDK installed:

```bash
docker build --target export --output type=local,dest=out .
```

The DLLs end up in `out/`. Releases are made by running the `Release` workflow on GitHub with a four-part version (like `1.0.0.0`), which builds the zip and creates a draft release with its SHA256 in the notes.
