using System.Text.Json;
using Editor.Tools.SyntyBrowser;

if ( args.Length < 4 )
{
	Console.Error.WriteLine( "Usage: SyntyPreparationBenchmark <source-root> <assets-root> <settings-json> <staging-root> [count] [workers]" );
	return 2;
}

var sourceRoot = Path.GetFullPath( args[0] );
var assetsRoot = Path.GetFullPath( args[1] );
var settingsPath = Path.GetFullPath( args[2] );
var stagingRoot = Path.GetFullPath( args[3] );
var count = args.Length > 4 ? int.Parse( args[4] ) : 1000;
var workers = args.Length > 5 ? int.Parse( args[5] ) : Math.Clamp( Environment.ProcessorCount - 2, 2, 12 );
var settings = JsonSerializer.Deserialize<SyntyBrowserProjectSettings>(
	File.ReadAllText( settingsPath ),
	new JsonSerializerOptions { PropertyNameCaseInsensitive = true } ) ?? new();
var catalog = SyntySourceCatalog.Build( sourceRoot );
var sampleSize = count + Math.Max( 100, count / 10 );
var eligible = catalog.Assets.Where( source =>
{
	var packName = source.PackName ?? catalog.PackName;
	var existing = Path.Combine(
		assetsRoot,
		SyntyImportService.DefaultDestinationRoot,
		packName,
		"Models",
		$"{source.Id}.vmdl" );
	return !File.Exists( existing )
		&& settings.Packs.TryGetValue( packName, out var pack )
		&& SyntyAutoImportPolicy.CanImport(
			source,
			pack.DefaultShader,
			pack.Materials.Keys.ToHashSet( StringComparer.OrdinalIgnoreCase ) );
}).Take( sampleSize ).ToArray();

if ( eligible.Length < sampleSize )
{
	Console.Error.WriteLine( $"Only {eligible.Length:N0} unimported candidates were found; {sampleSize:N0} are required for the failure-tolerant sample." );
	return 3;
}

var result = SyntyMassImportPreparation.PrepareBatch(
	eligible,
	settings.Packs,
	stagingRoot,
	workers,
	CancellationToken.None );
var invalidOutputs = result.Imports.Where( item => item.Success ).Where( item =>
	item.Files.Length < 2
	|| item.Files.Any( file => !File.Exists( file.StagedPath ) || new FileInfo( file.StagedPath ).Length == 0 )
	|| item.Files.Where( file => file.AssetPath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ).Any( file =>
	{
		var document = File.ReadAllText( file.StagedPath );
		return !document.Contains( "format:modeldoc30", StringComparison.Ordinal )
			|| !document.Contains( "_class = \"RenderMeshFile\"", StringComparison.Ordinal )
			|| !document.Contains( "_class = \"PhysicsHullFromRender\"", StringComparison.Ordinal )
			|| !document.Contains( "remaps =", StringComparison.Ordinal );
	} ) ).Select( item => item.Source.CacheId ).ToArray();
var validatedCount = result.PreparedCount - invalidOutputs.Length;
var temporaryFiles = Directory.EnumerateFiles( stagingRoot, "*.tmp", SearchOption.AllDirectories ).ToArray();
Console.WriteLine( JsonSerializer.Serialize( new
{
	Target = count,
	Candidates = eligible.Length,
	result.PreparedCount,
	ValidatedCount = validatedCount,
	Failed = result.Imports.Length - result.PreparedCount,
	InvalidOutputs = invalidOutputs,
	TemporaryFileCount = temporaryFiles.Length,
	DurationSeconds = result.Duration.TotalSeconds,
	result.AssetsPerMinute,
	Workers = workers,
	StagingRoot = stagingRoot,
	AcceptancePassed = validatedCount >= count
		&& temporaryFiles.Length == 0
		&& result.Duration <= TimeSpan.FromSeconds( 60 ),
	Failures = result.Imports.Where( item => !item.Success ).Take( 20 ).Select( item => new { item.Source.CacheId, item.Error } )
}, new JsonSerializerOptions { WriteIndented = true } ) );
return validatedCount >= count && temporaryFiles.Length == 0 && result.Duration <= TimeSpan.FromSeconds( 60 ) ? 0 : 1;
