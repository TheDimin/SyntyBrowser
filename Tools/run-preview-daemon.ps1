param(
	[Parameter(Mandatory = $true)][string] $QueueRoot,
	[ValidateRange(5, 300)][int] $IdleSeconds = 30,
	[ValidateRange(64, 256)][int] $Resolution = 96,
	[ValidateRange(1, 64)][int] $Samples = 8
)

$ErrorActionPreference = 'Stop'
$mutex = New-Object Threading.Mutex($false, 'Global\SyntyPreviewWorkerV2')
$ownsMutex = $false
try {
	$ownsMutex = $mutex.WaitOne(0)
	if (-not $ownsMutex) { exit 0 }

	$blender = Get-Command blender.exe -ErrorAction SilentlyContinue
	$blenderPath = if ($blender) { $blender.Source } else { 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' }
	if (-not (Test-Path -LiteralPath $blenderPath)) { throw 'Blender 4.5 is not installed.' }

	New-Item -ItemType Directory -Path (Join-Path $QueueRoot 'requests'),(Join-Path $QueueRoot 'results') -Force | Out-Null
	& $blenderPath --background --factory-startup --python (Join-Path $PSScriptRoot 'render-preview-daemon.py') -- `
		--queue-root ([IO.Path]::GetFullPath($QueueRoot)) --idle-seconds $IdleSeconds --resolution $Resolution --samples $Samples
	if ($LASTEXITCODE -ne 0) { throw "Persistent Blender preview worker exited with code $LASTEXITCODE." }
} finally {
	if ($ownsMutex) { $mutex.ReleaseMutex() }
	$mutex.Dispose()
}
