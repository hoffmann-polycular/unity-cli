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



using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Backend for shell tab-completion. Given a partial path string and a
	/// "kind" hint, returns the candidate paths that complete it. Always emits
	/// canonical paths (one per line) — the CLI passes them straight to the
	/// shell completion engine.
	///
	/// Kinds:
	///   "scene"      → hierarchy paths, optionally with ":Component" suffix
	///   "asset"      → project asset paths under Assets/ and Packages/
	///   "tag"        → registered tag names (TagManager)
	///   "layer"      → layer names (TagManager)
	///   "scene-or-root" → like scene, but also offers leading "/" forms
	///                     for cp/mv destination position
	/// </summary>
	[UnityCliTool(Name = "complete_path",
		Description = "Internal: shell tab-completion candidates for a partial path.")]
	public static class CompletePath
	{
		public class Parameters
		{
			[ToolParameter("Partial path being completed (may be empty).")]
			public string Prefix { get; set; }

			[ToolParameter("Kind: scene, asset, tag, layer, scene-or-root.")]
			public string Kind { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var prefix = p.Get("prefix") ?? "";
			var kind = (p.Get("kind") ?? "scene").ToLowerInvariant();

			var candidates = new List<string>();
			switch (kind)
			{
				case "scene":
					CompleteScenePath(prefix, candidates, allowSceneRootForm: false);
					break;
				case "scene-or-root":
					CompleteScenePath(prefix, candidates, allowSceneRootForm: true);
					break;
				case "asset":
					CompleteAssetPath(prefix, candidates);
					break;
				case "tag":
					CompleteTags(prefix, candidates);
					break;
				case "layer":
					CompleteLayers(prefix, candidates);
					break;
				default:
					return new ErrorResponse($"Unknown completion kind '{kind}'.");
			}

			return new SuccessResponse("", string.Join("\n", candidates));
		}

		// --- scene hierarchy ---

		private static void CompleteScenePath(string prefix, List<string> outList, bool allowSceneRootForm)
		{
			// Scene-root anchor form: "/" or "/Name"
			if (allowSceneRootForm && prefix.StartsWith("/"))
			{
				var afterSlash = prefix.Substring(1);
				if (afterSlash.Contains("/")) return; // grammar rejects deeper paths after leading /
				foreach (var r in GetAllSceneRoots())
				{
					if (string.IsNullOrEmpty(afterSlash) || r.name.StartsWith(afterSlash))
						outList.Add("/" + r.name);
				}
				if (string.IsNullOrEmpty(afterSlash))
					outList.Add("/"); // bare scene-root form, "keep source name"
				return;
			}

			// Component-segment completion: "Path:Partial"
			var colon = prefix.LastIndexOf(':');
			var lastSlash = prefix.LastIndexOf('/');
			if (colon > lastSlash) // colon belongs to current segment
			{
				var objPath = prefix.Substring(0, colon);
				var partial = prefix.Substring(colon + 1);
				CompleteComponentSegment(objPath, partial, outList);
				return;
			}

			// Plain hierarchy: "Parent/Partial"
			string parentPath;
			string namePartial;
			if (lastSlash < 0)
			{
				parentPath = "";
				namePartial = prefix;
			}
			else
			{
				parentPath = prefix.Substring(0, lastSlash);
				namePartial = prefix.Substring(lastSlash + 1);
			}

			IEnumerable<GameObject> candidates;
			if (string.IsNullOrEmpty(parentPath))
			{
				candidates = GetAllSceneRoots();
			}
			else
			{
				var parsed = PathParser.Parse(parentPath);
				if (!parsed.IsSuccess) return;
				var parentRes = PathResolver.ResolveGameObject(parsed.Value);
				if (!parentRes.IsSuccess) return;
				candidates = PathResolver.GetImmediateChildren(parentRes.Value);
			}

			// Prefix the completed name with exactly what the user typed up to and
			// including the last '/'. This preserves a leading '/' for absolute
			// paths (prefix "/Sce" → "/SceneSetup", not the bare "SceneSetup"
			// that parentPath would yield, since parentPath is "" at root level).
			var emitPrefix = lastSlash < 0 ? "" : prefix.Substring(0, lastSlash + 1);

			var seenNames = new HashSet<string>();
			foreach (var go in candidates)
			{
				if (go == null) continue;
				if (!string.IsNullOrEmpty(namePartial) && !go.name.StartsWith(namePartial)) continue;
				var emit = emitPrefix + go.name;
				// Append "/" when the object has children so a Tab on an exact
				// name descends into the hierarchy instead of completing to
				// itself (empty suffix = no-op). Mirrors asset-folder completion;
				// the CLI's shell scripts already suppress the trailing space
				// after "/".
				if (go.transform.childCount > 0)
					emit += "/";
				// Avoid emitting duplicate names (caller can disambiguate with [n] later).
				if (seenNames.Add(emit))
					outList.Add(emit);
			}
		}

		private static void CompleteComponentSegment(string objPath, string partial, List<string> outList)
		{
			GameObject go;
			if (string.IsNullOrEmpty(objPath)) return;
			var parsed = PathParser.Parse(objPath);
			if (!parsed.IsSuccess) return;
			var res = PathResolver.ResolveGameObject(parsed.Value);
			if (!res.IsSuccess) return;
			go = res.Value;

			var seen = new HashSet<string>();
			foreach (var c in go.GetComponents<Component>())
			{
				if (c == null) continue;
				var typeName = c.GetType().Name;
				if (!string.IsNullOrEmpty(partial) && !typeName.StartsWith(partial)) continue;
				if (seen.Add(typeName))
					outList.Add(objPath + ":" + typeName);
			}
		}

		private static List<GameObject> GetAllSceneRoots()
		{
			// Use PathResolver semantics so prefab-stage mode is respected.
			return PathResolver.GetSceneRoots();
		}

		// --- assets ---

		private static void CompleteAssetPath(string prefix, List<string> outList)
		{
			// Walk directories under the prefix's parent.
			if (string.IsNullOrEmpty(prefix))
			{
				outList.Add("Assets/");
				outList.Add("Packages/");
				return;
			}

			var lastSlash = prefix.LastIndexOf('/');
			string folder, partial;
			if (lastSlash < 0)
			{
				folder = "";
				partial = prefix;
			}
			else
			{
				folder = prefix.Substring(0, lastSlash);
				partial = prefix.Substring(lastSlash + 1);
			}

			// Top-level "As..." or "Pa..." → suggest the roots
			if (string.IsNullOrEmpty(folder))
			{
				if ("Assets".StartsWith(partial)) outList.Add("Assets/");
				if ("Packages".StartsWith(partial)) outList.Add("Packages/");
				return;
			}

			// Use AssetDatabase to enumerate sub-folders and asset files.
			if (!AssetDatabase.IsValidFolder(folder)) return;

			foreach (var sub in AssetDatabase.GetSubFolders(folder))
			{
				var name = sub.Substring(folder.Length + 1);
				if (string.IsNullOrEmpty(partial) || name.StartsWith(partial))
					outList.Add(sub + "/");
			}

			// Files in this folder.
			var guids = AssetDatabase.FindAssets("", new[] { folder });
			foreach (var guid in guids)
			{
				var path = AssetDatabase.GUIDToAssetPath(guid);
				// Only direct children of this folder, not nested.
				var parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
				if (parent != folder) continue;
				var name = System.IO.Path.GetFileName(path);
				if (string.IsNullOrEmpty(partial) || name.StartsWith(partial))
					outList.Add(path);
			}
		}

		// --- tags / layers ---

		private static void CompleteTags(string prefix, List<string> outList)
		{
			foreach (var tag in UnityEditorInternal.InternalEditorUtility.tags)
			{
				if (string.IsNullOrEmpty(prefix) || tag.StartsWith(prefix))
					outList.Add(tag);
			}
		}

		private static void CompleteLayers(string prefix, List<string> outList)
		{
			foreach (var layer in UnityEditorInternal.InternalEditorUtility.layers)
			{
				if (string.IsNullOrEmpty(prefix) || layer.StartsWith(prefix))
					outList.Add(layer);
			}
		}
	}
}
