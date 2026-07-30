# Synty Browser

Synty Browser is an s&box editor library for browsing developer-owned Synty source packs and selectively importing static models into a project. It contains no Synty models, materials, textures, thumbnails, or other commercial content.

## Features

- Scans one pack or a library of packs without copying source content into the project.
- Uses each pack's `MaterialList_*.txt` as the authoritative mesh/material-slot map.
- Hides collision helpers, UCX shapes, standalone LOD files, animations, and skinned models from the browser.
- Keeps large catalogs responsive with asynchronous scanning, search, a virtualized grid, and imported-asset thumbnails.
- Applies a curated cross-pack asset taxonomy and supports exact browser filters such as `tag:harbor-city`.
- Reads FBX `UnitScaleFactor` and applies the corresponding s&box import scale.
- Imports visible and near-visible assets through s&box and displays their native asset thumbnails.
- Imports transactionally and restores affected output when registration, material compilation, or model compilation fails.
- Plans removal before changing files and reports project assets that reference an imported model or its generated resources.
- Exposes the same catalog, search, import, validation, and removal-planning workflows through a public API and s&box MCP tools.

## Install

Place or clone this repository under your project's `Libraries/` directory, then reopen the project:

```text
MyGame/
  Libraries/
    SyntyBrowser/
      SyntyBrowser.sbproj
```

Open **Editor > Tools > Synty Browser**, choose a local Synty pack folder, and select a default project shader when importing the first asset from a pack. Configuration is stored by the host project at `ProjectSettings/SyntyBrowser.json`; see [`Examples/SyntyBrowser.json`](Examples/SyntyBrowser.json).

Imported output defaults to:

```text
Assets/ThirdParty/Synty/<pack>/
  Models/
  Materials/
  Textures/
```

The source-root preference remains project-local. As cards enter the visible or one-row-near-visible range, the browser imports eligible assets sequentially and uses the native s&box thumbnail. Packs without a configured shader and assets with unresolved custom-shader mappings are ignored.

## Public integration surface

```csharp
using Editor.Tools.SyntyBrowser;

var catalog = SyntyBrowserApi.BuildCatalog( sourceRoot );
var props = SyntyBrowserApi.Search( catalog, "barrel wood" );
var harborAssets = SyntyBrowserApi.Search( catalog, "tag:harbor-city" );
var result = SyntyBrowserApi.Import( catalog, props[0] );

var removal = SyntyBrowserApi.PlanRemoval( props[0] );
if ( removal.References.Length == 0 )
    SyntyBrowserApi.RemoveImport( removal );
```

Removal plans are snapshots. `RemoveImport` refuses a plan with references unless the caller explicitly opts into forcing removal.

## Curated tags

Tags describe individual source assets, not entire packs. Definitions and conservative matching rules live in
`Editor/SyntyAssetTags.cs`; catalog construction resolves them onto each `SyntySourceAsset`, so the editor,
public API, and MCP inspection all see the same metadata. To author a future tag:

1. Add one stable kebab-case ID and human-readable display name.
2. Add narrow positive asset/category terms and explicit incompatible-theme exclusions.
3. Return the tag from `Resolve` only when the asset itself satisfies the rule.
4. Add positive, negative, and `tag:<id>` composition tests.

`Harbor City` currently covers explicitly named harbor structures, vessels and maritime equipment, fishing and
waterfront cargo, plus market/merchant/tavern/shop pieces suitable for a working harbor district. It does not tag
all assets in a maritime pack, and obvious science-fiction, aviation, apocalypse, and submarine assets are excluded.

## MCP

The library registers the `synty_browser` toolset:

- `synty_catalog_status`
- `synty_inspect_asset`
- `synty_import_asset`
- `synty_validate_import`
- `synty_plan_remove_import`
- `synty_remove_import`

All asset IDs may be pack-qualified with `CacheId` when different packs contain the same model name.

## Development

The standalone test suite uses only synthetic files:

```powershell
dotnet test SyntyBrowser.slnx
```

Before release, also open a host s&box project containing the library, let the editor compile it, and check the live editor console for compile or whitelist errors.

## Content and trademark notice

Synty is a trademark of Synty Studios. This project is an independent developer tool and is not affiliated with or endorsed by Synty Studios. You must supply content you are licensed to use.
