using System;
using System.IO;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyPreviewMigrationResult( int Copied, int Existing, int Invalid, int Failed );

public static class SyntyPreviewMigration
{
	public const string LegacyArchiveVersion = "legacy-v1";

	public static SyntyPreviewMigrationResult CopyAndVerify( string legacyPreviewRoot, string cacheRoot )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( legacyPreviewRoot );
		ArgumentException.ThrowIfNullOrWhiteSpace( cacheRoot );
		if ( !Directory.Exists( legacyPreviewRoot ) )
			return new( 0, 0, 0, 0 );

		var copied = 0;
		var existing = 0;
		var invalid = 0;
		var failed = 0;
		foreach ( var source in Directory.EnumerateFiles( legacyPreviewRoot, "*.png", SearchOption.AllDirectories ) )
		{
			try
			{
				if ( !HasPngSignature( source ) )
				{
					invalid++;
					continue;
				}

				var relative = Path.GetRelativePath( legacyPreviewRoot, source );
				var destination = Path.Combine( cacheRoot, "previews", LegacyArchiveVersion, relative );
				if ( File.Exists( destination ) && HasPngSignature( destination ) )
				{
					existing++;
					continue;
				}

				Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
				var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
				File.Copy( source, temporary, true );
				if ( !HasPngSignature( temporary ) || new FileInfo( temporary ).Length != new FileInfo( source ).Length )
					throw new InvalidDataException( $"Copied preview '{source}' failed verification." );
				File.Move( temporary, destination, true );
				copied++;
			}
			catch
			{
				failed++;
			}
		}
		return new( copied, existing, invalid, failed );
	}

	public static bool HasPngSignature( string path )
	{
		if ( !File.Exists( path ) || new FileInfo( path ).Length < 8 )
			return false;
		Span<byte> signature = stackalloc byte[8];
		using var stream = File.OpenRead( path );
		return stream.Read( signature ) == signature.Length
			&& signature.SequenceEqual( new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 } );
	}
}
