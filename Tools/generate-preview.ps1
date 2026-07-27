param(
	[Parameter(Mandatory = $true)][string] $SourceFbx,
	[Parameter(Mandatory = $true)][string] $PackRoot,
	[Parameter(Mandatory = $true)][string] $OutputPng,
	[string[]] $TextureHints = @(),
	[string] $BindingsJson
)

$ErrorActionPreference = 'Stop'
$f3d = Get-Command f3d.exe -ErrorAction SilentlyContinue
if (-not $f3d) {
	$f3dPath = 'C:\Program Files\F3D\bin\f3d.exe'
	if (-not (Test-Path -LiteralPath $f3dPath)) {
		throw 'F3D is not installed. Install f3d-app.f3d before generating Synty previews.'
	}
} else {
	$f3dPath = $f3d.Source
}

$outputDirectory = Split-Path -Parent $OutputPng
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('synty-preview-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null

try {
	$stagedFbx = Join-Path $workRoot ([IO.Path]::GetFileName($SourceFbx))
	Copy-Item -LiteralPath $SourceFbx -Destination $stagedFbx
	$packFiles = Get-ChildItem -LiteralPath $PackRoot -Recurse -File
	$bindings = @()
	if ($BindingsJson) {
		$bindings = @(Get-Content -LiteralPath $BindingsJson -Raw | ConvertFrom-Json)
		$invalid = @($bindings | Where-Object { -not $_.MeshName -or -not $_.SlotName })
		if ($invalid.Count -gt 0) {
			throw 'Every preview binding must identify its MaterialList mesh and slot.'
		}
		$TextureHints = @($bindings | ForEach-Object { $_.TextureHint } | Where-Object { $_ })
	}

	foreach ($hint in $TextureHints | Where-Object { $_ } | Select-Object -Unique) {
		$name = [IO.Path]::GetFileName($hint)
		$matches = @($packFiles | Where-Object {
			$_.Name -ieq $name -or $_.BaseName -ieq [IO.Path]::GetFileNameWithoutExtension($name)
		})
		if ($matches.Count -eq 0) {
			throw "MaterialList preview texture '$hint' was not found under '$PackRoot'."
		}
		if ($matches.Count -gt 1) {
			throw "MaterialList preview texture '$hint' is ambiguous under '$PackRoot'."
		}
		$texture = $matches[0]
		Copy-Item -LiteralPath $texture.FullName -Destination (Join-Path $workRoot $texture.Name) -Force
	}

	$render = Start-Process -FilePath $f3dPath -ArgumentList @(
		'--output', "`"$OutputPng`"",
		'--resolution', '256,256',
		"`"$stagedFbx`""
	) -Wait -PassThru -WindowStyle Hidden
	if ($render.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $OutputPng)) {
		throw "F3D failed to render '$SourceFbx'."
	}
} finally {
	Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
