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
	/// v3 path-contract semantics:
	///   - Bare paths and "./..." / "../..." anchor at the current Editor
	///     <see cref="Selection"/>. Multi-selection causes fan-out — every
	///     resolution call returns a list, never a single value.
	///   - "/..." anchors at the Hierarchy root (the union of all loaded
	///     scene roots, or the prefab stage root when one is open).
	///   - Asset, Packages, ProjectSettings, and InstanceId paths are
	///     absolute and never fan out from selection.
	///
	/// The list-returning <see cref="ResolveTargets"/> is the canonical
	/// entry point. The legacy single-target <see cref="ResolveGameObject"/>
	/// is preserved for tools that genuinely require N=1 (it errors on
	/// fan-out).
	/// </summary>
	public static class PathResolver
	{
		// ---- Hierarchy root enumeration ----

		/// <summary>
		/// Returns the GameObjects that "/" expands to: the union of every
		/// loaded scene's roots in scene-then-root order, OR — when a prefab
		/// stage is open — only the prefab stage's root. Mirrors what the
		/// Hierarchy window shows.
		/// </summary>
		public static List<GameObject> GetSceneRoots()
		{
			var prefabRoots = GetPrefabRoots();
			if (prefabRoots.Count > 0) return prefabRoots;

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
		/// Returns the root GameObject of the currently open prefab stage,
		/// if any (otherwise an empty list).
		/// </summary>
		public static List<GameObject> GetPrefabRoots()
		{
			var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
			if (prefabStage != null && prefabStage.prefabContentsRoot != null)
				return new List<GameObject> { prefabStage.prefabContentsRoot };
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

		// ---- v3 selection-aware fan-out resolver ----

		/// <summary>
		/// Snapshot of the current Editor selection as GameObjects. Components
		/// in the selection are coerced to their owning GameObject. Order
		/// matches <c>Selection.objects</c> order (which matches Hierarchy
		/// top-to-bottom). Captured up front so concurrent selection changes
		/// during a fan-out don't shift the target set mid-run.
		/// </summary>
		public static List<GameObject> SelectionSnapshot()
		{
			var sel = Selection.objects;
			var result = new List<GameObject>(sel.Length);
			foreach (var obj in sel)
			{
				switch (obj)
				{
					case GameObject go:
						result.Add(go);
						break;
					case Component c when c != null:
						result.Add(c.gameObject);
						break;
				}
			}
			return result;
		}

		/// <summary>
		/// Walks <paramref name="ancestors"/> levels up from <paramref name="go"/>.
		/// Returns null if the walk runs off the top of the hierarchy.
		/// </summary>
		private static GameObject WalkUp(GameObject go, int ancestors)
		{
			if (go == null) return null;
			var t = go.transform;
			for (var i = 0; i < ancestors; i++)
			{
				if (t.parent == null) return null;
				t = t.parent;
			}
			return t.gameObject;
		}

		/// <summary>
		/// Resolves a parsed path to the full set of matching GameObjects.
		/// Selection-anchored paths produce one entry per selected object
		/// (after walking up <see cref="ParsedPath.ParentJumps"/> levels and
		/// then descending through the segments). Hierarchy- and InstanceId-
		/// anchored paths produce a single entry. Empty selection on a
		/// selection-anchored path is reported as <see cref="ErrorKind.NotFound"/>.
		///
		/// Asset and ProjectSettings paths cannot resolve to GameObjects via
		/// this entry point — use the asset/settings backends directly.
		/// </summary>
		public static Result<List<GameObject>> ResolveTargets(ParsedPath parsed)
		{
			if (parsed == null) return Result<List<GameObject>>.Error("Path is null.");

			switch (parsed.Kind)
			{
				case PathKind.InstanceId:
					return ResolveInstanceIdAsList(parsed);
				case PathKind.Scene:
					return ResolveSceneTargets(parsed);
				case PathKind.Asset:
					return ResolveAssetTargets(parsed);
				case PathKind.ProjectSettings:
					return Result<List<GameObject>>.Error(
						"ProjectSettings paths do not resolve to GameObjects.", ErrorKind.Usage);
				default:
					return Result<List<GameObject>>.Error("Unknown path kind.");
			}
		}

		private static Result<List<GameObject>> ResolveInstanceIdAsList(ParsedPath parsed)
		{
			var obj = EditorUtility.InstanceIDToObject(parsed.InstanceId);
			if (obj == null)
				return Result<List<GameObject>>.Error($"No object with instance ID #{parsed.InstanceId}.", ErrorKind.NotFound);
			GameObject go = null;
			if (obj is GameObject gameObject) go = gameObject;
			else if (obj is Component c) go = c.gameObject;
			if (go == null)
				return Result<List<GameObject>>.Error(
					$"Instance ID #{parsed.InstanceId} resolves to a {obj.GetType().Name}, not a GameObject.");
			return Result<List<GameObject>>.Success(new List<GameObject> { go });
		}

		private static Result<List<GameObject>> ResolveSceneTargets(ParsedPath parsed)
		{
			// Hierarchy-anchored: walk down from scene/prefab roots. Bare "/"
			// (no segments) resolves to the scene roots themselves.
			if (parsed.Anchor == PathAnchor.Hierarchy)
			{
				var hierarchyRoots = GetSceneRoots();
				if (parsed.Segments == null || parsed.Segments.Count == 0)
				{
					if (hierarchyRoots.Count == 0)
						return Result<List<GameObject>>.Error(
							"No loaded scenes (no roots under '/').", ErrorKind.NotFound);
					return Result<List<GameObject>>.Success(hierarchyRoots);
				}
				return WalkDown(hierarchyRoots, parsed.Segments);
			}

			// Selection-anchored.
			var selection = SelectionSnapshot();
			if (selection.Count == 0)
			{
				// v3 §4.4: empty selection + relative path → treat as if anchored
				// at the Hierarchy root. "Items", "./Items" and "/Items" all mean
				// the same thing when nothing is selected. Walking up ("..") from
				// an empty selection has no meaning and is rejected.
				if (parsed.ParentJumps > 0)
					return Result<List<GameObject>>.Error(
						$"Cannot walk {parsed.ParentJumps} parent step(s) from an empty selection. " +
						"Hint: select a GameObject first.",
						ErrorKind.NotFound);
				var sceneRoots = GetSceneRoots();
				if (parsed.Segments == null || parsed.Segments.Count == 0)
				{
					if (sceneRoots.Count == 0)
						return Result<List<GameObject>>.Error(
							"No selection and no loaded scene roots.", ErrorKind.NotFound);
					return Result<List<GameObject>>.Success(sceneRoots);
				}
				return WalkDown(sceneRoots, parsed.Segments);
			}

			// Walk each selected object up by ParentJumps, dedupe, then
			// descend through the segments under each surviving root.
			var roots = new List<GameObject>(selection.Count);
			var seen = new HashSet<int>();
			foreach (var sel in selection)
			{
				var rooted = parsed.ParentJumps == 0 ? sel : WalkUp(sel, parsed.ParentJumps);
				if (rooted == null) continue;
				if (!seen.Add(rooted.GetInstanceID())) continue;
				roots.Add(rooted);
			}
			if (roots.Count == 0)
				return Result<List<GameObject>>.Error(
					$"Walking {parsed.ParentJumps} parent step(s) from the selection runs past the Hierarchy root.",
					ErrorKind.NotFound);

			// No segments → the rooted objects ARE the targets ("." / ".." / "../..").
			if (parsed.Segments == null || parsed.Segments.Count == 0)
				return Result<List<GameObject>>.Success(roots);

			// Segments present: the selected objects are the PARENTS.
			// Expand each root to its immediate children — those are the candidates
			// that WalkDown will match the first segment against.
			var startCandidates = new List<GameObject>();
			foreach (var root in roots)
				startCandidates.AddRange(GetImmediateChildren(root));
			if (startCandidates.Count == 0)
				return Result<List<GameObject>>.Error(
					"The selected object(s) have no children to search.", ErrorKind.NotFound);

			return WalkDown(startCandidates, parsed.Segments);
		}

		/// <summary>
		/// Walks a list of starting roots through the given segments, fanning
		/// out at each level when a name has duplicate matches and no index
		/// was provided. Returns the deduplicated leaf set, in input-root
		/// order. An empty result is reported as <see cref="ErrorKind.NotFound"/>.
		/// </summary>
		private static Result<List<GameObject>> WalkDown(List<GameObject> roots, List<PathSegment> segments)
		{
			// First segment matches against the supplied roots.
			var frontier = new List<GameObject>();
			foreach (var r in roots)
				if (r != null && r.name == segments[0].Name)
					frontier.Add(r);
			frontier = ApplyIndex(frontier, segments[0]);
			if (frontier.Count == 0)
				return Result<List<GameObject>>.Error(
					$"No object matching '{segments[0]}' under the anchor.", ErrorKind.NotFound);

			for (var i = 1; i < segments.Count; i++)
			{
				var seg = segments[i];
				var next = new List<GameObject>();
				foreach (var parent in frontier)
				{
					var sameName = new List<GameObject>();
					var children = GetImmediateChildren(parent);
					foreach (var c in children)
						if (c != null && c.name == seg.Name)
							sameName.Add(c);
					next.AddRange(ApplyIndex(sameName, seg));
				}
				if (next.Count == 0)
					return Result<List<GameObject>>.Error(
						$"No descendants matching '{seg}' beneath any candidate at depth {i}.", ErrorKind.NotFound);
				frontier = next;
			}

			// Dedup while preserving order.
			var seen = new HashSet<int>();
			var result = new List<GameObject>(frontier.Count);
			foreach (var g in frontier)
			{
				if (g == null) continue;
				if (seen.Add(g.GetInstanceID())) result.Add(g);
			}
			return Result<List<GameObject>>.Success(result);
		}

		private static List<GameObject> ApplyIndex(List<GameObject> sameName, PathSegment seg)
		{
			if (!seg.Index.HasValue) return sameName;
			if (seg.Index.Value < 0 || seg.Index.Value >= sameName.Count)
				return new List<GameObject>();
			return new List<GameObject> { sameName[seg.Index.Value] };
		}

		private static Result<List<GameObject>> ResolveAssetTargets(ParsedPath parsed)
		{
			if (string.IsNullOrEmpty(parsed.AssetPath))
				return Result<List<GameObject>>.Error("Asset path is empty.", ErrorKind.Usage);

			var go = AssetDatabase.LoadAssetAtPath<GameObject>(parsed.AssetPath);
			if (go == null)
			{
				// Try generic load for non-prefab assets so we can produce a
				// clearer error than "no GameObject" when the asset exists
				// but isn't a prefab.
				var any = AssetDatabase.LoadMainAssetAtPath(parsed.AssetPath);
				if (any == null)
					return Result<List<GameObject>>.Error(
						$"Asset not found: '{parsed.AssetPath}'.", ErrorKind.NotFound);
				return Result<List<GameObject>>.Error(
					$"Asset '{parsed.AssetPath}' is a {any.GetType().Name}, not a GameObject.", ErrorKind.Usage);
			}

			if (parsed.Segments == null || parsed.Segments.Count == 0)
				return Result<List<GameObject>>.Success(new List<GameObject> { go });

			// Sub-asset "//" segments walk DOWN from the prefab root, not
			// against it: e.g. "Foo.prefab//Hat" means "the Hat child of Foo",
			// not "an asset named Hat" — so the first segment matches against
			// the prefab root's immediate children, just like a scene path
			// after a hierarchy anchor.
			var children = GetImmediateChildren(go);
			if (children.Count == 0)
				return Result<List<GameObject>>.Error(
					$"Asset '{parsed.AssetPath}' has no children to descend into.",
					ErrorKind.NotFound);
			return WalkDown(children, parsed.Segments);
		}

		// ---- Single-target wrapper (legacy) ----

		/// <summary>
		/// Convenience wrapper around <see cref="ResolveTargets"/> that errors
		/// on empty/multi results. Used by tools that genuinely require one
		/// GameObject (e.g. <c>cp</c>, <c>mv</c>, <c>create</c> destination).
		/// </summary>
		public static Result<GameObject> ResolveGameObject(ParsedPath parsed)
		{
			var listRes = ResolveTargets(parsed);
			if (!listRes.IsSuccess) return Result<GameObject>.Error(listRes.ErrorMessage, listRes.ErrorKind);
			var list = listRes.Value;
			if (list.Count == 0)
				return Result<GameObject>.Error("Path resolved to zero objects.", ErrorKind.NotFound);
			if (list.Count > 1)
				return AmbiguityError(list, parsed);
			return Result<GameObject>.Success(list[0]);
		}

		/// <summary>
		/// Alias of <see cref="ResolveTargets"/> retained for tools that
		/// previously called <c>ResolveGameObjectsAll</c> behind a <c>--all</c>
		/// flag. Under v3, fan-out is the default — this is now identical.
		/// </summary>
		public static Result<List<GameObject>> ResolveGameObjectsAll(ParsedPath parsed)
		{
			return ResolveTargets(parsed);
		}

		// ---- canonical paths ----

		/// <summary>
		/// Builds the canonical Hierarchy path for a GameObject. Indices are
		/// appended only when a name is actually duplicated at that level.
		/// Always emits a leading "/" so callers can pipe the result back
		/// into any v3-grammar tool without ambiguity (bare paths are
		/// selection-relative under v3).
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
			return "/" + string.Join("/", stack);
		}

		/// <summary>
		/// Name-with-optional-index for the object, relative to its sibling
		/// set. Adds <c>[n]</c> only when the name is non-unique.
		/// </summary>
		public static string GetSegmentName(GameObject go)
		{
			return BuildSegment(go);
		}

		// ---- component / property resolution (unchanged) ----

		public static Result<Component> ResolveComponent(GameObject go, ComponentRef compRef)
		{
			if (go == null) return Result<Component>.Error("GameObject is null.");
			if (!compRef.IsPresent) return Result<Component>.Error("No component specified.");

			var type = TypeResolver.ResolveComponentType(compRef.TypeName);
			if (type == null)
				return Result<Component>.Error($"Unknown component type: '{compRef.TypeName}'.", ErrorKind.NotFound);

			var comps = go.GetComponents(type);
			if (comps == null || comps.Length == 0)
				return Result<Component>.Error(
					$"No {type.Name} on '{GetCanonicalPath(go)}'.", ErrorKind.NotFound);

			if (compRef.Index.HasValue)
			{
				if (compRef.Index.Value < 0 || compRef.Index.Value >= comps.Length)
					return Result<Component>.Error(
						$"Index [{compRef.Index.Value}] out of range (have {comps.Length} {type.Name}).", ErrorKind.NotFound);
				return Result<Component>.Success(comps[compRef.Index.Value]);
			}

			if (comps.Length > 1)
				return Result<Component>.Error(
					$"Ambiguous component '{type.Name}' on '{GetCanonicalPath(go)}' ({comps.Length} present). Use '{type.Name}[n]'.", ErrorKind.Ambiguous);

			return Result<Component>.Success(comps[0]);
		}

		public static SerializedProperty FindPropertyByUserName(SerializedObject so, string userName)
		{
			if (so == null || string.IsNullOrEmpty(userName)) return null;

			var direct = so.FindProperty(userName);
			if (direct != null) return direct;

			var pascal = "m_" + char.ToUpperInvariant(userName[0]) + userName.Substring(1);
			direct = so.FindProperty(pascal);
			if (direct != null) return direct;

			var it = so.GetIterator();
			var enterChildren = true;
			while (it.NextVisible(enterChildren))
			{
				enterChildren = false;
				if (NameMatches(it.name, userName) || NameMatches(it.displayName, userName))
					return it.Copy();
			}
			return null;
		}

		public static SerializedProperty FindRelativeByUserName(SerializedProperty parent, string userName)
		{
			if (parent == null || string.IsNullOrEmpty(userName)) return null;

			var direct = parent.FindPropertyRelative(userName);
			if (direct != null) return direct;

			var pascal = "m_" + char.ToUpperInvariant(userName[0]) + userName.Substring(1);
			direct = parent.FindPropertyRelative(pascal);
			if (direct != null) return direct;

			var it = parent.Copy();
			var end = parent.GetEndProperty();
			var enterChildren = true;
			while (it.NextVisible(enterChildren) && !SerializedProperty.EqualContents(it, end))
			{
				enterChildren = false;
				if (NameMatches(it.name, userName) || NameMatches(it.displayName, userName))
					return it.Copy();
			}
			return null;
		}

		public static string NormalizeSerializedName(string name)
		{
			if (string.IsNullOrEmpty(name)) return name;
			if (name.Length > 2 && name[0] == 'm' && name[1] == '_')
				return char.ToLowerInvariant(name[2]) + name.Substring(3);
			return name;
		}

		private static bool NameMatches(string serializedName, string userName)
		{
			if (string.IsNullOrEmpty(serializedName)) return false;
			if (string.Equals(serializedName, userName, System.StringComparison.OrdinalIgnoreCase))
				return true;
			var normalized = NormalizeSerializedName(serializedName);
			if (string.Equals(normalized, userName, System.StringComparison.OrdinalIgnoreCase))
				return true;
			if (serializedName.IndexOf(' ') >= 0)
			{
				var collapsed = serializedName.Replace(" ", "");
				if (string.Equals(collapsed, userName, System.StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		// ---- internals ----

		private static Result<GameObject> AmbiguityError(List<GameObject> matches, ParsedPath parsed)
		{
			var sb = new StringBuilder();
			sb.Append($"Path '{parsed.Raw}' resolves to {matches.Count} objects but a single target was required. Candidates:");
			foreach (var m in matches)
				sb.Append('\n').Append("  ").Append(GetCanonicalPath(m));
			return Result<GameObject>.Error(sb.ToString(), ErrorKind.Ambiguous);
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
