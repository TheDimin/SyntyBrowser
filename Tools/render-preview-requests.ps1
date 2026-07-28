param(
	[Parameter(Mandatory = $true)][string] $RequestManifest,
	[Parameter(Mandatory = $true)][string] $ResultManifest,
	[Parameter(Mandatory = $true)][string] $SourceRoot,
	[Parameter(Mandatory = $true)][string] $CacheRoot,
	[ValidateRange(64, 256)][int] $Resolution = 96,
	[ValidateRange(1, 64)][int] $Samples = 8
)

$ErrorActionPreference = 'Stop'

function Find-Blender {
	$command = Get-Command blender.exe -ErrorAction SilentlyContinue
	if ($command) { return $command.Source }
	$installed = 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe'
	if (Test-Path -LiteralPath $installed) { return $installed }
	throw 'Blender 4.5 is not installed.'
}

$null = Resolve-Path -LiteralPath $SourceRoot
$cachePath = [IO.Path]::GetFullPath($CacheRoot)
$requests = Get-Content -LiteralPath $RequestManifest -Raw | ConvertFrom-Json
$results = [Collections.Generic.List[object]]::new()
$blenderJobs = [Collections.Generic.List[object]]::new()
$packIndexes = @{}

foreach ($request in $requests) {
	try {
		$sourcePath = (Resolve-Path -LiteralPath ([string]$request.source_fbx)).Path
		$packPath = (Resolve-Path -LiteralPath ([string]$request.pack_root)).Path
		$outputPath = [IO.Path]::GetFullPath([string]$request.output_png)
		if (-not $outputPath.StartsWith($cachePath, [StringComparison]::OrdinalIgnoreCase)) {
			throw "Preview output escapes shared cache root: '$outputPath'."
		}

		if (-not $packIndexes.ContainsKey($packPath)) {
			$index = @{}
			foreach ($file in Get-ChildItem -LiteralPath $packPath -Recurse -File) {
				foreach ($key in @($file.Name, $file.BaseName) | Select-Object -Unique) {
					if (-not $index.ContainsKey($key)) {
						$index[$key] = [Collections.Generic.List[string]]::new()
					}
					$index[$key].Add($file.FullName)
				}
			}
			$packIndexes[$packPath] = $index
		}

		$bindings = [Collections.Generic.List[object]]::new()
		foreach ($binding in @($request.bindings)) {
			$texturePath = $null
			$hint = [string]$binding.texture_hint
			if ($hint) {
				$hintName = [IO.Path]::GetFileName($hint)
				$hintBase = [IO.Path]::GetFileNameWithoutExtension($hintName)
				$matches = @(
					@($packIndexes[$packPath][$hintName]) + @($packIndexes[$packPath][$hintBase]) |
						Where-Object { $_ } |
						Sort-Object -Unique
				)
				if ($matches.Count -eq 1) { $texturePath = $matches[0] }
			}
			$bindings.Add([ordered]@{
				mesh_name = [string]$binding.mesh_name
				slot_name = [string]$binding.slot_name
				slot_ordinal = [int]$binding.slot_ordinal
				texture_path = $texturePath
			})
		}
		$blenderJobs.Add([ordered]@{
			asset_id = [string]$request.asset_id
			source_fbx = $sourcePath
			output_png = $outputPath
			bindings = @($bindings)
		})
	} catch {
		$results.Add([ordered]@{ assetId = [string]$request.asset_id; status = 'failed'; error = $_.Exception.Message })
	}
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('synty-preview-worker-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null
try {
	if ($blenderJobs.Count -gt 0) {
		$manifest = Join-Path $workRoot 'render.json'
		$skips = Join-Path $workRoot 'skipped.json'
		ConvertTo-Json -InputObject @($blenderJobs) -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
		& (Find-Blender) --background --factory-startup --python (Join-Path $PSScriptRoot 'render-preview.py') -- `
			--manifest-json $manifest --skipped-json $skips --resolution $Resolution --samples $Samples
		$exitCode = $LASTEXITCODE
		$skippedByOutput = @{}
		if (Test-Path -LiteralPath $skips) {
			foreach ($skip in (Get-Content -LiteralPath $skips -Raw | ConvertFrom-Json)) {
				$skippedByOutput[[string]$skip.output_png] = [string]$skip.reason
			}
		}
		foreach ($job in $blenderJobs) {
			if (Test-Path -LiteralPath ([string]$job.output_png)) {
				$results.Add([ordered]@{ assetId = [string]$job.asset_id; status = 'completed'; error = $null })
			} elseif ($skippedByOutput.ContainsKey([string]$job.output_png)) {
				$results.Add([ordered]@{ assetId = [string]$job.asset_id; status = 'skipped'; error = $skippedByOutput[[string]$job.output_png] })
			} else {
				$results.Add([ordered]@{ assetId = [string]$job.asset_id; status = 'failed'; error = "Blender exited with code $exitCode without producing a preview." })
			}
		}
	}
} finally {
	$directory = Split-Path -Parent ([IO.Path]::GetFullPath($ResultManifest))
	New-Item -ItemType Directory -Path $directory -Force | Out-Null
	ConvertTo-Json -InputObject @($results) -Depth 4 | Set-Content -LiteralPath $ResultManifest -Encoding utf8
	Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
