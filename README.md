# deskmon

A small Conky-style resource panel for Windows. It's a transparent, click-through window that sits just above the wallpaper and below every app window.

It shows CPU, RAM and commit, GPU load, temperature, power and VRAM, disk usage and I/O, and network speed. It needs no admin rights, kernel driver or HWiNFO: everything comes from Windows performance counters and NVML, the library that ships with the NVIDIA driver. CPU temperature isn't shown because Windows has no reliable way to read it without a kernel driver.

## Using it

Run `deskmon.exe`, then right-click the brain icon in the system tray:

- **Monitor:** choose which display it sits on
- **Corner:** top left, top right, bottom left or bottom right
- **Settings:** start with Windows, and the accent color (presets or custom)
- **Exit deskmon**

Settings are saved in `%APPDATA%\deskmon\settings.ini`.

Windows SmartScreen may warn you when you first run `deskmon.exe`, because the download isn't code-signed. Choose **More info**, then **Run anyway**, or build it yourself from source.

## Building

Run `build.cmd`. It uses the C# compiler included with Windows (.NET Framework), so no SDK is needed. That compiler is limited to C# 5.

`deskmon.exe --png out.png` renders one frame to a file. This is the easiest way to check the layout, since the live panel sits behind your windows.

## Credits

The icon is based on the brain from [Fluent Emoji](https://github.com/microsoft/fluentui-emoji) by Microsoft, used under the MIT license, recolored and with smoke added. See `LICENSE-fluent-emoji.txt`.

## License

MIT, see `LICENSE`. The icon's license is in `LICENSE-fluent-emoji.txt`.
