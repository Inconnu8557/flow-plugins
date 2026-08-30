# Flow.Launcher.Plugin.Iconify

Search and copy [Iconify](https://iconify.design) SVG icons **without leaving Flow Launcher**.

> C# (.NET 8) plugin for [Flow Launcher](https://github.com/Flow-Launcher/Flow.Launcher). No Python, uses public API `https://api.iconify.design`.

## Features

- **Instant search** via `https://api.iconify.design/search?query=...` (limit 48, ~200 collections, 300k+ icons)
- **One-Enter copy**: `Enter` copies `<svg ...>...</svg>` to clipboard (stays in Flow)
- **Direct icon**: `iconify mdi:home` → immediate copy without search
- **Filter by collection**: `iconify --prefix=mdi home` or `iconify mdi home` (heuristic)
- **Browse collection**: `iconify :mdi` / `iconify :ph` / `iconify :tabler`
- **Preview**: local PNG cache ` %LOCALAPPDATA%\FlowLauncher\IconifyCache\{prefix}_{name}.png` rendered via `Svg.Skia` (centered, 256px) – fixes Flow's remote SVG preview bug
- **Context menu** (Shift+Enter / right click):
  - Copy SVG (default)
  - Copy SVG with forced color (`#000000`)
  - Copy SVG 24x24
  - Copy SVG URL
  - Copy name `prefix:name`
  - Copy Data URL (base64)
  - Open on `icon-sets.iconify.design`
  - View collection

## Installation

### From Flow Launcher (recommended once published)
`pm install Iconify`

### Manual
1. `dotnet publish Flow.Launcher.Plugin.Iconify -c Release -r win-x64 --no-self-contained`
2. Copy `Flow.Launcher.Plugin.Iconify/bin/Release/win-x64/publish` → `%APPDATA%\FlowLauncher\Plugins\Iconify`
3. Restart Flow Launcher

### Dev
```powershell
.\debug.ps1
```

## Usage

| Query | Description |
|---------|-------------|
| `iconify` | Help + examples |
| `iconify home` | Search `home` in all collections |
| `iconify arrow` | Search `arrow` |
| `iconify mdi:home` | Exact icon `mdi:home` (direct copy) |
| `iconify --prefix=mdi home` | Filter collection `mdi` |
| `iconify :mdi` | List icons of `mdi` (collection browsing) |
| `iconify :ph` | List Phosphor icons |
| `iconify mdi/` | Same browsing |

**Shortcuts**
- `Enter`: copy SVG and stay in Flow (doesn't close window)
- `Ctrl+C`: copy name `prefix:name` (via `CopyText`)
- `Context menu`: alternatives (color, size, URL, Data URL, open browser)

## Technical

- `Flow.Launcher.Plugin` 4.4.0
- `net8.0-windows`, `ImplicitUsings`, `Nullable`
- `HttpClient` singleton, timeout 10s
- `IAsyncPlugin` + `IContextMenu`
- `System.Text.Json` for `/search` and `/collection?prefix=...`
- `Svg.Skia` + `SkiaSharp` for SVG → PNG preview cache (centered)
- `CopyToClipboard(svg, false, true)` for SVG
- `PreviewInfo` with `PreviewImagePath = cachedPng`

### Iconify API used

- `GET /search?query={q}&limit=48&pretty=1[&prefix=...]` → `icons: string[]`, `collections: Record<string, IconifyInfo>`
- `GET /{prefix}/{name}.svg[?color=&width=&height=]` → raw SVG
- `GET /collection?prefix={prefix}&pretty=1` → list of icons in a collection
- Docs: https://iconify.design/docs/api/search.html , https://iconify.design/docs/api/svg.html , https://iconify.design/docs/types/iconify-json.html

## Structure

```
flow-plugins/
└── Flow.Launcher.Plugin.Iconify/
    ├── plugin.json              # ID AACC3BDF-5D40-4897-A705-1286E335B613, ActionKeyword iconify
    ├── Flow.Launcher.Plugin.Iconify.csproj
    ├── Main.cs                  # IAsyncPlugin + IContextMenu + models SearchResponse/CollectionInfo
    └── Images/icon.png
```

## plugin.json

```json
{
  "$schema": "https://www.flowlauncher.com/schemas/plugin.schema.json",
  "ID": "AACC3BDF-5D40-4897-A705-1286E335B613",
  "ActionKeyword": "iconify",
  "Name": "Iconify",
  "Description": "Search and copy Iconify SVG icons without leaving Flow Launcher",
  "Author": "IconifyPlugin",
  "Version": "1.0.0",
  "Language": "csharp",
  "Website": "https://github.com/IconifyPlugin/Flow.Launcher.Plugin.Iconify",
  "IcoPath": "Images\\icon.png",
  "ExecuteFileName": "Flow.Launcher.Plugin.Iconify.dll"
}
```

## Roadmap

- [ ] Settings: `apiBase`, `limit`, default `color`, `keepOpen` vs `hideAfterCopy`
- [ ] Local cache for searches + collections (`%APPDATA%`)
- [ ] Copy as JSX / React / Vue (`<Icon icon="mdi:home" />`)
- [ ] Download SVG as file

## License

MIT
