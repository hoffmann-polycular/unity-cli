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
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Unified find command. Mode is determined by the first positional
	/// argument:
	///
	///   find                    (no positional)            → scene search
	///   find Assets/...         (asset-database path)      → asset search
	///   find Packages/...       (asset-database path)      → asset search
	///
	/// Scene search: filter loaded GameObjects by name, component, tag, layer,
	/// prefab source, or override state. All filters AND-combine.
	///
	/// Asset search: wrap <see cref="AssetDatabase.FindAssets(string, string[])"/>.
	/// The path positional scopes the search; --type / --label / --name /
	/// --area further filter the results.
	/// </summary>
	[UnityCliTool(Name = "find",
		Description = "Find scene GameObjects, or assets when first arg is an Assets/ or Packages/ path.")]
	public static class Find
	{
		public class Parameters
		{
			[ToolParameter("First positional. Empty = scene search; 'Assets/...' or 'Packages/...' = asset search.")]
			public string Path { get; set; }

			// Scene mode
			[ToolParameter("Scene: name glob (e.g. 'Enemy*').")]
			public string Name { get; set; }

			[ToolParameter("Scene: require a component of this type (may repeat).")]
			public string Component { get; set; }

			[ToolParameter("Scene: exclude objects that have a component of this type (may repeat).")]
			public string Missing { get; set; }

			[ToolParameter("Scene: match only objects with this tag.")]
			public string Tag { get; set; }

			[ToolParameter("Scene: match only objects on this layer (name).")]
			public string Layer { get; set; }

			[ToolParameter("Scene: match only instances of this prefab asset (root match).")]
			public string Prefab { get; set; }

			[ToolParameter("Scene: match only prefab instances with overrides.")]
			public bool HasOverrides { get; set; }

			[ToolParameter("Scene: match only active-in-hierarchy objects.")]
			public bool Active { get; set; }

			[ToolParameter("Scene: match only inactive-in-hierarchy objects.")]
			public bool Inactive { get; set; }

			// Asset mode
			[ToolParameter("Asset: type filter (e.g. Material, Mesh, Prefab, ScriptableObject, Texture2D).")]
			public string Type { get; set; }

			[ToolParameter("Asset: label filter.")]
			public string Label { get; set; }

			[ToolParameter("Asset: search area: all (default), assets, or packages.")]
			public string Area { get; set; }

			[ToolParameter("Output format: human (default), json, plain, null.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;

			// Pick the first positional arg, if any. If it looks like an
			// asset-database path, run an asset search scoped to it.
			// Otherwise, treat it as a scene-hierarchy path that scopes the
			// search to that subtree.
			string firstPositional = null;
			if (args != null && args.Count > 0)
				firstPositional = args[0]?.ToString();

			if (IsAssetPath(firstPositional) || IsAssetPath(p.Get("path")))
			{
				var pathArg = !string.IsNullOrEmpty(p.Get("path"))
					? p.Get("path")
					: firstPositional;
				return DoAssetSearch(p, pathArg);
			}

			return DoSceneSearch(p, @params, firstPositional);
		}

		private static bool IsAssetPath(string s)
		{
			if (string.IsNullOrEmpty(s)) return false;
			return s.StartsWith("Assets/") || s.StartsWith("Packages/")
				|| s == "Assets" || s == "Packages";
		}

		// =================================================================
		// Scene search
		// =================================================================

		private static object DoSceneSearch(ToolParams p, JObject @params, string scopePath)
		{
			var nameGlob = p.Get("name");
			var regex = p.Get("regex");
			var components = GetStringList(@params, "component");
			var missing = GetStringList(@params, "missing");
			var tagFilter = p.Get("tag");
			var layerName = p.Get("layer");
			var prefabAssetPath = p.Get("prefab");
			var wantOverrides = p.GetBool("has_overrides");
			var wantActive = p.GetBool("active");
			var wantInactive = p.GetBool("inactive");
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			if (!string.IsNullOrEmpty(nameGlob) && !string.IsNullOrEmpty(regex))
				return new ErrorResponse("--name and --regex are mutually exclusive.");

			if (wantActive && wantInactive)
				return new ErrorResponse("--active and --inactive are mutually exclusive.");

			var requiredTypes = new List<Type>();
			foreach (var n in components)
			{
				var t = TypeResolver.ResolveComponentType(n);
				if (t == null) return new ErrorResponse($"Unknown component type: '{n}'.");
				requiredTypes.Add(t);
			}

			var forbiddenTypes = new List<Type>();
			foreach (var n in missing)
			{
				var t = TypeResolver.ResolveComponentType(n);
				if (t == null) return new ErrorResponse($"Unknown component type: '{n}'.");
				forbiddenTypes.Add(t);
			}

			int? layerIndex = null;
			if (!string.IsNullOrEmpty(layerName))
			{
				var idx = LayerMask.NameToLayer(layerName);
				if (idx < 0) return new ErrorResponse($"Unknown layer: '{layerName}'.");
				layerIndex = idx;
			}

			Regex nameRegex = null;
			if (!string.IsNullOrEmpty(nameGlob))
				nameRegex = GlobMatcher.Compile(nameGlob);
			if (!string.IsNullOrEmpty(regex))
				nameRegex = new Regex(regex, RegexOptions.CultureInvariant);

			var filter = new SceneFilter
			{
				NameRegex = nameRegex,
				RequiredTypes = requiredTypes,
				ForbiddenTypes = forbiddenTypes,
				Tag = tagFilter,
				LayerIndex = layerIndex,
				PrefabAssetPath = prefabAssetPath,
				HasOverrides = wantOverrides,
				WantActive = wantActive,
				WantInactive = wantInactive,
			};

			// Resolve the scope: a path positional narrows the search to
			// that GameObject's subtree (the GameObject itself is excluded —
			// `find` returns descendants matching the filters, not the
			// scope itself).
			List<GameObject> roots;
			if (!string.IsNullOrEmpty(scopePath))
			{
				var parsedScope = PathParser.Parse(scopePath);
				if (!parsedScope.IsSuccess) return ErrorResponse.FromResult(parsedScope);
				var scopeRes = PathResolver.ResolveGameObject(parsedScope.Value);
				if (!scopeRes.IsSuccess) return ErrorResponse.FromResult(scopeRes);
				roots = PathResolver.GetImmediateChildren(scopeRes.Value);
			}
			else
			{
				roots = PathResolver.GetSceneRoots();
			}

			var matches = new List<GameObject>();
			foreach (var root in roots)
				CollectScene(root, filter, matches);

			return format switch
			{
				"json" => new SuccessResponse("", BuildSceneJson(matches)),
				"plain" => new SuccessResponse("", JoinScene(matches, "\n")),
				"null" or "null-delimited" or "null_delimited"
					=> new SuccessResponse("", JoinScene(matches, "\0")),
				"human" or "" => new SuccessResponse("", RenderSceneHuman(matches)),
				_ => new ErrorResponse($"Unknown format '{format}'. Use: human, json, plain, null."),
			};
		}

		private class SceneFilter
		{
			public Regex NameRegex;
			public List<Type> RequiredTypes;
			public List<Type> ForbiddenTypes;
			public string Tag;
			public int? LayerIndex;
			public string PrefabAssetPath;
			public bool HasOverrides;
			public bool WantActive;
			public bool WantInactive;
		}

		private static void CollectScene(GameObject go, SceneFilter filter, List<GameObject> sink)
		{
			if (MatchesScene(go, filter)) sink.Add(go);
			for (var i = 0; i < go.transform.childCount; i++)
				CollectScene(go.transform.GetChild(i).gameObject, filter, sink);
		}

		private static bool MatchesScene(GameObject go, SceneFilter f)
		{
			if (f.NameRegex != null && !f.NameRegex.IsMatch(go.name)) return false;

			if (f.WantActive && !go.activeInHierarchy) return false;
			if (f.WantInactive && go.activeInHierarchy) return false;

			if (!string.IsNullOrEmpty(f.Tag))
			{
				if (!go.CompareTag(f.Tag)) return false;
			}

			if (f.LayerIndex.HasValue && go.layer != f.LayerIndex.Value) return false;

			if (f.RequiredTypes != null && f.RequiredTypes.Count > 0)
			{
				foreach (var t in f.RequiredTypes)
					if (go.GetComponent(t) == null) return false;
			}

			if (f.ForbiddenTypes != null && f.ForbiddenTypes.Count > 0)
			{
				foreach (var t in f.ForbiddenTypes)
					if (go.GetComponent(t) != null) return false;
			}

			if (!string.IsNullOrEmpty(f.PrefabAssetPath) && !MatchesPrefab(go, f.PrefabAssetPath))
				return false;

			if (f.HasOverrides && !HasAnyOverrides(go)) return false;

			return true;
		}

		private static bool MatchesPrefab(GameObject go, string assetPath)
		{
			if (PrefabUtility.GetNearestPrefabInstanceRoot(go) != go) return false;
			var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
			if (source == null) return false;
			var path = AssetDatabase.GetAssetPath(source);
			return string.Equals(path, assetPath, StringComparison.Ordinal);
		}

		private static bool HasAnyOverrides(GameObject go)
		{
			if (PrefabUtility.GetNearestPrefabInstanceRoot(go) != go) return false;
			return PrefabUtility.HasPrefabInstanceAnyOverrides(go, includeDefaultOverrides: false);
		}

		private static List<string> GetStringList(JObject @params, string key)
		{
			var list = new List<string>();
			if (@params == null) return list;
			var tok = @params[key];
			if (tok == null || tok.Type == JTokenType.Null) return list;
			if (tok is JArray arr)
			{
				foreach (var item in arr)
				{
					var s = item?.ToString();
					if (!string.IsNullOrEmpty(s)) list.Add(s);
				}
			}
			else
			{
				var s = tok.ToString();
				if (!string.IsNullOrEmpty(s)) list.Add(s);
			}
			return list;
		}

		private static object BuildSceneJson(List<GameObject> matches)
		{
			var list = new List<object>(matches.Count);
			foreach (var go in matches)
			{
				list.Add(new
				{
					name = go.name,
					path = PathResolver.GetCanonicalPath(go),
					active = go.activeInHierarchy,
					instanceId = go.GetInstanceID(),
				});
			}
			return list;
		}

		private static string JoinScene(List<GameObject> matches, string sep)
		{
			var sb = new StringBuilder();
			var first = true;
			foreach (var go in matches)
			{
				if (!first) sb.Append(sep);
				first = false;
				sb.Append(PathResolver.GetCanonicalPath(go));
			}
			return sb.ToString();
		}

		private static string RenderSceneHuman(List<GameObject> matches)
		{
			if (matches.Count == 0) return "(no matches)";
			var sb = new StringBuilder();
			foreach (var go in matches)
			{
				sb.Append(PathResolver.GetCanonicalPath(go));
				if (!go.activeInHierarchy) sb.Append("  (inactive)");
				sb.Append('\n');
			}
			sb.Length--;
			return sb.ToString();
		}

		// =================================================================
		// Asset search
		// =================================================================

		private static object DoAssetSearch(ToolParams p, string pathSpec)
		{
			var name = p.Get("name");
			var type = p.Get("type");
			var label = p.Get("label");
			var area = p.Get("area");
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			SplitPathSpec(pathSpec, out var searchFolders, out var pathRegex);

			var filter = BuildAssetFilter(name, type, label, area);

			string[] guids;
			try
			{
				guids = searchFolders != null && searchFolders.Length > 0
					? AssetDatabase.FindAssets(filter, searchFolders)
					: AssetDatabase.FindAssets(filter);
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"AssetDatabase.FindAssets failed: {ex.Message}");
			}

			var seen = new HashSet<string>();
			var assetPaths = new List<string>(guids.Length);
			foreach (var guid in guids)
			{
				var assetPath = AssetDatabase.GUIDToAssetPath(guid);
				if (string.IsNullOrEmpty(assetPath)) continue;
				if (!seen.Add(assetPath)) continue;
				if (pathRegex != null && !pathRegex.IsMatch(assetPath)) continue;
				assetPaths.Add(assetPath);
			}

			assetPaths.Sort(StringComparer.OrdinalIgnoreCase);

			return format switch
			{
				"json" => RenderAssetJson(assetPaths),
				"plain" => new SuccessResponse("", JoinAssets(assetPaths, "\n")),
				"null" or "null-delimited" or "null_delimited"
					=> new SuccessResponse("", JoinAssets(assetPaths, "\0")),
				"human" or "" => new SuccessResponse("", RenderAssetHuman(assetPaths)),
				_ => new ErrorResponse($"Unknown format '{format}'. Use: human, json, plain, null."),
			};
		}

		private static string BuildAssetFilter(string name, string type, string label, string area)
		{
			var parts = new List<string>();

			if (!string.IsNullOrEmpty(name))
			{
				if (HasWildcards(name))
					parts.Add($"glob:\"{name}\"");
				else
					parts.Add(name);
			}

			if (!string.IsNullOrEmpty(type))
				parts.Add($"t:{type}");

			if (!string.IsNullOrEmpty(label))
				parts.Add($"l:{label}");

			if (!string.IsNullOrEmpty(area))
				parts.Add($"a:{area}");

			return string.Join(" ", parts);
		}

		private static bool HasWildcards(string s)
		{
			return s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0;
		}

		private static void SplitPathSpec(string spec, out string[] searchFolders, out Regex pathRegex)
		{
			searchFolders = null;
			pathRegex = null;
			if (string.IsNullOrEmpty(spec)) return;

			var normalized = spec.Replace('\\', '/').TrimEnd('/');
			if (string.IsNullOrEmpty(normalized)) return;

			if (!HasWildcards(normalized))
			{
				searchFolders = new[] { normalized };
				return;
			}

			var folderEnd = -1;
			var slashIdx = -1;
			for (var i = 0; i < normalized.Length; i++)
			{
				var c = normalized[i];
				if (c == '/')
				{
					slashIdx = i;
					continue;
				}
				if (c == '*' || c == '?')
				{
					folderEnd = slashIdx;
					break;
				}
			}

			if (folderEnd > 0)
			{
				searchFolders = new[] { normalized.Substring(0, folderEnd) };
			}

			pathRegex = GlobMatcher.Compile(normalized);
		}

		private static object RenderAssetJson(List<string> paths)
		{
			var list = new List<object>(paths.Count);
			foreach (var assetPath in paths)
			{
				var name = System.IO.Path.GetFileName(assetPath);
				var type = GetAssetType(assetPath);

				list.Add(new Dictionary<string, object>
				{
					["path"] = assetPath,
					["name"] = name,
					["type"] = type,
				});
			}
			return new SuccessResponse("", list);
		}

		private static string RenderAssetHuman(List<string> paths)
		{
			if (paths.Count == 0) return "(no matches)";
			var sb = new StringBuilder();
			foreach (var p in paths)
			{
				sb.Append(p).Append('\n');
			}
			sb.Length--;
			return sb.ToString();
		}

		private static string JoinAssets(List<string> paths, string sep)
		{
			if (paths.Count == 0) return "";
			var sb = new StringBuilder();
			var first = true;
			foreach (var p in paths)
			{
				if (!first) sb.Append(sep);
				first = false;
				sb.Append(p);
			}
			return sb.ToString();
		}

		private static string GetAssetType(string assetPath)
		{
			var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
			if (obj != null) return obj.GetType().Name;
			return "Unknown";
		}
	}
}
