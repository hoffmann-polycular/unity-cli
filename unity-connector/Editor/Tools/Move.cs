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

			var srcParse = PathParser.Parse(srcArg);
			if (!srcParse.IsSuccess) return ErrorResponse.FromResult(srcParse);
			var srcRes = PathResolver.ResolveGameObject(srcParse.Value);
			if (!srcRes.IsSuccess) return ErrorResponse.FromResult(srcRes);
			var src = srcRes.Value;

			var dstSplit = MoveCopyPath.Split(dstArg, src.name);
			if (!dstSplit.IsSuccess) return ErrorResponse.FromResult(dstSplit);
			var (parentPath, newName) = dstSplit.Value;

			GameObject parent = null;
			Transform parentT = null;
			var parentDisplay = "/";
			if (!MoveCopyPath.IsSceneRoot(parentPath))
			{
				var parentParse = PathParser.Parse(parentPath);
				if (!parentParse.IsSuccess) return ErrorResponse.FromResult(parentParse);
				var parentRes = PathResolver.ResolveGameObject(parentParse.Value);
				if (!parentRes.IsSuccess) return ErrorResponse.FromResult(parentRes);
				parent = parentRes.Value;
				parentT = parent.transform;
				parentDisplay = PathResolver.GetCanonicalPath(parent);

				if (parent == src)
					return new ErrorResponse("Cannot move a GameObject into itself.");
				if (MoveCopyPath.IsAncestor(src.transform, parent.transform))
					return new ErrorResponse("Cannot move a GameObject into one of its own descendants.");
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
			var data = new Dictionary<string, object>
			{
				["path"] = canonical,
				["name"] = src.name,
				["parent"] = parent != null ? PathResolver.GetCanonicalPath(parent) : "",
				["instanceId"] = src.GetInstanceID(),
			};
			return new SuccessResponse(canonical, data);
		}
	}
}
