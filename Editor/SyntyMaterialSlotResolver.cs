using System;

namespace Editor.Tools.SyntyBrowser;

public sealed record SyntyMaterialSlotOverride
{
	public string Shader { get; set; }
	public string OutputName { get; set; }
	public string TextureHint { get; set; }
	public Dictionary<string, string> Parameters { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

public sealed record SyntyResolvedMaterialSlot
{
	public required string MeshName { get; init; }
	public required int SlotOrdinal { get; init; }
	public required SyntyMaterialSlot Source { get; init; }
	public required string OutputName { get; init; }
	public SyntyMaterialSlotOverride Override { get; init; }
	public string BindingKey => $"{MeshName}[{SlotOrdinal}]";
}

public static class SyntyMaterialSlotResolver
{
	public static SyntyResolvedMaterialSlot[] Resolve(
		SyntySourceAsset source,
		IReadOnlyDictionary<string, SyntyMaterialSlotOverride> overrides )
	{
		ArgumentNullException.ThrowIfNull( source );
		overrides ??= new Dictionary<string, SyntyMaterialSlotOverride>( StringComparer.OrdinalIgnoreCase );

		return source.Meshes.SelectMany( mesh => mesh.Materials.Select( (slot, ordinal) =>
		{
			var key = $"{source.Id}/{mesh.Name}[{ordinal}]";
			overrides.TryGetValue( key, out var slotOverride );
			return new SyntyResolvedMaterialSlot
			{
				MeshName = mesh.Name,
				SlotOrdinal = ordinal,
				Source = slot,
				Override = slotOverride,
				OutputName = string.IsNullOrWhiteSpace( slotOverride?.OutputName )
					? slot.Name
					: slotOverride.OutputName
			};
		} ) ).ToArray();
	}
}
