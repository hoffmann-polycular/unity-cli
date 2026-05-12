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
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// List the children of a GameObject. Path resolution follows the v3
	/// contract: bare paths and "./..." anchor at the current selection,
	/// "/..." at the Hierarchy root, "../..." walks up. Fan-out across
	/// multiple selected objects is the default — output is one block
	/// per target, in selection order.
	///
	/// With no positional, lists scene roots (matches what the Hierarchy
	/// window shows with nothing selected).
	/// </summary>
	[UnityCliTool(Name = "ls",
		Description = "List Hierarchy children. No path lists scene roots; '.' lists selection's children.")]
	public static class Ls
	{
		public class Parameters
		{
			[ToolParameter("GameObject path. Bare/'./' = selection-relative; '/' = Hierarchy root. Omit for scene roots.")]
			public string Path { get; set; }

			[ToolParameter("Recurse into descendants (-R / --recursive on the CLI).")]
			public bool Recursive { get; set; }

			[ToolParameter("Include each object's component type list.")]
			public bool Components { get; set; }

			[ToolParameter("Output format: human (default), json, plain, null.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var path = p.Get("path")
					   ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			var recursive = p.GetBool("recursive");
			var includeComponents = p.GetBool("components");
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			List<TargetListing> targets;
			if (string.IsNullOrWhiteSpace(path))
			{
				// No path → children of the current selection (spec §2.1:
				// "selection is the cwd"). Falls back to scene roots when
				// nothing is selected (spec §4.4).
				var sel = PathResolver.SelectionSnapshot();
				if (sel.Count == 0)
				{
					targets = new List<TargetListing>
					{
						new TargetListing
						{
							RootPath = "",
							Children = PathResolver.GetSceneRoots(),
						},
					};
				}
				else
				{
					targets = new List<TargetListing>(sel.Count);
					foreach (var go in sel)
					{
						targets.Add(new TargetListing
						{
							RootPath = PathResolver.GetCanonicalPath(go),
							Children = PathResolver.GetImmediateChildren(go),
						});
					}
				}
			}
			else
			{
				var parseResult = PathParser.Parse(path);
				if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
				if (parseResult.Value.Kind == PathKind.ProjectSettings)
					return new ErrorResponse(
						"ls does not list ProjectSettings groups; use 'inspect ProjectSettings/' for that.",
						ErrorKind.Usage);

				var resolveResult = PathResolver.ResolveTargets(parseResult.Value);
				if (!resolveResult.IsSuccess) return ErrorResponse.FromResult(resolveResult);

				targets = new List<TargetListing>(resolveResult.Value.Count);
				foreach (var t in resolveResult.Value)
				{
					targets.Add(new TargetListing
					{
						RootPath = PathResolver.GetCanonicalPath(t),
						Children = PathResolver.GetImmediateChildren(t),
					});
				}
			}

			switch (format)
			{
				case "json":
					return RenderJson(targets, recursive, includeComponents);
				case "plain":
					return new SuccessResponse("", RenderDelimited(targets, recursive, includeComponents, "\n"));
				case "null":
				case "null-delimited":
				case "null_delimited":
					return new SuccessResponse("", RenderDelimited(targets, recursive, includeComponents, "\0"));
				case "human":
				case "":
					return new SuccessResponse("", RenderHuman(targets, recursive, includeComponents));
				default:
					return new ErrorResponse($"Unknown format '{format}'. Use: human, json, plain, null.");
			}
		}

		// ---- Per-target listing struct ----

		private struct TargetListing
		{
			public string RootPath;
			public List<GameObject> Children;
		}

		// ---- JSON rendering ----

		private static SuccessResponse RenderJson(
			List<TargetListing> targets, bool recursive, bool includeComponents)
		{
			if (targets.Count == 1)
			{
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = targets[0].RootPath,
					["children"] = BuildJsonChildren(targets[0].Children, recursive, includeComponents),
				});
			}
			var blocks = new List<object>(targets.Count);
			foreach (var t in targets)
			{
				blocks.Add(new Dictionary<string, object>
				{
					["path"] = t.RootPath,
					["children"] = BuildJsonChildren(t.Children, recursive, includeComponents),
				});
			}
			return new SuccessResponse("", new Dictionary<string, object>
			{
				["count"] = targets.Count,
				["targets"] = blocks,
			});
		}

		private static List<object> BuildJsonChildren(
			List<GameObject> children, bool recursive, bool includeComponents)
		{
			var list = new List<object>(children.Count);
			foreach (var c in children)
			{
				if (c == null) continue;
				var entry = new Dictionary<string, object>
				{
					["name"] = c.name,
					["path"] = PathResolver.GetCanonicalPath(c),
					["active"] = c.activeInHierarchy,
				};
				if (includeComponents)
					entry["components"] = ComponentNames(c);
				if (recursive)
				{
					var kids = PathResolver.GetImmediateChildren(c);
					if (kids.Count > 0)
						entry["children"] = BuildJsonChildren(kids, true, includeComponents);
				}
				list.Add(entry);
			}
			return list;
		}

		// ---- Plain / null-delimited rendering ----

		private static string RenderDelimited(
			List<TargetListing> targets, bool recursive, bool includeComponents, string sep)
		{
			var sb = new StringBuilder();
			var first = true;
			foreach (var t in targets)
				AppendDelimited(t.Children, recursive, includeComponents, sb, sep, ref first);
			return sb.ToString();
		}

		private static void AppendDelimited(
			List<GameObject> children, bool recursive, bool includeComponents,
			StringBuilder sb, string sep, ref bool first)
		{
			foreach (var c in children)
			{
				if (c == null) continue;
				if (!first) sb.Append(sep);
				first = false;
				sb.Append(PathResolver.GetCanonicalPath(c));
				if (includeComponents)
				{
					// Tab-separated so `cut -f1` / `cut -f2` gives clean splits.
					sb.Append('\t').Append(string.Join(",", ComponentNames(c)));
				}
				if (recursive)
				{
					var kids = PathResolver.GetImmediateChildren(c);
					if (kids.Count > 0)
						AppendDelimited(kids, true, includeComponents, sb, sep, ref first);
				}
			}
		}

		// ---- Human rendering ----

		private static string RenderHuman(
			List<TargetListing> targets, bool recursive, bool includeComponents)
		{
			var sb = new StringBuilder();
			for (var i = 0; i < targets.Count; i++)
			{
				if (i > 0) sb.Append('\n');
				var t = targets[i];
				if (!string.IsNullOrEmpty(t.RootPath))
					sb.Append(t.RootPath).Append('\n');

				if (t.Children == null || t.Children.Count == 0)
				{
					sb.Append(string.IsNullOrEmpty(t.RootPath) ? "(empty hierarchy)" : "  (no children)");
					sb.Append('\n');
					continue;
				}
				AppendHuman(t.Children, recursive, includeComponents, sb, depth: string.IsNullOrEmpty(t.RootPath) ? 0 : 1);
			}
			return sb.ToString().TrimEnd('\n');
		}

		private static void AppendHuman(
			List<GameObject> children, bool recursive, bool includeComponents,
			StringBuilder sb, int depth)
		{
			foreach (var c in children)
			{
				if (c == null) continue;
				sb.Append(' ', depth * 2);
				sb.Append(PathResolver.GetSegmentName(c));
				if (!c.activeInHierarchy) sb.Append("  (inactive)");
				if (includeComponents)
					sb.Append("  [").Append(string.Join(", ", ComponentNames(c))).Append(']');
				sb.Append('\n');

				if (recursive)
				{
					var kids = PathResolver.GetImmediateChildren(c);
					if (kids.Count > 0)
						AppendHuman(kids, true, includeComponents, sb, depth + 1);
				}
			}
		}

		private static List<string> ComponentNames(GameObject go)
		{
			var comps = go.GetComponents<Component>();
			var names = new List<string>(comps.Length);
			foreach (var comp in comps)
				names.Add(comp == null ? "<missing script>" : comp.GetType().Name);
			return names;
		}
	}
}
