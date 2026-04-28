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



using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Search the project asset database by name, type, label, and folder.
	/// Wraps <see cref="AssetDatabase.FindAssets(string, string[])"/>.
	///
	/// Filters AND-combine: all specified filters must match.
	///
	/// Uses Unity's documented filter syntax (see AssetDatabase.FindAssets docs):
	///   bare term  → filename match (partial, case-insensitive)
	///   glob:Foo*  → wildcard pattern (* and ?)
	///   t:Type     → asset type
	///   l:Label    → asset label
	///   a:assets   → area: all (default), assets, or packages
	///
	/// Folder restriction is via the <c>searchInFolders</c> parameter, not a
	/// filter prefix. The CLI's <c>--path</c> flag accepts either a plain folder
	/// (passed straight to <c>searchInFolders</c>) or a glob (folder prefix is
	/// extracted for the search; full glob is applied as a post-filter on the
	/// asset path).
	/// </summary>
	[UnityCliTool(Name = "find-asset",
		Description = "Search the project asset database by name, type, label, and folder.")]
	public static class FindAsset
	{
		public class Parameters
		{
			[ToolParameter("Asset name (partial match) or glob (e.g. 'Metal*'). Optional positional.")]
			public string Name { get; set; }

			[ToolParameter("Asset type filter (e.g. Material, Mesh, Prefab, ScriptableObject, Texture2D).")]
			public string Type { get; set; }

			[ToolParameter("Asset label filter (matches Unity's label system).")]
			public string Label { get; set; }

			[ToolParameter("Restrict to a folder or path glob (e.g. 'Assets/Enemies' or 'Assets/Enemies/*').")]
			public string Path { get; set; }

			[ToolParameter("Search area: all (default), assets, or packages.")]
			public string Area { get; set; }

			[ToolParameter("Output format: human (default), json, plain, null.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

			// Positional: first arg is name (legacy find-asset pattern).
			var args = p.GetRaw("args") as JArray;
			var name = p.Get("name")
				?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);
			var type = p.Get("type");
			var label = p.Get("label");
			var area = p.Get("area");
			var pathSpec = p.Get("path");
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			// --path can be a folder ("Assets/Enemies"), a folder glob
			// ("Assets/Enemies/*"), or a full asset-path glob
			// ("Assets/**/Red*.mat"). Split into a folder prefix for
			// searchInFolders (cheap) and a regex for post-filter (precise).
			SplitPathSpec(pathSpec, out var searchFolders, out var pathRegex);

			// Build AssetDatabase filter from name/type/label/area.
			var filter = BuildFilter(name, type, label, area);

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

			// Deduplicate (overlapping searchInFolders can return the same GUID
			// multiple times) and apply the path post-filter.
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
				"json" => RenderJson(assetPaths),
				"plain" => new SuccessResponse("", Join(assetPaths, "\n")),
				"null" or "null-delimited" or "null_delimited"
					=> new SuccessResponse("", Join(assetPaths, "\0")),
				"human" or "" => new SuccessResponse("", RenderHuman(assetPaths)),
				_ => new ErrorResponse($"Unknown format '{format}'. Use: human, json, plain, null."),
			};
		}

		// ---- filter building ----

		// Per Unity's AssetDatabase.FindAssets contract: filter terms are
		// space-separated. Bare terms partially match filenames. Wildcards
		// require the 'glob:' prefix. Type / label / area use t: / l: / a:.
		private static string BuildFilter(string name, string type, string label, string area)
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

		// ---- path spec parsing ----

		// Splits a user --path spec into:
		//   searchFolders: folders to pass to AssetDatabase.FindAssets for a
		//                  cheap scope restriction (null when there's none).
		//   pathRegex:     glob compiled against full asset paths for a precise
		//                  post-filter (null when --path is a plain folder and
		//                  no further filtering is needed).
		private static void SplitPathSpec(string spec, out string[] searchFolders, out Regex pathRegex)
		{
			searchFolders = null;
			pathRegex = null;
			if (string.IsNullOrEmpty(spec)) return;

			var normalized = spec.Replace('\\', '/').TrimEnd('/');

			if (!HasWildcards(normalized))
			{
				// Plain folder — pure searchInFolders restriction.
				searchFolders = new[] { normalized };
				return;
			}

			// Find the longest leading directory segment without wildcards.
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

		// ---- rendering ----

		private static object RenderJson(List<string> paths)
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

		private static string RenderHuman(List<string> paths)
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

		private static string Join(List<string> paths, string sep)
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

		// ---- helpers ----

		private static string GetAssetType(string assetPath)
		{
			var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
			if (obj != null) return obj.GetType().Name;
			return "Unknown";
		}
	}
}
