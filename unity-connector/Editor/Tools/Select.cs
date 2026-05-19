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

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Get or set the Unity Editor's current Selection — the bridge between
	/// the terminal and the Hierarchy/Inspector windows.
	///
	/// v3: paths fan out the same way as everywhere else. <c>select Hat</c>
	/// with three Players selected replaces the selection with all three
	/// Hat children. <c>select --add ...</c> adds every fan-out target.
	/// Multiple positional paths (or stdin lines) compose: each is resolved
	/// and the union becomes the new selection.
	///
	/// Modes:
	///   - No flags                → set selection to the resolved path(s)
	///   - <c>--get</c>            → emit currently selected paths (one per line)
	///   - <c>--add</c>            → add the resolved path(s) to current selection
	///   - <c>--clear</c>          → deselect all
	/// </summary>
	[UnityCliTool(Name = "select",
		Description = "Get or set the Editor's current Selection. Path fan-out becomes the new selection.")]
	public static class Select
	{
		public class Parameters
		{
			[ToolParameter("GameObject path(s) to select. Bare/'./' = selection-relative; '/' = Hierarchy root.")]
			public string Path { get; set; }

			[ToolParameter("Print currently selected paths, one per line.")]
			public bool Get { get; set; }

			[ToolParameter("Add to the current selection instead of replacing it.")]
			public bool Add { get; set; }

			[ToolParameter("Clear the selection (deselect all).")]
			public bool Clear { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

			var args = p.GetRaw("args") as JArray;
			var positional = new List<string>();
			if (args != null)
			{
				foreach (var a in args)
				{
					var s = a?.ToString();
					if (!string.IsNullOrEmpty(s)) positional.Add(s);
				}
			}
			var pathFlag = p.Get("path");
			if (!string.IsNullOrEmpty(pathFlag) && (positional.Count == 0 || positional[0] != pathFlag))
				positional.Insert(0, pathFlag);

			var get = p.GetBool("get");
			var add = p.GetBool("add");
			var clear = p.GetBool("clear");

			// --get: list currently selected objects' paths. Asset selections
			// (Project window) emit their asset path; scene selections emit
			// the canonical Hierarchy path. Round-trips with `select <input>`:
			// whatever path you pass in is what you'll see back here.
			if (get)
			{
				var paths = new List<string>();
				foreach (var obj in Selection.objects)
				{
					if (obj == null) continue;
					// Asset first — covers prefabs, textures, materials, scenes,
					// scriptable objects, etc. selected in the Project window.
					var assetPath = AssetDatabase.GetAssetPath(obj);
					if (!string.IsNullOrEmpty(assetPath))
					{
						paths.Add(assetPath);
						continue;
					}
					if (obj is GameObject go) paths.Add(PathResolver.GetCanonicalPath(go));
					else if (obj is Component c) paths.Add(PathResolver.GetCanonicalPath(c.gameObject));
				}
				// Honour the standard --null-delimited / --null format so
				// `select --get | xargs -0 ...` works even when asset paths
				// have spaces. Matches ls/find's output flag set.
				var format = (p.Get("format") ?? "plain").ToLowerInvariant();
				var sep = (format == "null" || format == "null-delimited" || format == "null_delimited")
					? "\0" : "\n";
				var output = string.Join(sep, paths);
				return new SuccessResponse(output.Length == 0 ? "(no selection)" : "", output);
			}

			// --clear: deselect all.
			if (clear)
			{
				Selection.objects = new UnityEngine.Object[0];
				return new SuccessResponse("Selection cleared.");
			}

			if (positional.Count == 0)
				return new ErrorResponse("Path required (or --get / --clear).");

			// Resolve every positional through the fan-out resolver. Selection-
			// anchored paths are resolved against the selection AS IT EXISTS
			// AT THE START of the call — we snapshot once so a multi-positional
			// invocation doesn't see its own intermediate selection edits.
			//
			// Asset paths (`Assets/...` / `Packages/...`) without a sub-asset
			// or component suffix are handled separately: they select the
			// asset itself in the Project window (the natural Unity behaviour)
			// rather than the in-memory prefab-root GameObject that the
			// fan-out resolver would otherwise return for prefab assets only.
			var snapshotBefore = PathResolver.SelectionSnapshot();
			var resolvedObjects = new List<UnityEngine.Object>();
			var resolvedPaths = new List<string>();
			var errors = new List<string>();
			foreach (var path in positional)
			{
				var parseResult = PathParser.Parse(path);
				if (!parseResult.IsSuccess) { errors.Add($"{path}: {parseResult.ErrorMessage}"); continue; }
				var parsed = parseResult.Value;

				// Asset-path with no inner-segment / component → select the asset.
				if (parsed.Kind == PathKind.Asset
					&& (parsed.Segments == null || parsed.Segments.Count == 0)
					&& !parsed.Component.IsPresent)
				{
					var asset = AssetDatabase.LoadMainAssetAtPath(parsed.AssetPath);
					if (asset == null)
					{
						errors.Add($"{path}: asset not found.");
						continue;
					}
					resolvedObjects.Add(asset);
					resolvedPaths.Add(parsed.AssetPath);
					continue;
				}

				var targetsRes = PathResolver.ResolveTargets(parsed);
				if (!targetsRes.IsSuccess) { errors.Add($"{path}: {targetsRes.ErrorMessage}"); continue; }

				foreach (var go in targetsRes.Value)
				{
					resolvedObjects.Add(go);
					resolvedPaths.Add(PathResolver.GetCanonicalPath(go));
				}
			}

			if (resolvedObjects.Count == 0)
			{
				if (errors.Count > 0)
					return new ErrorResponse(string.Join("\n", errors));
				return new ErrorResponse("No targets resolved from the given paths.");
			}

			// Dedup while preserving first-seen order.
			var dedup = new List<UnityEngine.Object>();
			var dedupPaths = new List<string>();
			var seen = new HashSet<int>();
			void Push(UnityEngine.Object o, string p)
			{
				if (o == null) return;
				var id = o.GetInstanceID();
				if (!seen.Add(id)) return;
				dedup.Add(o);
				if (p != null) dedupPaths.Add(p);
			}

			if (add)
			{
				// Start from current selection, then add resolved targets.
				foreach (var s in Selection.objects)
				{
					if (s == null) continue;
					var existingPath = AssetDatabase.GetAssetPath(s);
					if (string.IsNullOrEmpty(existingPath) && s is GameObject existingGo)
						existingPath = PathResolver.GetCanonicalPath(existingGo);
					Push(s, existingPath);
				}
			}
			for (var i = 0; i < resolvedObjects.Count; i++)
				Push(resolvedObjects[i], i < resolvedPaths.Count ? resolvedPaths[i] : null);

			Selection.objects = dedup.ToArray();

			var verb = add ? "Added" : "Selected";
			var msg = dedupPaths.Count == 1
				? $"{verb} {dedupPaths[0]}."
				: $"{verb} {dedupPaths.Count} object(s).";
			if (errors.Count > 0) msg += $" ({errors.Count} input(s) failed.)";

			return new SuccessResponse(msg, new Dictionary<string, object>
			{
				["selected"] = dedupPaths,
				["errors"] = errors,
				["snapshotBefore"] = snapshotBefore.Count,
			});
		}
	}
}
