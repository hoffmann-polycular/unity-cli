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
	/// Modes:
	///   - No flags                → set selection to the given path
	///   - <c>--get</c>            → emit currently selected paths (one per line)
	///   - <c>--add</c>            → add the given path to the current selection
	///   - <c>--clear</c>          → deselect all
	///
	/// Paths are canonicalized so <c>get | inspect</c> and <c>find | select</c>
	/// chains work seamlessly.
	/// </summary>
	[UnityCliTool(Name = "select",
		Description = "Get or set the Editor's current Selection (Hierarchy bridge).")]
	public static class Select
	{
		public class Parameters
		{
			[ToolParameter("GameObject path to select. Omitted with --get / --clear; optional with --add (read from stdin).")]
			public string Path { get; set; }

			[ToolParameter("Print currently selected paths, one per line.")]
			public bool Get { get; set; }

			[ToolParameter("Add a path to the current selection instead of replacing it.")]
			public bool Add { get; set; }

			[ToolParameter("Clear the selection (deselect all).")]
			public bool Clear { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

			// Positional: `args` may contain the path when user writes `select World/Player`.
			var args = p.GetRaw("args") as JArray;
			var path = p.Get("path")
					   ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			var get = p.GetBool("get");
			var add = p.GetBool("add");
			var clear = p.GetBool("clear");

			// --get: list currently selected objects' paths.
			if (get)
			{
				var paths = new List<string>();
				foreach (var obj in Selection.objects)
				{
					var gameObject = obj as GameObject;
					if (gameObject != null)
					{
						paths.Add(PathResolver.GetCanonicalPath(gameObject));
						continue;
					}
					var comp = obj as Component;
					if (comp != null)
						paths.Add(PathResolver.GetCanonicalPath(comp.gameObject));
				}
				var output = string.Join("\n", paths);
				return new SuccessResponse(output.Length == 0 ? "(no selection)" : "", output);
			}

			// --clear: deselect all.
			if (clear)
			{
				Selection.objects = new UnityEngine.Object[0];
				return new SuccessResponse("Selection cleared.");
			}

			// set or --add: requires a path.
			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("Path required (or --get / --clear).");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return new ErrorResponse(parseResult.ErrorMessage);

			var goResult = PathResolver.ResolveGameObject(parseResult.Value);
			if (!goResult.IsSuccess) return new ErrorResponse(goResult.ErrorMessage);
			var go = goResult.Value;
			var canonicalPath = PathResolver.GetCanonicalPath(go);

			if (add)
			{
				// Add to current selection.
				var current = new List<UnityEngine.Object>(Selection.objects);
				if (!current.Contains(go))
					current.Add(go);
				Selection.objects = current.ToArray();
				return new SuccessResponse($"Added {canonicalPath} to selection.");
			}

			// Default: set selection to this object only.
			Selection.objects = new UnityEngine.Object[] { go };
			return new SuccessResponse($"Selected {canonicalPath}.");
		}
	}
}
