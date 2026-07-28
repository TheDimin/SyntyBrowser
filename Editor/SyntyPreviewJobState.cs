using System;
using System.IO;

namespace Editor.Tools.SyntyBrowser;

public enum SyntyPreviewJobStatus
{
	Pending,
	Rendering,
	Completed,
	Skipped,
	Failed
}

public sealed record SyntyPreviewJobState
{
	public required string AssetId { get; init; }
	public required SyntyPreviewJobStatus Status { get; init; }
	public int Attempts { get; init; }
	public string Error { get; init; }
	public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class SyntyPreviewRetryPolicy
{
	public static bool CanAutomaticallyRetry( SyntyPreviewJobState state, int maximumAttempts )
	{
		if ( maximumAttempts <= 0 )
			throw new ArgumentOutOfRangeException( nameof( maximumAttempts ) );
		return state is null || state.Status is SyntyPreviewJobStatus.Failed && state.Attempts < maximumAttempts;
	}
}

public static class SyntyPreviewVisibility
{
	public static bool IsVisibleOrNear(
		float cardTop,
		float cardBottom,
		float viewportTop,
		float viewportBottom,
		float oneRowHeight ) =>
		cardBottom >= viewportTop - oneRowHeight && cardTop <= viewportBottom + oneRowHeight;
}

public static class SyntyPreviewEligibility
{
	public static bool CanGenerate( SyntySourceAsset source ) =>
		source is not null
		&& source.CanImport
		&& !SyntySourceCatalog.IsAuxiliaryModel( source.Name )
		&& !SyntySourceCatalog.IsAuxiliaryModel( Path.GetFileNameWithoutExtension( source.SourceFbxPath ) );
}
