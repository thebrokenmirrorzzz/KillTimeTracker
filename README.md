# Kill Time Tracker

> A CS2 plugin that tracks player kill reaction time, built on SwiftlyS2.

---

## Language / 语言

- [English](README.en.md)
- [简体中文](README.zh-CN.md)

---

**Kill Time Tracker** is a Counter-Strike 2 plugin that automatically detects enemies in a player's field of view and measures the precise time from first sight to elimination.

### Quick Links

| Feature | Description |
|---|---|
| 🎯 Reaction Time | Measures time from visual lock-on to kill |
| 📊 Average Stats | Cross-round average kill time and round kill counter |
| 🎨 Color Coded | Green (<500ms), Yellow (500-1000ms), Red (>1000ms) |
| 👀 Spectator View | Spectators see tracked player's kill stats |
| 🌍 Multi-language | Built-in English and Simplified Chinese |

### Build

```bash
dotnet publish -c Release
```

Output: `Release/KillTimeTracker-1.0.0.zip`

### License

GNU GPLv3
