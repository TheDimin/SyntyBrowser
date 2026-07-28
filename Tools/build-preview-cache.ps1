param(
	[Parameter(Mandatory = $true)][string] $SourceRoot,
	[Parameter(Mandatory = $true)][string] $ProjectRoot,
	[ValidateRange(64, 256)][int] $Resolution = 96,
	[ValidateRange(1, 64)][int] $Samples = 8,
	[ValidateRange(10, 500)][int] $BatchSize = 50,
	[ValidateRange(4, 128)][int] $MinFreeVirtualGb = 16,
	[ValidateRange(2, 128)][int] $MinFreePhysicalGb = 8,
	[string] $Filter = '*',
	[ValidateRange(0, 1000000)][int] $Limit = 0,
	[switch] $Force
)

$ErrorActionPreference = 'Stop'

function Normalize-Id([string] $Value) {
	$id = ($Value.Trim().ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_')
	if ($id) { return $id }
	return 'asset'
}

function Find-Blender {
	$command = Get-Command blender.exe -ErrorAction SilentlyContinue
	if ($command) { return $command.Source }
	$installed = 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe'
	if (Test-Path -LiteralPath $installed) { return $installed }
	throw 'Blender 4.5 is not installed.'
}

$sourcePath = (Resolve-Path -LiteralPath $SourceRoot).Path
$projectPath = (Resolve-Path -LiteralPath $ProjectRoot).Path
$skipManifest = Join-Path $projectPath '.sbox\synty-browser\preview-skips.json'
$knownSkipped = @{}
if (-not $Force -and (Test-Path -LiteralPath $skipManifest)) {
	foreach ($entry in @(Get-Content -LiteralPath $skipManifest -Raw | ConvertFrom-Json)) {
		$knownSkipped[$entry.output_png] = $true
	}
}
$materialLists = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Filter 'MaterialList_*.txt')
if ($materialLists.Count -eq 0) {
	throw "No MaterialList_*.txt files were found under '$sourcePath'."
}

$jobs = [Collections.Generic.List[object]]::new()
$skipped = 0
foreach ($materialList in $materialLists) {
	$packRoot = $materialList.Directory.FullName
	$packId = Normalize-Id $materialList.Directory.Name
	$packFiles = @(Get-ChildItem -LiteralPath $packRoot -Recurse -File)
	$filesByName = @{}
	foreach ($file in $packFiles) {
		foreach ($key in @($file.Name, $file.BaseName) | Select-Object -Unique) {
			if (-not $filesByName.ContainsKey($key)) {
				$filesByName[$key] = [Collections.Generic.List[IO.FileInfo]]::new()
			}
			$filesByName[$key].Add($file)
		}
	}
	$fbxByName = @{}
	foreach ($file in $packFiles | Where-Object Extension -IEq '.fbx') {
		if (-not $fbxByName.ContainsKey($file.BaseName)) {
			$fbxByName[$file.BaseName] = [Collections.Generic.List[string]]::new()
		}
		$fbxByName[$file.BaseName].Add($file.FullName)
	}

	$text = Get-Content -LiteralPath $materialList.FullName -Raw
	$prefabs = [regex]::Matches(
		$text,
		'(?ms)^Prefab Name:\s*(?<name>[^\r\n]+)\r?\n(?<body>.*?)(?=^Prefab Name:|\z)' )
	foreach ($prefab in $prefabs) {
		$name = $prefab.Groups['name'].Value.Trim()
		if ($name -notlike $Filter) { continue }
		if (-not $fbxByName.ContainsKey($name) -or $fbxByName[$name].Count -ne 1) {
			Write-Warning "Skipping '$name': expected exactly one matching FBX."
			$skipped++
			continue
		}

		$output = Join-Path $projectPath ".sbox\synty-browser\previews\$packId\$(Normalize-Id $name).png"
		if (-not $Force -and (Test-Path -LiteralPath $output)) { continue }
		if (-not $Force -and $knownSkipped.ContainsKey($output)) {
			$skipped++
			continue
		}

		$bindings = [Collections.Generic.List[object]]::new()
		$meshes = [regex]::Matches(
			$prefab.Groups['body'].Value,
			'(?ms)^\s*Mesh Name:\s*(?<name>[^\r\n]+)\r?\n(?<body>.*?)(?=^\s*Mesh Name:|\z)' )
		foreach ($mesh in $meshes) {
			$meshName = $mesh.Groups['name'].Value.Trim()
			if ($meshName -match '(?i)(?:^|_)(?:collision|ucx|ubx|ucp|usp)(?:_|$)' -or
				$meshName -match '(?i)(?:^|_)LOD[1-9][0-9]*(?:_|$)') {
				continue
			}
			$slotOrdinal = 0
			foreach ($slot in [regex]::Matches($mesh.Groups['body'].Value, '(?m)^\s*Slot:\s*(?<value>[^\r\n]+)')) {
				$value = $slot.Groups['value'].Value.Trim()
				$detailMatch = [regex]::Match($value, '^(?<name>.*?)\s+\((?<detail>[^()]*)\)$')
				$slotName = if ($detailMatch.Success) { $detailMatch.Groups['name'].Value.Trim() } else { $value }
				$detail = if ($detailMatch.Success) { $detailMatch.Groups['detail'].Value.Trim() } else { $null }
				$hint = if ($detail -ieq 'Uses custom shader') { $slotName } else { $detail }
				if ($hint -and $hint -ine 'No Albedo Texture') {
					$hintName = [IO.Path]::GetFileName($hint)
					$hintBase = [IO.Path]::GetFileNameWithoutExtension($hintName)
					$matches = @(
						@($filesByName[$hintName]) + @($filesByName[$hintBase]) |
							Where-Object { $_ } |
							Sort-Object FullName -Unique
					)
					if ($matches.Count -eq 1) {
						$bindings.Add([ordered]@{
							mesh_name = $meshName
							slot_name = $slotName
							slot_ordinal = $slotOrdinal
							texture_path = $matches[0].FullName
						})
					} else {
						Write-Warning "Using a neutral preview material for '$name/$meshName[$slotOrdinal]': '$hint' matched $($matches.Count) files."
						$bindings.Add([ordered]@{
							mesh_name = $meshName
							slot_name = $slotName
							slot_ordinal = $slotOrdinal
							texture_path = $null
						})
					}
				} else {
					$bindings.Add([ordered]@{
						mesh_name = $meshName
						slot_name = $slotName
						slot_ordinal = $slotOrdinal
						texture_path = $null
					})
				}
				$slotOrdinal++
			}
		}

		$jobs.Add([ordered]@{
			source_fbx = $fbxByName[$name][0]
			output_png = $output
			bindings = @($bindings)
		})
		if ($Limit -gt 0 -and $jobs.Count -ge $Limit) { break }
	}
	if ($Limit -gt 0 -and $jobs.Count -ge $Limit) { break }
}

if ($jobs.Count -eq 0) {
	Write-Host "Preview cache is already current; no matching thumbnails need rendering."
	exit 0
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('synty-preview-cache-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null
try {
	$renderScript = Join-Path $PSScriptRoot 'render-preview.py'
	$blender = Find-Blender
	$chunkCount = [Math]::Ceiling($jobs.Count / $BatchSize)
	Write-Host "Rendering $($jobs.Count) thumbnail(s) at ${Resolution}x${Resolution}, $Samples sample(s), recycling Blender every $BatchSize asset(s)..."
	for ($offset = 0; $offset -lt $jobs.Count; $offset += $BatchSize) {
		$memory = Get-CimInstance Win32_OperatingSystem
		$freeVirtualGb = $memory.FreeVirtualMemory * 1KB / 1GB
		$freePhysicalGb = $memory.FreePhysicalMemory * 1KB / 1GB
		if ($freeVirtualGb -lt $MinFreeVirtualGb -or $freePhysicalGb -lt $MinFreePhysicalGb) {
			throw "Preview generation paused before the next chunk: only $([Math]::Round($freeVirtualGb, 1)) GB virtual and $([Math]::Round($freePhysicalGb, 1)) GB physical memory remain."
		}
		$last = [Math]::Min($offset + $BatchSize - 1, $jobs.Count - 1)
		$chunk = @($jobs[$offset..$last])
		$chunkNumber = [Math]::Floor($offset / $BatchSize) + 1
		$manifest = Join-Path $workRoot "manifest-$chunkNumber.json"
		$renderSkips = Join-Path $workRoot "skipped-$chunkNumber.json"
		ConvertTo-Json -InputObject $chunk -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
		Write-Host "Starting Blender chunk $chunkNumber/$chunkCount ($($offset + 1)-$($last + 1) of $($jobs.Count))..."
		& $blender --background --factory-startup --python $renderScript -- `
			--manifest-json $manifest --skipped-json $renderSkips --resolution $Resolution --samples $Samples
		if ($LASTEXITCODE -ne 0) { throw "Blender chunk $chunkNumber/$chunkCount exited with code $LASTEXITCODE." }
		$newSkips = if (Test-Path -LiteralPath $renderSkips) { @(Get-Content -LiteralPath $renderSkips -Raw | ConvertFrom-Json) } else { @() }
		if ($newSkips.Count -gt 0) {
			$existingSkips = if (Test-Path -LiteralPath $skipManifest) { @(Get-Content -LiteralPath $skipManifest -Raw | ConvertFrom-Json) } else { @() }
			$mergedSkips = @($existingSkips) + @($newSkips) | Group-Object output_png | ForEach-Object { $_.Group[-1] }
			New-Item -ItemType Directory -Path (Split-Path -Parent $skipManifest) -Force | Out-Null
			ConvertTo-Json -InputObject @($mergedSkips) -Depth 4 | Set-Content -LiteralPath $skipManifest -Encoding utf8
			$skipped += $newSkips.Count
		}
		$skippedOutputs = @{}
		foreach ($entry in $newSkips) { $skippedOutputs[$entry.output_png] = $true }
		$missing = @($chunk | Where-Object { -not (Test-Path -LiteralPath $_.output_png) -and -not $skippedOutputs.ContainsKey($_.output_png) })
		if ($missing.Count -gt 0) { throw "Blender chunk $chunkNumber/$chunkCount completed without writing $($missing.Count) expected thumbnail(s)." }
		Write-Host "Completed Blender chunk $chunkNumber/$chunkCount; progress is persisted in the preview cache."
	}
	Write-Host "Cached $($jobs.Count) thumbnail(s). Skipped $skipped invalid asset(s)."
} finally {
	Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
