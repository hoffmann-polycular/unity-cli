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
	/// Searches objects across all loaded scenes by name, component, tag,
	/// layer, prefab source, or override state.
	///
	/// All filters AND-combine. <c>--component</c> and <c>--missing</c> accept
	/// either a single value or an array (for repeated CLI flags).
	/// </summary>
	[UnityCliTool(Name = "find",
		Description = "Find GameObjects in loaded scenes. Filters AND-combine.")]
	public static class FindObjects
	{
		public class Parameters
		{
			[ToolParameter("Name glob (e.g. 'Enemy*').")]
			public string Name { get; set; }

			[ToolParameter("Require a component of this type (may repeat).")]
			public string Component { get; set; }

			[ToolParameter("Exclude objects that have a component of this type (may repeat).")]
			public string Missing { get; set; }

			[ToolParameter("Match only objects with this tag.")]
			public string Tag { get; set; }

			[ToolParameter("Match only objects on this layer (name).")]
			public string Layer { get; set; }

			[ToolParameter("Match only instances of this prefab asset (root match).")]
			public string Prefab { get; set; }

			[ToolParameter("Match only prefab instances with overrides.")]
			public bool HasOverrides { get; set; }

			[ToolParameter("Match only active-in-hierarchy objects.")]
			public bool Active { get; set; }

			[ToolParameter("Match only inactive-in-hierarchy objects.")]
			public bool Inactive { get; set; }

			[ToolParameter("Output format: human (default), json, plain, null.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

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

			// Resolve component type names up-front so we fail fast on typos.
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

			var filter = new Filter
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

			var matches = new List<GameObject>();
			foreach (var root in PathResolver.GetSceneRoots())
				Collect(root, filter, matches);

			return format switch
			{
				"json" => new SuccessResponse("", BuildJson(matches)),
				"plain" => new SuccessResponse("", Join(matches, "\n")),
				"null" or "null-delimited" or "null_delimited"
					=> new SuccessResponse("", Join(matches, "\0")),
				"human" or "" => new SuccessResponse("", RenderHuman(matches)),
				_ => new ErrorResponse($"Unknown format '{format}'. Use: human, json, plain, null."),
			};
		}

		// ---- filtering ----

		private class Filter
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

		private static void Collect(GameObject go, Filter filter, List<GameObject> sink)
		{
			if (Matches(go, filter)) sink.Add(go);
			for (var i = 0; i < go.transform.childCount; i++)
				Collect(go.transform.GetChild(i).gameObject, filter, sink);
		}

		private static bool Matches(GameObject go, Filter f)
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
			// Only match prefab-instance roots — the outer boundary of an
			// instance. Children of an instance aren't themselves "instances".
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

		// ---- param helpers ----

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

		// ---- output rendering ----

		private static object BuildJson(List<GameObject> matches)
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

		private static string Join(List<GameObject> matches, string sep)
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

		private static string RenderHuman(List<GameObject> matches)
		{
			if (matches.Count == 0) return "(no matches)";
			var sb = new StringBuilder();
			foreach (var go in matches)
			{
				sb.Append(PathResolver.GetCanonicalPath(go));
				if (!go.activeInHierarchy) sb.Append("  (inactive)");
				sb.Append('\n');
			}
			sb.Length--; // trim trailing newline
			return sb.ToString();
		}
	}
}
