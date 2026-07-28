using System;

namespace Editor.Tools.SyntyBrowser;

public static class SyntyAssetSearch
{
	public static SyntySourceAsset[] Search( IEnumerable<SyntySourceAsset> assets, string query )
	{
		var parsed = ParseQuery( query );
		var terms = parsed.Terms;
		if ( terms.Length == 0 )
			return assets.Where( asset => HasTags( asset, parsed.Tags ) ).ToArray();

		return assets
			.Where( asset => HasTags( asset, parsed.Tags ) )
			.Select( asset => (Asset: asset, Score: Score( asset, terms )) )
			.Where( result => result.Score >= 0 )
			.OrderByDescending( result => result.Score )
			.ThenBy( result => result.Asset.DisplayName, StringComparer.OrdinalIgnoreCase )
			.Select( result => result.Asset )
			.ToArray();
	}

	private static int Score( SyntySourceAsset asset, string[] terms )
	{
		var searchable = $"{asset.DisplayName} {asset.Name} {asset.Category} {asset.PackDisplayName} {string.Join( ' ', asset.Tags.Select( tag => tag.DisplayName ) )}".ToLowerInvariant();
		var words = Tokenize( searchable );
		var total = 0;
		foreach ( var term in terms )
		{
			if ( searchable.Contains( term, StringComparison.Ordinal ) )
			{
				total += searchable.StartsWith( term, StringComparison.Ordinal ) ? 120 : 90;
				continue;
			}

			var best = words.Select( word => WordScore( word, term ) ).DefaultIfEmpty( -1 ).Max();
			if ( best < 0 )
				return -1;
			total += best;
		}
		return total;
	}

	private static int WordScore( string word, string term )
	{
		if ( word.StartsWith( term, StringComparison.Ordinal ) )
			return 80;
		if ( IsAdjacentTransposition( word, term ) )
			return 72;
		if ( term.Length >= 3 && IsSubsequence( term, word ) )
			return 55 - Math.Min( 20, word.Length - term.Length );
		var distance = EditDistance( word, term );
		var allowed = term.Length >= 6 ? 2 : term.Length >= 4 ? 1 : 0;
		return distance <= allowed ? 50 - distance * 8 : -1;
	}

	private static string[] Tokenize( string value ) =>
		(value ?? "").ToLowerInvariant().Split( [' ', '_', '-', '/', '\\', '.'], StringSplitOptions.RemoveEmptyEntries );

	private static (string[] Terms, string[] Tags) ParseQuery( string query )
	{
		var terms = new List<string>();
		var tags = new List<string>();
		foreach ( var token in (query ?? "").Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( token.StartsWith( "tag:", StringComparison.OrdinalIgnoreCase ) )
			{
				var tag = token[4..].Trim();
				if ( tag.Length > 0 )
					tags.Add( tag );
				continue;
			}
			terms.AddRange( Tokenize( token ) );
		}
		return (terms.ToArray(), tags.ToArray());
	}

	private static bool HasTags( SyntySourceAsset asset, string[] requested ) =>
		requested.All( query => asset.Tags.Any( tag => SyntyAssetTags.Matches( tag, query ) ) );

	private static bool IsSubsequence( string needle, string haystack )
	{
		var index = 0;
		foreach ( var character in haystack )
			if ( index < needle.Length && character == needle[index] )
				index++;
		return index == needle.Length;
	}

	private static bool IsAdjacentTransposition( string word, string term )
	{
		if ( word.Length != term.Length )
			return false;
		var differences = Enumerable.Range( 0, word.Length ).Where( index => word[index] != term[index] ).ToArray();
		return differences.Length == 2
			&& differences[1] == differences[0] + 1
			&& word[differences[0]] == term[differences[1]]
			&& word[differences[1]] == term[differences[0]];
	}

	private static int EditDistance( string left, string right )
	{
		var previous = Enumerable.Range( 0, right.Length + 1 ).ToArray();
		for ( var row = 1; row <= left.Length; row++ )
		{
			var current = new int[right.Length + 1];
			current[0] = row;
			for ( var column = 1; column <= right.Length; column++ )
				current[column] = Math.Min(
					Math.Min( current[column - 1] + 1, previous[column] + 1 ),
					previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1) );
			previous = current;
		}
		return previous[right.Length];
	}
}
