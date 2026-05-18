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
	/// Move and/or rename a GameObject in one operation. Mirrors a Hierarchy
	/// drag + F2 rename. Returns the canonical path of the moved object.
	///
	/// Destination grammar:
	///   "parent/name" → move under parent, rename to name
	///   "parent/"     → move under parent, keep current name
	/// The destination parent must already exist. Moving an object into one
	/// of its own descendants is rejected.
	/// </summary>
	[UnityCliTool(Name = "mv",
		Description = "Move and/or rename a GameObject. Returns canonical path.")]
	public static class Move
	{
		public class Parameters
		{
			[ToolParameter("Source GameObject path.", Required = true)]
			public string Src { get; set; }

			[ToolParameter("Destination 'parent/name' or 'parent/' (keep source name).", Required = true)]
			public string Dst { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var srcArg = p.Get("src") ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);
			var dstArg = p.Get("dst") ?? (args != null && args.Count > 1 ? args[1]?.ToString() : null);

			if (string.IsNullOrWhiteSpace(srcArg))
				return new ErrorResponse("mv requires a source path.");
			if (string.IsNullOrWhiteSpace(dstArg))
				return new ErrorResponse("mv requires a destination path.");

			// v3 §4.2: <src> fans out across the selection. <dst> must end with
			// '/' (a parent) when src cardinality > 1.
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
					$"mv src fans out to {srcs.Count} objects but destination '{dstArg}' is a single name. " +
					"Use a 'parent/' destination to move all sources under one parent.");

			GameObject sharedParent = null;
			Transform sharedParentT = null;
			string sharedParentDisplay = "/";
			if (dstIsParent)
			{
				var split = MoveCopyPath.Split(dstArg, "");
				if (!split.IsSuccess) return ErrorResponse.FromResult(split);
				if (!MoveCopyPath.IsSceneRoot(split.Value.parentPath))
				{
					var parentParse = PathParser.Parse(split.Value.parentPath);
					if (!parentParse.IsSuccess) return ErrorResponse.FromResult(parentParse);
					var parentRes = PathResolver.ResolveGameObject(parentParse.Value);
					if (!parentRes.IsSuccess) return ErrorResponse.FromResult(parentRes);
					sharedParent = parentRes.Value;
					sharedParentT = sharedParent.transform;
					sharedParentDisplay = PathResolver.GetCanonicalPath(sharedParent);
				}
			}

			var entries = new List<object>(srcs.Count);
			var stdoutLines = new List<string>(srcs.Count);
			var errorLines = new List<string>();

			foreach (var src in srcs)
			{
				GameObject parent;
				Transform parentT;
				string newName;
				string parentDisplay;
				if (dstIsParent)
				{
					parent = sharedParent;
					parentT = sharedParentT;
					parentDisplay = sharedParentDisplay;
					newName = src.name;
				}
				else
				{
					// Single src (we already errored on multi+non-parent dst).
					var split = MoveCopyPath.Split(dstArg, src.name);
					if (!split.IsSuccess) return ErrorResponse.FromResult(split);
					var (pPath, name) = split.Value;
					newName = name;
					if (MoveCopyPath.IsSceneRoot(pPath))
					{
						parent = null;
						parentT = null;
						parentDisplay = "/";
					}
					else
					{
						var parentParse = PathParser.Parse(pPath);
						if (!parentParse.IsSuccess) return ErrorResponse.FromResult(parentParse);
						var parentRes = PathResolver.ResolveGameObject(parentParse.Value);
						if (!parentRes.IsSuccess) return ErrorResponse.FromResult(parentRes);
						parent = parentRes.Value;
						parentT = parent.transform;
						parentDisplay = PathResolver.GetCanonicalPath(parent);
					}
				}

				if (parent == src)
				{
					errorLines.Add($"{PathResolver.GetCanonicalPath(src)}: cannot move a GameObject into itself.");
					continue;
				}
				if (parent != null && MoveCopyPath.IsAncestor(src.transform, parent.transform))
				{
					errorLines.Add($"{PathResolver.GetCanonicalPath(src)}: cannot move into one of its own descendants.");
					continue;
				}

				var undoGroup = Undo.GetCurrentGroup();
				Undo.SetCurrentGroupName($"Move {src.name} → {parentDisplay}/{newName}");

				if (src.transform.parent != parentT)
					Undo.SetTransformParent(src.transform, parentT, "Move GameObject");

				if (src.name != newName)
				{
					Undo.RecordObject(src, "Rename GameObject");
					src.name = newName;
				}

				Undo.CollapseUndoOperations(undoGroup);

				EditorUtility.SetDirty(src);
				if (parent != null) EditorUtility.SetDirty(parent);

				var canonical = PathResolver.GetCanonicalPath(src);
				stdoutLines.Add(canonical);
				entries.Add(new Dictionary<string, object>
				{
					["path"] = canonical,
					["name"] = src.name,
					["parent"] = parent != null ? PathResolver.GetCanonicalPath(parent) : "",
					["instanceId"] = src.GetInstanceID(),
				});
			}

			if (srcs.Count == 1)
			{
				var single = entries.Count > 0 ? entries[0] : null;
				var msg = stdoutLines.Count > 0 ? stdoutLines[0] : "";
				var resp = new SuccessResponse(msg, single);
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
	}
}
