param(
	[Parameter(Mandatory = $true)][string] $SourceFbx,
	[Parameter(Mandatory = $true)][string] $PackRoot,
	[Parameter(Mandatory = $true)][string] $OutputPng,
	[string[]] $TextureHints = @(),
	[string] $BindingsJson,
	[ValidateRange(64, 512)][int] $Resolution = 96,
	[ValidateRange(1, 64)][int] $Samples = 8
)

$ErrorActionPreference = 'Stop'
$blender = Get-Command blender.exe -ErrorAction SilentlyContinue
if (-not $blender) {
	$blenderPath = 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe'
	if (-not (Test-Path -LiteralPath $blenderPath)) {
		throw 'Blender 4.5 is not installed.'
	}
} else {
	$blenderPath = $blender.Source
}

$sourcePath = (Resolve-Path -LiteralPath $SourceFbx).Path
$packPath = (Resolve-Path -LiteralPath $PackRoot).Path
$outputPath = [IO.Path]::GetFullPath($OutputPng)
$renderScript = Join-Path $PSScriptRoot 'render-preview.py'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('synty-preview-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null

try {
	$packFiles = Get-ChildItem -LiteralPath $packPath -Recurse -File
	$rawBindings = @()
	if ($BindingsJson) {
		$parsedBindings = Get-Content -LiteralPath $BindingsJson -Raw | ConvertFrom-Json
		foreach ($parsedBinding in $parsedBindings) {
			$rawBindings += $parsedBinding
		}
	} elseif ($TextureHints.Count -gt 0) {
		for ($slotOrdinal = 0; $slotOrdinal -lt $TextureHints.Count; $slotOrdinal++) {
			$rawBindings += [pscustomobject]@{
				MeshName = [IO.Path]::GetFileNameWithoutExtension($sourcePath)
				SlotName = $TextureHints[$slotOrdinal]
				SlotOrdinal = $slotOrdinal
				TextureHint = $TextureHints[$slotOrdinal]
			}
		}
	}

	$resolvedBindings = @()
	foreach ($binding in $rawBindings) {
		if (-not $binding.MeshName -or $null -eq $binding.SlotOrdinal) {
			throw 'Every preview binding must identify its MaterialList mesh and slot ordinal.'
		}
		if (-not $binding.TextureHint) {
			continue
		}
		$name = [IO.Path]::GetFileName([string]$binding.TextureHint)
		$matches = @($packFiles | Where-Object {
			$_.Name -ieq $name -or $_.BaseName -ieq [IO.Path]::GetFileNameWithoutExtension($name)
		})
		if ($matches.Count -eq 0) {
			throw "MaterialList preview texture '$($binding.TextureHint)' was not found under '$packPath'."
		}
		if ($matches.Count -gt 1) {
			throw "MaterialList preview texture '$($binding.TextureHint)' is ambiguous under '$packPath'."
		}
		$resolvedBindings += [ordered]@{
			mesh_name = [string]$binding.MeshName
			slot_name = [string]$binding.SlotName
			slot_ordinal = [int]$binding.SlotOrdinal
			texture_path = $matches[0].FullName
		}
	}

	$resolvedJson = Join-Path $workRoot 'bindings.json'
	$resolvedBindings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedJson -Encoding utf8
	$outputDirectory = Split-Path -Parent $outputPath
	New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
	& $blenderPath --background --factory-startup --python $renderScript -- `
		--source-fbx $sourcePath --output-png $outputPath --bindings-json $resolvedJson --resolution $Resolution --samples $Samples
	if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath)) {
		throw "Blender failed to render '$sourcePath'."
	}
} finally {
	Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
