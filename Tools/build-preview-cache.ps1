param(
	[Parameter(Mandatory = $true)][string] $SourceRoot,
	[Parameter(Mandatory = $true)][string] $ProjectRoot,
	[ValidateRange(64, 256)][int] $Resolution = 128,
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
				$hint = if ($detailMatch.Success) { $detailMatch.Groups['detail'].Value.Trim() } else { $null }
				if ($hint -and $hint -ine 'Uses custom shader') {
					$hintName = [IO.Path]::GetFileName($hint)
					$hintBase = [IO.Path]::GetFileNameWithoutExtension($hintName)
					$matches = @($packFiles | Where-Object {
						$_.Name -ieq $hintName -or $_.BaseName -ieq $hintBase
					})
					if ($matches.Count -eq 1) {
						$bindings.Add([ordered]@{
							mesh_name = $meshName
							slot_name = $slotName
							slot_ordinal = $slotOrdinal
							texture_path = $matches[0].FullName
						})
					} else {
						Write-Warning "Skipping texture binding '$name/$meshName[$slotOrdinal]': '$hint' matched $($matches.Count) files."
					}
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
	$manifest = Join-Path $workRoot 'manifest.json'
	ConvertTo-Json -InputObject @($jobs) -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
	$renderScript = Join-Path $PSScriptRoot 'render-preview.py'
	Write-Host "Rendering $($jobs.Count) thumbnail(s) at ${Resolution}x${Resolution} in one offline Blender session..."
	& (Find-Blender) --background --factory-startup --python $renderScript -- `
		--manifest-json $manifest --resolution $Resolution
	if ($LASTEXITCODE -ne 0) {
		throw "Blender exited with code $LASTEXITCODE."
	}
	$missing = @($jobs | Where-Object { -not (Test-Path -LiteralPath $_.output_png) })
	if ($missing.Count -gt 0) {
		throw "Blender completed without writing $($missing.Count) expected thumbnail(s)."
	}
	Write-Host "Cached $($jobs.Count) thumbnail(s). Skipped $skipped invalid asset(s)."
} finally {
	Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
