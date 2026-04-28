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
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Destroy GameObjects (and their children) by path. Supports single
	/// deletions, ambiguity resolution via <c>--all</c>, and batch deletion
	/// when multiple paths are supplied via stdin or positional args.
	///
	/// All deletions register with Undo so the Editor's undo stack records them.
	/// </summary>
	[UnityCliTool(Name = "delete",
		Description = "Destroy a GameObject and its children. Supports --all for ambiguous matches and batch via stdin.")]
	public static class Delete
	{
		public class Parameters
		{
			[ToolParameter("GameObject path to delete. Optional with batch mode (stdin or multiple positionals).")]
			public string Path { get; set; }

			[ToolParameter("Broadcast to all GameObjects matching the path (no ambiguity error).")]
			public bool All { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

			// Batch mode: multiple positional paths from stdin or command line.
			var args = p.GetRaw("args") as JArray;
			var singlePath = p.Get("path");
			var all = p.GetBool("all");

			// If we have positional args (from batch / stdin), delete each.
			if (args != null && args.Count > 0)
				return DoBatch(args, all);

			// Single path mode.
			if (string.IsNullOrWhiteSpace(singlePath))
				return new ErrorResponse("delete requires a path, or pass paths on stdin for batch deletion.");

			var parseResult = PathParser.Parse(singlePath);
			if (!parseResult.IsSuccess) return new ErrorResponse(parseResult.ErrorMessage);
			var parsed = parseResult.Value;

			var deleted = new List<string>();
			var errors = new List<string>();

			if (all)
			{
				var allRes = PathResolver.ResolveGameObjectsAll(parsed);
				if (!allRes.IsSuccess) return new ErrorResponse(allRes.ErrorMessage);
				foreach (var go in allRes.Value)
					DoDeleteOne(go, deleted, errors);
			}
			else
			{
				var goRes = PathResolver.ResolveGameObject(parsed);
				if (!goRes.IsSuccess) return new ErrorResponse(goRes.ErrorMessage);
				DoDeleteOne(goRes.Value, deleted, errors);
			}

			if (deleted.Count == 0 && errors.Count > 0)
				return new ErrorResponse(errors.Count == 1 ? errors[0] : string.Join("\n", errors));

			var data = new Dictionary<string, object>
			{
				["deleted"] = deleted,
			};
			if (errors.Count > 0)
				data["errors"] = errors;

			var msg = $"Deleted {deleted.Count} object(s)";
			if (errors.Count > 0) msg += $", {errors.Count} failed";
			msg += ".";

			return new SuccessResponse(msg, data);
		}

		private static object DoBatch(JArray args, bool all)
		{
			var deleted = new List<string>();
			var errors = new List<string>();

			foreach (var arg in args)
			{
				var pathStr = arg?.ToString();
				if (string.IsNullOrEmpty(pathStr)) continue;

				var parseResult = PathParser.Parse(pathStr);
				if (!parseResult.IsSuccess) { errors.Add($"{pathStr}: {parseResult.ErrorMessage}"); continue; }
				var parsed = parseResult.Value;

				if (all)
				{
					var allRes = PathResolver.ResolveGameObjectsAll(parsed);
					if (!allRes.IsSuccess) { errors.Add($"{pathStr}: {allRes.ErrorMessage}"); continue; }
					foreach (var go in allRes.Value)
						DoDeleteOne(go, deleted, errors);
				}
				else
				{
					var goRes = PathResolver.ResolveGameObject(parsed);
					if (!goRes.IsSuccess) { errors.Add($"{pathStr}: {goRes.ErrorMessage}"); continue; }
					DoDeleteOne(goRes.Value, deleted, errors);
				}
			}

			if (deleted.Count == 0 && errors.Count > 0)
				return new ErrorResponse(errors.Count == 1 ? errors[0] : string.Join("\n", errors));

			var data = new Dictionary<string, object>
			{
				["deleted"] = deleted,
			};
			if (errors.Count > 0)
				data["errors"] = errors;

			var msg = $"Batch delete: {deleted.Count} object(s)";
			if (errors.Count > 0) msg += $", {errors.Count} failed";
			msg += ".";

			return new SuccessResponse(msg, data);
		}

		private static void DoDeleteOne(GameObject go, List<string> deleted, List<string> errors)
		{
			var path = PathResolver.GetCanonicalPath(go);
			try
			{
				Undo.DestroyObjectImmediate(go);
				deleted.Add(path);
			}
			catch (System.Exception ex)
			{
				errors.Add($"{path}: {ex.Message}");
			}
		}
	}
}
