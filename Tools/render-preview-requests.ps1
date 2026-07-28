param(
	[Parameter(Mandatory = $true)][string] $RequestManifest,
	[Parameter(Mandatory = $true)][string] $ResultManifest,
	[Parameter(Mandatory = $true)][string] $SourceRoot,
	[Parameter(Mandatory = $true)][string] $CacheRoot
)

$ErrorActionPreference = 'Stop'
$null = Resolve-Path -LiteralPath $SourceRoot
$cachePath = [IO.Path]::GetFullPath($CacheRoot)
$queueRoot = Join-Path $cachePath 'state\v2\ipc'
$requestsRoot = Join-Path $queueRoot 'requests'
$resultsRoot = Join-Path $queueRoot 'results'
New-Item -ItemType Directory -Path $requestsRoot,$resultsRoot -Force | Out-Null

$requestId = [Guid]::NewGuid().ToString('N')
$queuedRequest = Join-Path $requestsRoot "$requestId.json"
$queuedResult = Join-Path $resultsRoot "$requestId.json"
$temporaryRequest = "$queuedRequest.tmp"
Copy-Item -LiteralPath $RequestManifest -Destination $temporaryRequest
Move-Item -LiteralPath $temporaryRequest -Destination $queuedRequest -Force
$watcher = New-Object IO.FileSystemWatcher $resultsRoot,"$requestId.json*"
$watcher.EnableRaisingEvents = $true

$daemonScript = Join-Path $PSScriptRoot 'run-preview-daemon.ps1'
$arguments = @(
	'-NoProfile',
	'-ExecutionPolicy', 'Bypass',
	'-File', "`"$daemonScript`"",
	'-QueueRoot', "`"$queueRoot`""
) -join ' '
$null = Start-Process -FilePath powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru

$deadline = [DateTime]::UtcNow.AddMinutes(30)
try {
	while (-not (Test-Path -LiteralPath $queuedResult) -and [DateTime]::UtcNow -lt $deadline) {
		$remaining = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
		$null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All, $remaining)
	}
	if (-not (Test-Path -LiteralPath $queuedResult)) {
		throw 'Persistent Synty preview worker did not finish within 30 minutes.'
	}
	Copy-Item -LiteralPath $queuedResult -Destination $ResultManifest -Force
} finally {
	$watcher.Dispose()
	Remove-Item -LiteralPath $queuedRequest,$queuedResult -Force -ErrorAction SilentlyContinue
}
