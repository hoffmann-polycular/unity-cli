// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// 
// COMMERCIAL LICENSE NOTICE:
// If you wish to use this code inside a non-GPL, proprietary software product, 
// you must instead acquire a commercial license from the copyright holder.
// 
// Contact: info@polycular.com | Website: https://www.polycular.com/



using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliConnector
{
	/// <summary>
	/// Resolves <see cref="ParsedPath"/> values against live Unity state.
	///
	/// For the first cut, only scene GameObject resolution is implemented —
	/// enough to power <c>ls</c>. Component / property / asset-internal
	/// resolution will be layered in for <c>inspect</c>, <c>get</c>, <c>set</c>,
	/// and the prefab commands.
	/// </summary>
	public static class PathResolver
	{
		/// <summary>
		/// Union of root GameObjects across all loaded scenes, in scene-order
		/// then scene-root-order. Matches what the Hierarchy window shows at
		/// the top level.
		/// </summary>
		public static List<GameObject> GetSceneRoots()
		{
			var roots = new List<GameObject>();
			for (var i = 0; i < SceneManager.sceneCount; i++)
			{
				var scene = SceneManager.GetSceneAt(i);
				if (!scene.IsValid() || !scene.isLoaded) continue;
				roots.AddRange(scene.GetRootGameObjects());
			}
			return roots;
		}
		/// <summary>
		/// Returns the root GameObject of the currently open prefab stage, if any.
		/// </summary>
		/// <returns>The root GameObject of the open prefab stage, or an empty list if no prefab stage is open.</returns>
		public static List<GameObject> GetPrefabRoots()
		{
			// Check if a prefab stage is open
			var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
			if (prefabStage != null && prefabStage.prefabContentsRoot != null)
			{
				// Only the root of the open prefab is shown in the Hierarchy
				return new List<GameObject> { prefabStage.prefabContentsRoot };
			}
			return new List<GameObject>();
		}

		/// <summary>
		/// Immediate children of <paramref name="parent"/>, in Hierarchy order.
		/// </summary>
		public static List<GameObject> GetImmediateChildren(GameObject parent)
		{
			if (parent == null) return new List<GameObject>();
			var count = parent.transform.childCount;
			var result = new List<GameObject>(count);
			for (var i = 0; i < count; i++)
				result.Add(parent.transform.GetChild(i).gameObject);
			return result;
		}

		/// <summary>
		/// Resolves a parsed path to exactly one GameObject. Returns an error
		/// with candidate canonical paths when the input is ambiguous.
		/// </summary>
		public static Result<GameObject> ResolveGameObject(ParsedPath parsed)
		{
			if (parsed == null) return Result<GameObject>.Error("Path is null.");

			switch (parsed.Kind)
			{
				case PathKind.InstanceId:
					var obj = EditorUtility.InstanceIDToObject(parsed.InstanceId);
					if (obj == null)
						return Result<GameObject>.Error($"No object with instance ID #{parsed.InstanceId}.");
					if (obj is GameObject go) return Result<GameObject>.Success(go);
					if (obj is Component c) return Result<GameObject>.Success(c.gameObject);
					return Result<GameObject>.Error(
						$"Instance ID #{parsed.InstanceId} resolves to a {obj.GetType().Name}, not a GameObject.");

				case PathKind.Scene:
					return ResolveSceneObject(parsed);

				case PathKind.Asset:
					return Result<GameObject>.Error(
						"Asset-backed GameObject resolution is not yet implemented.");

				default:
					return Result<GameObject>.Error("Unknown path kind.");
			}
		}

		/// <summary>
		/// Builds the canonical path for a GameObject. Indices are appended
		/// only when a name is actually duplicated at that level.
		/// </summary>
		public static string GetCanonicalPath(GameObject go)
		{
			if (go == null) return "";
			var stack = new Stack<string>();
			var t = go.transform;
			while (t != null)
			{
				stack.Push(BuildSegment(t.gameObject));
				t = t.parent;
			}
			return string.Join("/", stack);
		}

		/// <summary>
		/// Name-with-optional-index for the object, relative to its sibling
		/// set. Adds <c>[n]</c> only when the name is non-unique.
		/// </summary>
		public static string GetSegmentName(GameObject go)
		{
			return BuildSegment(go);
		}

		// ---- internals ----

		private static Result<GameObject> ResolveSceneObject(ParsedPath parsed)
		{
			if (parsed.Segments == null || parsed.Segments.Count == 0)
				return Result<GameObject>.Error("Scene path has no hierarchy segments.");

			// First segment matches against scene roots.
			var candidates = FilterByName(GetSceneRoots(), parsed.Segments[0]);
			if (candidates.Count == 0)
				return Result<GameObject>.Error($"No root object matching '{parsed.Segments[0]}'.");
			if (candidates.Count > 1)
				return AmbiguityError(candidates, parsed, 0);

			var current = candidates[0];

			for (var i = 1; i < parsed.Segments.Count; i++)
			{
				var seg = parsed.Segments[i];
				var children = GetImmediateChildren(current);
				candidates = FilterByName(children, seg);
				if (candidates.Count == 0)
					return Result<GameObject>.Error(
						$"No child matching '{seg}' under '{GetCanonicalPath(current)}'.");
				if (candidates.Count > 1)
					return AmbiguityError(candidates, parsed, i);
				current = candidates[0];
			}

			return Result<GameObject>.Success(current);
		}

		private static List<GameObject> FilterByName(IEnumerable<GameObject> candidates, PathSegment seg)
		{
			var sameName = new List<GameObject>();
			foreach (var c in candidates)
				if (c != null && c.name == seg.Name)
					sameName.Add(c);

			if (!seg.Index.HasValue) return sameName;
			if (seg.Index.Value < 0 || seg.Index.Value >= sameName.Count)
				return new List<GameObject>();
			return new List<GameObject> { sameName[seg.Index.Value] };
		}

		private static Result<GameObject> AmbiguityError(
			List<GameObject> matches, ParsedPath parsed, int segmentIdx)
		{
			var sb = new StringBuilder();
			sb.Append($"Ambiguous path at segment {segmentIdx} ('{parsed.Segments[segmentIdx]}'). Candidates:");
			foreach (var m in matches)
				sb.Append('\n').Append("  ").Append(GetCanonicalPath(m));
			return Result<GameObject>.Error(sb.ToString());
		}

		private static string BuildSegment(GameObject go)
		{
			List<GameObject> siblings = go.transform.parent == null
				? GetSceneRoots()
				: GetImmediateChildren(go.transform.parent.gameObject);

			var sameName = new List<GameObject>();
			foreach (var s in siblings)
				if (s.name == go.name) sameName.Add(s);

			if (sameName.Count <= 1) return go.name;
			return $"{go.name}[{sameName.IndexOf(go)}]";
		}
	}
}
