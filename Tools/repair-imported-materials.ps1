param(
    [string] $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string] $SourceRoot = 'E:\SyntyPacks',
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
$worldShader = 'shaders/synty/synty_world.shader_c'
$grassShader = 'shaders/foliage/valkark_grass.shader_c'
$assetRoot = Join-Path $ProjectRoot 'Assets\ThirdParty\Synty'

function Normalize-Id([string] $value) {
    return (($value.Trim().ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_'))
}

function Read-PackMaterialData([string] $packSource) {
    $meshSlots = @{}
    $slotCounts = @{}
    $slotDetails = @{}
    foreach ($list in Get-ChildItem -LiteralPath $packSource -Recurse -Filter 'MaterialList_*.txt') {
        $mesh = $null
        foreach ($raw in Get-Content -LiteralPath $list.FullName) {
            $line = $raw.Trim()
            if ($line.StartsWith('Mesh Name:', [StringComparison]::OrdinalIgnoreCase)) {
                $mesh = $line.Substring(10).Trim()
                $meshKey = Normalize-Id $mesh
                if (-not $meshSlots.ContainsKey($meshKey)) { $meshSlots[$meshKey] = [Collections.Generic.List[object]]::new() }
            }
            elseif ($line.StartsWith('Slot:', [StringComparison]::OrdinalIgnoreCase) -and $mesh) {
                $value = $line.Substring(5).Trim()
                $open = $value.LastIndexOf(' (', [StringComparison]::Ordinal)
                $detail = $null
                if ($open -ge 0 -and $value.EndsWith(')')) {
                    $detail = $value.Substring($open + 2, $value.Length - $open - 3).Trim()
                    $name = $value.Substring(0, $open).Trim()
                } else { $name = $value }
                $custom = $detail -eq 'Uses custom shader'
                $texture = if ($custom) { $null } else { $detail }
                $slot = [pscustomobject]@{ Name = $name; Texture = $texture; Custom = $custom }
                $meshSlots[(Normalize-Id $mesh)].Add($slot)
                $slotKey = Normalize-Id $name
                if (-not $slotCounts.ContainsKey($slotKey)) { $slotCounts[$slotKey] = 0; $slotDetails[$slotKey] = $slot }
                $slotCounts[$slotKey]++
            }
        }
    }
    $dominant = $slotCounts.GetEnumerator() | Where-Object { -not $slotDetails[$_.Key].Custom } | Sort-Object Value -Descending | Select-Object -First 1
    return [pscustomobject]@{ MeshSlots = $meshSlots; SlotDetails = $slotDetails; Dominant = if ($dominant) { $slotDetails[$dominant.Key] } else { $null } }
}

function Find-Texture([string] $packSource, [string] $hint) {
    if ([string]::IsNullOrWhiteSpace($hint)) { return $null }
    $stem = [IO.Path]::GetFileNameWithoutExtension($hint)
    return Get-ChildItem -LiteralPath $packSource -Recurse -File | Where-Object {
        $_.Extension -in '.png','.tga','.jpg','.jpeg','.tif','.tiff','.bmp' -and
        [IO.Path]::GetFileNameWithoutExtension($_.Name).Equals($stem, [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object @{ Expression = { if ($_.Extension -eq '.png') { 0 } else { 1 } } }, FullName | Select-Object -First 1
}

function Is-FlatGrass([string] $modelName) {
    $name = $modelName.ToLowerInvariant()
    return $name.Contains('grass') -and
        -not $name.Contains('ground') -and
        -not $name.Contains('river') -and
        -not $name.Contains('slope') -and
        -not $name.Contains('rubble')
}

function Write-Atomic([string] $path, [string] $contents) {
    $temp = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp, $contents)
    Move-Item -LiteralPath $temp -Destination $path -Force
}

function Ensure-Material([string] $packName, [string] $packSource, $slot, [bool] $flatGrass) {
    $suffix = if ($flatGrass) { '_grass_plane' } else { '' }
    $materialName = (Normalize-Id $slot.Name) + $suffix
    $relative = "thirdparty/synty/$packName/materials/$materialName.vmat"
    $materialPath = Join-Path $ProjectRoot ('Assets\' + $relative.Replace('/', '\'))
    if (Test-Path -LiteralPath $materialPath) { return $relative }

    $texture = Find-Texture $packSource $slot.Texture
    $textureAsset = $null
    if ($texture) {
        $textureDirectory = Join-Path $assetRoot "$packName\Textures"
        if ($Apply) { New-Item -ItemType Directory -Path $textureDirectory -Force | Out-Null }
        $textureDestination = Join-Path $textureDirectory $texture.Name
        if ($Apply -and -not (Test-Path -LiteralPath $textureDestination)) {
            $tempTexture = "$textureDestination.$([Guid]::NewGuid().ToString('N')).tmp"
            Copy-Item -LiteralPath $texture.FullName -Destination $tempTexture
            Move-Item -LiteralPath $tempTexture -Destination $textureDestination
        }
        $textureAsset = "ThirdParty/Synty/$packName/Textures/$($texture.Name)"
    }

    $shader = if ($flatGrass) { $grassShader } else { $worldShader }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('Layer0'); $lines.Add('{'); $lines.Add("`tshader `"$shader`"")
    if ($textureAsset) {
        if ($flatGrass) { $lines.Add("`tLeafTexture `"$textureAsset`""); $lines.Add("`tTrunkTexture `"$textureAsset`"") }
        else { $lines.Add("`tTextureColor `"$textureAsset`"") }
    }
    if ($flatGrass) {
        $lines.Add("`tSyntyFoliageLeafSmoothness `"0.25`"")
        $lines.Add("`tSyntyFoliageLeafNormalStrength `"0`"")
        $lines.Add("`tSyntyFoliageRooted `"1`"")
        $lines.Add("`tSyntyFoliageBaseWindInfluence `"1`"")
    } else {
        $defaults = [ordered]@{ F_SYNTY_WORLD_VARIATION_PATTERN='1'; SyntyWorldRoughness='0.77'; SyntyWorldVariationSize='20'; SyntyWorldVariationContrast='0.30'; SyntyWorldColorVariation='0.025'; SyntyWorldNormalVariation='0.018'; SyntyWorldRoughnessVariation='0.055'; SyntyWorldInstanceVariation='0.012'; SyntyWorldMicroVariationSize='6.5'; SyntyWorldMicroColorVariation='0.035'; SyntyWorldMicroRoughnessVariation='0.03'; SyntyWorldMicroNormalVariation='0.02'; SyntyWorldWetnessResponse='0.72'; SyntyWorldMossResponse='0.55'; SyntyWorldDustResponse='0.70' }
        foreach ($entry in $defaults.GetEnumerator()) { $lines.Add("`t$($entry.Key) `"$($entry.Value)`"") }
    }
    $lines.Add('}')
    if ($Apply) { New-Item -ItemType Directory -Path (Split-Path $materialPath) -Force | Out-Null; Write-Atomic $materialPath (($lines -join [Environment]::NewLine) + [Environment]::NewLine) }
    return $relative
}

$sourcePacks = @{}
foreach ($directory in Get-ChildItem -LiteralPath $SourceRoot -Directory) { $sourcePacks[(Normalize-Id $directory.Name)] = $directory.FullName }
$stats = [ordered]@{ ModelsScanned=0; FallbacksRepaired=0; ExistingDefaultsRepointed=0; ExactMeshMappings=0; DominantFallbacks=0; FlatGrass=0; MissingPack=0; MissingMaterialData=0 }

foreach ($packDirectory in Get-ChildItem -LiteralPath $assetRoot -Directory) {
    $packName = Normalize-Id $packDirectory.Name
    if (-not $sourcePacks.ContainsKey($packName)) { $stats.MissingPack++; continue }
    $packSource = $sourcePacks[$packName]
    $data = Read-PackMaterialData $packSource
    foreach ($model in Get-ChildItem -LiteralPath $packDirectory.FullName -Recurse -Filter '*.vmdl') {
        $stats.ModelsScanned++
        $document = [IO.File]::ReadAllText($model.FullName)
        $isFallback = $document -match 'use_global_default\s*=\s*true' -and $document -match 'global_default_material\s*=\s*"materials/default\.vmat"'
        if (-not $isFallback) {
            $firstTarget = [regex]::Match($document, 'to\s*=\s*"(?<target>[^"]+\.vmat)"').Groups['target'].Value
            if ($firstTarget -and $document.Contains('global_default_material = "materials/default.vmat"')) {
                $updated = $document.Replace('global_default_material = "materials/default.vmat"', "global_default_material = `"$firstTarget`"")
                if ($Apply) { Write-Atomic $model.FullName $updated }
                $stats.ExistingDefaultsRepointed++
            }
            continue
        }

        $modelName = [IO.Path]::GetFileNameWithoutExtension($model.Name)
        $key = Normalize-Id $modelName
        $slots = @()
        if ($data.MeshSlots.ContainsKey($key)) { $slots = @($data.MeshSlots[$key] | Group-Object Name | ForEach-Object { $_.Group[0] }); $stats.ExactMeshMappings++ }
        elseif ($data.Dominant) { $slots = @($data.Dominant); $stats.DominantFallbacks++ }
        else { $stats.MissingMaterialData++; continue }

        $flatGrass = Is-FlatGrass $modelName
        if ($flatGrass) { $stats.FlatGrass++ }
        $targets = @($slots | ForEach-Object { Ensure-Material $packName $packSource $_ $flatGrass })
        $indent = "`t`t`t`t`t`t"
        if ($data.MeshSlots.ContainsKey($key)) {
            $remapLines = [Collections.Generic.List[string]]::new()
            $remapLines.Add('remaps = [')
            for ($index = 0; $index -lt $slots.Count; $index++) {
                $remapLines.Add("$indent`t{")
                $remapLines.Add("$indent`t`tfrom = `"$($slots[$index].Name).vmat`"")
                $remapLines.Add("$indent`t`tto = `"$($targets[$index])`"")
                $remapLines.Add("$indent`t},")
            }
            $remapLines.Add("$indent]")
            $remap = $remapLines -join [Environment]::NewLine
            $useGlobal = 'false'
        } else {
            $remap = 'remaps = []'
            $useGlobal = 'true'
        }
        $replacement = "$remap`r`n$indent" + "use_global_default = $useGlobal`r`n$indent" + "global_default_material = `"$($targets[0])`""
        $updated = [regex]::Replace($document, 'remaps\s*=\s*\[.*?\]\s*use_global_default\s*=\s*true\s*global_default_material\s*=\s*"materials/default\.vmat"', $replacement, [Text.RegularExpressions.RegexOptions]::Singleline)
        if ($updated -eq $document) { throw "Could not update material group in $($model.FullName)" }
        if ($Apply) { Write-Atomic $model.FullName $updated }
        $stats.FallbacksRepaired++
    }
}

[pscustomobject]$stats | Format-List
if (-not $Apply) { Write-Host 'Dry run only. Re-run with -Apply to write changes.' }