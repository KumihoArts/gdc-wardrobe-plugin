# GDC Wardrobe Plugin

A BepInEx plugin for HoneySelect2 (and StudioNEOV2) that adds a floating in-game
window for GDC clothing mods: blendshape sliders, material sliders, fabric
presets, MainTex swaps, and clothing-to-accessory layering. Works in Maker and
Studio.

> Full feature write-up to come.

## Requirements

- HoneySelect2 with BepInEx 5.x
- Sideloader and KKAPI / HS2API (hard dependencies)
- MaterialEditor (soft dependency)

## Install

1. Download `GDCplugin.dll` from [Releases](../../releases).
2. Drop it in `BepInEx\plugins\`.
3. Launch the game and open the window with **Ctrl+Shift+G**.

## Building from source

```powershell
dotnet build .\src\GDCplugin.csproj -c Release
```

Targets `net46`. References resolve from a local HoneySelect2 install; adjust the
paths in `src\GDCplugin.csproj` to match yours. The UI skin is an embedded Unity
2018.4 asset bundle (`Bundle\`, rebuilt via `tools\sync-bundle.ps1`).

## Credits

- Plugin: Kumiho
- Clothing mods and authoring convention: GDC

## License

GPL-3.0-or-later. See [`LICENSE`](LICENSE).
