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
}
