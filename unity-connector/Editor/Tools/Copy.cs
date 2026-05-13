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
	/// Copy a GameObject (and, by default, its full subtree) to a new spot
	/// in the hierarchy. Mirrors the Editor's Edit→Duplicate when copying
	/// alongside the source, and reparents+duplicates in one step otherwise.
	///
	/// Options:
	///   --depth N         constrain how many descendant layers come along
	///                     (0 = no children, 1 = direct children, …)
	///   --auto-suffix     on a sibling-name collision, append a numeric
	///                     suffix using Unity's default " (1)", " (2)", …
	///   --auto-suffix F   custom format with {n} as the index placeholder
	///
	/// Prefab connections are not preserved (the result is a standalone
	/// clone). Use 'prefab create' if you want a new prefab from the copy.
	/// </summary>
	[UnityCliTool(Name = "cp",
		Description = "Copy a GameObject. Returns canonical path of the new object.")]
	public static class Copy
	{
		public class Parameters
		{
			[ToolParameter("Source GameObject path.", Required = true)]
			public string Src { get; set; }

			[ToolParameter("Destination 'parent/name' or 'parent/' (keep source name).", Required = true)]
			public string Dst { get; set; }

			[ToolParameter("Descendant depth: 0 = no children, 1 = direct children, omitted = full deep copy.")]
			public int Depth { get; set; }

			[ToolParameter("On name collision, suffix with format (default ' ({n})'). Use {n} as the index placeholder.")]
			public string AutoSuffix { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var srcArg = p.Get("src") ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);
			var dstArg = p.Get("dst") ?? (args != null && args.Count > 1 ? args[1]?.ToString() : null);

			if (string.IsNullOrWhiteSpace(srcArg))
				return new ErrorResponse("cp requires a source path.");
			if (string.IsNullOrWhiteSpace(dstArg))
				return new ErrorResponse("cp requires a destination path.");

			var depth = ResolveDepth(p);
			var suffixFormat = ResolveAutoSuffix(p);

			// v3 §4.2: <src> fans out across the selection. <dst> must end
			// with '/' (a parent) when src cardinality > 1.
			var srcParse = PathParser.Parse(srcArg);
			if (!srcParse.IsSuccess) return ErrorResponse.FromResult(srcParse);
			var srcRes = PathResolver.ResolveTargets(srcParse.Value);
			if (!srcRes.IsSuccess) return ErrorResponse.FromResult(srcRes);
			var srcs = srcRes.Value;
			if (srcs.Count == 0)
				return ErrorResponse.NotFound($"Source path '{srcArg}' matched no GameObjects.");

			var dstIsParent = dstArg.EndsWith("/");
			if (srcs.Count > 1 && !dstIsParent)
				return ErrorResponse.Ambiguous(
					$"cp src fans out to {srcs.Count} objects but destination '{dstArg}' is a single name. " +
					"Use a 'parent/' destination to copy all sources under one parent.");

			// Resolve the destination parent once when broadcasting.
			GameObject sharedParent = null;
			Transform sharedParentT = null;
			string parentPath = null;
			if (dstIsParent)
			{
				var split = MoveCopyPath.Split(dstArg, "");
				if (!split.IsSuccess) return ErrorResponse.FromResult(split);
				parentPath = split.Value.parentPath;
				if (!MoveCopyPath.IsSceneRoot(parentPath))
				{
					var parentParse = PathParser.Parse(parentPath);
					if (!parentParse.IsSuccess) return ErrorResponse.FromResult(parentParse);
					var parentRes = PathResolver.ResolveGameObject(parentParse.Value);
					if (!parentRes.IsSuccess) return ErrorResponse.FromResult(parentRes);
					sharedParent = parentRes.Value;
					sharedParentT = sharedParent.transform;
				}
			}

			var entries = new List<object>(srcs.Count);
			var stdoutLines = new List<string>(srcs.Count);
			var errorLines = new List<string>();

			foreach (var src in srcs)
			{
				GameObject parent;
				Transform parentT;
				string desiredName;
				if (dstIsParent)
				{
					parent = sharedParent;
					parentT = sharedParentT;
					desiredName = src.name;
				}
				else
				{
					// Single src (we already errored on multi+non-parent dst).
					var split = MoveCopyPath.Split(dstArg, src.name);
					if (!split.IsSuccess) return ErrorResponse.FromResult(split);
					var (pPath, name) = split.Value;
					desiredName = name;
					if (MoveCopyPath.IsSceneRoot(pPath))
					{
						parent = null;
						parentT = null;
					}
					else
					{
						var parentParse = PathParser.Parse(pPath);
						if (!parentParse.IsSuccess) return ErrorResponse.FromResult(parentParse);
						var parentRes = PathResolver.ResolveGameObject(parentParse.Value);
						if (!parentRes.IsSuccess) return ErrorResponse.FromResult(parentRes);
						parent = parentRes.Value;
						parentT = parent.transform;
					}
				}

				var clone = Object.Instantiate(src, parentT);
				if (clone == null)
				{
					errorLines.Add($"{PathResolver.GetCanonicalPath(src)}: failed to clone.");
					continue;
				}

				if (parentT == null && src.scene.IsValid())
					SceneManager.MoveGameObjectToScene(clone, src.scene);

				clone.name = MoveCopyPath.PickName(parentT, desiredName, suffixFormat);

				if (depth >= 0)
					PruneToDepth(clone.transform, depth);

				Undo.RegisterCreatedObjectUndo(clone, $"Copy {src.name}");

				if (parent != null) EditorUtility.SetDirty(parent);
				EditorUtility.SetDirty(clone);

				var canonical = PathResolver.GetCanonicalPath(clone);
				stdoutLines.Add(canonical);
				entries.Add(new Dictionary<string, object>
				{
					["path"] = canonical,
					["name"] = clone.name,
					["parent"] = parent != null ? PathResolver.GetCanonicalPath(parent) : "",
					["source"] = PathResolver.GetCanonicalPath(src),
					["depth"] = depth < 0 ? -1 : depth,
					["instanceId"] = clone.GetInstanceID(),
				});
			}

			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			if (srcs.Count == 1)
			{
				var single = entries.Count > 0 ? entries[0] : null;
				var msg = stdoutLines.Count > 0 ? stdoutLines[0] : "";
				// Pipe-friendly default: data is the canonical path (string)
				// so `cp X Y | set … …`, `cp X Y | select`, etc. work.
				// `--json` opts into the full per-target record.
				object data = format == "json" ? (object)single : (object)msg;
				var resp = new SuccessResponse(msg, data);
				if (errorLines.Count > 0)
				{
					resp.partialFailure = true;
					resp.stderr = string.Join("\n", errorLines);
				}
				return resp;
			}

			var multi = new SuccessResponse("", string.Join("\n", stdoutLines));
			if (errorLines.Count > 0)
			{
				multi.partialFailure = true;
				multi.stderr = string.Join("\n", errorLines);
			}
			return multi;
		}

		// --- helpers ---

		private static int ResolveDepth(ToolParams p)
		{
			var raw = p.GetRaw("depth");
			if (raw == null || raw.Type == JTokenType.Null) return -1; // unlimited
			var asInt = p.GetInt("depth");
			return asInt ?? -1;
		}

		private static string ResolveAutoSuffix(ToolParams p)
		{
			var keys = new[] { "auto-suffix", "autoSuffix", "auto_suffix" };
			foreach (var k in keys)
			{
				var tok = p.GetRaw(k);
				if (tok == null || tok.Type == JTokenType.Null) continue;
				if (tok.Type == JTokenType.Boolean)
					return tok.Value<bool>() ? " ({n})" : null;
				var s = tok.ToString();
				if (string.IsNullOrEmpty(s) || s == "false") return null;
				if (s == "true") return " ({n})";
				return s;
			}
			return null;
		}

		private static void PruneToDepth(Transform root, int maxDepth)
		{
			PruneAt(root, 0, maxDepth);
		}

		private static void PruneAt(Transform t, int level, int maxDepth)
		{
			if (level >= maxDepth)
			{
				for (var i = t.childCount - 1; i >= 0; i--)
					Object.DestroyImmediate(t.GetChild(i).gameObject);
				return;
			}
			for (var i = 0; i < t.childCount; i++)
				PruneAt(t.GetChild(i), level + 1, maxDepth);
		}
	}
}
