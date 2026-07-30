using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Tools.SyntyBrowser;

public static class SyntyModelDocument
{
	private static readonly Regex RenderMeshListRegex = new(
		"(?m)^(?<indent>[ \\t]*)\\{\\r?\\n(?<propertyIndent>[ \\t]*)_class = \"RenderMeshList\"",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );
	private static readonly Regex ImportScaleRegex = new(
		"(?m)^(?<prefix>[ \\t]*import_scale[ \\t]*=[ \\t]*)[+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][+-]?\\d+)?",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );

	public static string Create(
		string fbxAssetPath,
		IReadOnlyList<string> sourceMaterialReferences,
		IReadOnlyList<string> materialTargets,
		float importScale,
		bool addRenderHullCollision = true,
		string fallbackMaterial = null )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( fbxAssetPath );
		ArgumentNullException.ThrowIfNull( sourceMaterialReferences );
		ArgumentNullException.ThrowIfNull( materialTargets );
		var template = $$"""
			<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->
			{
				rootNode =
				{
					_class = "RootNode"
					children =
					[
						{
							_class = "MaterialGroupList"
							children =
							[
								{
									_class = "DefaultMaterialGroup"
									remaps = []
									use_global_default = true
									global_default_material = "{{Escape( string.IsNullOrWhiteSpace( fallbackMaterial ) ? "materials/default.vmat" : fallbackMaterial )}}"
								},
							]
						},
						{
							_class = "RenderMeshList"
							children =
							[
								{
									_class = "RenderMeshFile"
									filename = "{{Escape( fbxAssetPath )}}"
									import_translation = [ 0.0, 0.0, 0.0 ]
									import_rotation = [ 0.0, 0.0, 0.0 ]
									import_scale = 1.0
									align_origin_x_type = "None"
									align_origin_y_type = "None"
									align_origin_z_type = "None"
									parent_bone = ""
									import_filter =
									{
										exclude_by_default = false
										exception_list = []
									}
								},
							]
						},
					]
					model_archetype = ""
					primary_associated_entity = ""
					anim_graph_name = ""
					base_model_name = ""
				}
			}
			""";
		return Configure( template + Environment.NewLine, sourceMaterialReferences, materialTargets, addRenderHullCollision, importScale );
	}

	public static string[] AlignMaterialTargets(
		IReadOnlyList<string> sourceMaterialReferences,
		IReadOnlyList<string> declaredSlotNames,
		IReadOnlyList<string> declaredTargets )
	{
		ArgumentNullException.ThrowIfNull( sourceMaterialReferences );
		ArgumentNullException.ThrowIfNull( declaredSlotNames );
		ArgumentNullException.ThrowIfNull( declaredTargets );
		if ( declaredSlotNames.Count != declaredTargets.Count )
			throw new InvalidDataException( "Declared material slot names and targets must have equal counts." );
		if ( sourceMaterialReferences.Count == 0 || declaredTargets.Count == 0 )
			return [];

		return sourceMaterialReferences.Select( (reference, index) =>
		{
			var normalizedReference = NormalizeMaterialName( reference );
			for ( var slotIndex = 0; slotIndex < declaredSlotNames.Count; slotIndex++ )
			{
				if ( string.Equals( NormalizeMaterialName( declaredSlotNames[slotIndex] ), normalizedReference, StringComparison.OrdinalIgnoreCase ) )
					return declaredTargets[slotIndex];
			}
			return declaredTargets[Math.Min( index, declaredTargets.Count - 1 )];
		} ).ToArray();
	}

	public static string Configure(
		string document,
		IReadOnlyList<string> sourceMaterialReferences,
		IReadOnlyList<string> materialTargets,
		bool addRenderHullCollision,
		float importScale = 1.0f )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( document );
		ArgumentNullException.ThrowIfNull( sourceMaterialReferences );
		ArgumentNullException.ThrowIfNull( materialTargets );
		if ( sourceMaterialReferences.Count != materialTargets.Count )
		{
			throw new InvalidDataException(
				$"FBX exposes {sourceMaterialReferences.Count} material slot(s), but the authoritative material list assigns {materialTargets.Count} distinct slot(s)." );
		}
		if ( !float.IsFinite( importScale ) || importScale <= 0.0f )
			throw new ArgumentOutOfRangeException( nameof(importScale), "Import scale must be finite and positive." );

		var configured = document;
		if ( sourceMaterialReferences.Count > 0 )
		{
			var remaps = sourceMaterialReferences
				.Select( (source, index) => new FbxMaterialRemap( source, materialTargets[index] ) )
				.ToArray();
			configured = FbxModelMaterialDocument.SetMaterialRemaps( configured, remaps );
		}
		if ( !ImportScaleRegex.IsMatch( configured ) )
			throw new InvalidDataException( "The generated ModelDoc does not contain an import_scale property." );
		configured = ImportScaleRegex.Replace(
			configured,
			match => match.Groups["prefix"].Value + importScale.ToString( "0.########", System.Globalization.CultureInfo.InvariantCulture ) );

		if ( addRenderHullCollision && !configured.Contains( "_class = \"PhysicsShapeList\"", StringComparison.Ordinal ) )
			configured = AddRenderHullCollision( configured );
		return configured;
	}

	private static string AddRenderHullCollision( string document )
	{
		var renderMesh = RenderMeshListRegex.Match( document );
		if ( !renderMesh.Success )
			throw new InvalidDataException( "The generated ModelDoc does not contain a RenderMeshList node." );

		var indent = renderMesh.Groups["indent"].Value;
		var propertyIndent = renderMesh.Groups["propertyIndent"].Value;
		var itemIndent = $"{propertyIndent}\t";
		var itemPropertyIndent = $"{itemIndent}\t";
		var collision = new StringBuilder()
			.Append( indent ).AppendLine( "{" )
			.Append( propertyIndent ).AppendLine( "_class = \"PhysicsShapeList\"" )
			.Append( propertyIndent ).AppendLine( "children =" )
			.Append( propertyIndent ).AppendLine( "[" )
			.Append( itemIndent ).AppendLine( "{" )
			.Append( itemPropertyIndent ).AppendLine( "_class = \"PhysicsHullFromRender\"" )
			.Append( itemPropertyIndent ).AppendLine( "parent_bone = \"\"" )
			.Append( itemPropertyIndent ).AppendLine( "surface_prop = \"default\"" )
			.Append( itemPropertyIndent ).AppendLine( "collision_prop = \"default\"" )
			.Append( itemPropertyIndent ).AppendLine( "faceMergeAngle = 20.0" )
			.Append( itemPropertyIndent ).AppendLine( "maxHullVertices = 32" )
			.Append( itemIndent ).AppendLine( "}," )
			.Append( propertyIndent ).AppendLine( "]" )
			.Append( indent ).AppendLine( "}," )
			.ToString();
		return document.Insert( renderMesh.Index, collision );
	}

	private static string Escape( string value ) =>
		value.Replace( "\\", "\\\\", StringComparison.Ordinal ).Replace( "\"", "\\\"", StringComparison.Ordinal );

	private static string NormalizeMaterialName( string value ) =>
		Path.GetFileNameWithoutExtension( value ?? "" ).Replace( " ", "", StringComparison.Ordinal ).Replace( "_", "", StringComparison.Ordinal );
}
