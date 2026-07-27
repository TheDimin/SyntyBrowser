using System;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyPreviewMaterialBinding
{
	public required string MeshName { get; init; }
	public required string SlotName { get; init; }
	public int SlotOrdinal { get; init; }
	public string TextureHint { get; init; }
	public bool IsAuthoritative { get; init; }
}

public static class SyntyPreviewTextureResolver
{
	public static SyntyPreviewMaterialBinding[] Bindings(
		SyntySourceAsset source,
		IEnumerable<string> sourceMaterialNames = null )
	{
		ArgumentNullException.ThrowIfNull( source );
		var authoritative = source.Meshes
			.SelectMany( mesh => mesh.Materials.Select( (slot, slotOrdinal) => new SyntyPreviewMaterialBinding
			{
				MeshName = mesh.Name,
				SlotName = slot.Name,
				SlotOrdinal = slotOrdinal,
				TextureHint = slot.TextureHint,
				IsAuthoritative = true
			} ) )
			.ToArray();
		if ( authoritative.Length > 0 )
			return authoritative;

		return (sourceMaterialNames ?? [])
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.Select( (name, slotOrdinal) => new SyntyPreviewMaterialBinding
			{
				MeshName = source.Name,
				SlotName = name,
				SlotOrdinal = slotOrdinal,
				TextureHint = name,
				IsAuthoritative = false
			} )
			.ToArray();
	}

	public static string[] CandidateHints( SyntySourceAsset source, IEnumerable<string> sourceMaterialNames )
	{
		return Bindings( source, sourceMaterialNames )
			.SelectMany( binding => new[] { binding.TextureHint, binding.SlotName } )
			.Where( hint => !string.IsNullOrWhiteSpace( hint ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}
}
