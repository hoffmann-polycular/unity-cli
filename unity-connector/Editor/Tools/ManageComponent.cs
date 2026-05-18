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
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Add, remove, or list Components on a GameObject. The CLI surface
	/// mirrors what the Inspector's "Add Component" / context-menu Remove
	/// affordances do, including Undo registration and dirty-marking.
	///
	/// Action semantics (from unity-cli-reference.md §component):
	///   - <c>list</c>  → all components on the object, with <c>[n]</c> only
	///                     when a type is duplicated.
	///   - <c>add</c>   → returns the canonical <c>path:Type[n]</c> of the
	///                     new component so callers can pipe straight into
	///                     <c>set</c> / <c>get</c>.
	///   - <c>remove</c>→ requires an explicit <c>[n]</c> when the object
	///                     has multiple components of that type. Refuses to
	///                     destroy Transform / RectTransform.
	/// </summary>
	[UnityCliTool(Name = "component",
		Description = "Add / remove / list Components on a GameObject. Subcommand via --action.")]
	public static class ManageComponent
	{
		public class Parameters
		{
			[ToolParameter("Subcommand: list, add, or remove.", Required = true)]
			public string Action { get; set; }

			[ToolParameter("GameObject path.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Component type. Required for add / remove. May include [n] for remove.")]
			public string Type { get; set; }

			[ToolParameter("Output format: human (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;

			// Positional layouts:
			//   list   : [path]
			//   add    : [path, type]
			//   remove : [path, type[idx]]
			var action = (p.Get("action")
				?? (args != null && args.Count > 0 ? args[0]?.ToString() : null) ?? "")
				.ToLowerInvariant();

			if (string.IsNullOrEmpty(action))
				return new ErrorResponse("component requires an action: list, add, or remove.");

			var path = p.Get("path")
				?? (args != null && args.Count > 1 ? args[1]?.ToString() : null);
			var typeArg = p.Get("type")
				?? (args != null && args.Count > 2 ? args[2]?.ToString() : null);
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse($"component {action} requires a GameObject path.");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);

			// v3: paths fan out across the selection. `list` against a fan-out
			// emits one block per target. `add`/`remove` apply to each, all
			// wrapped in one Undo group.
			var targetsRes = PathResolver.ResolveTargets(parseResult.Value);
			if (!targetsRes.IsSuccess) return ErrorResponse.FromResult(targetsRes);
			var targets = targetsRes.Value;

			if (targets.Count == 1)
			{
				return action switch
				{
					"list" => DoList(targets[0], format),
					"add" => DoAdd(targets[0], typeArg, format),
					"remove" or "rm" => DoRemove(targets[0], typeArg, format),
					_ => new ErrorResponse($"Unknown component action '{action}'. Use: list, add, remove."),
				};
			}

			// Fan-out path.
			if (action == "list")
			{
				var blocks = new List<object>(targets.Count);
				foreach (var go in targets)
				{
					var single = DoList(go, format);
					blocks.Add(single is SuccessResponse sr ? sr.data : single);
				}
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["count"] = targets.Count,
					["targets"] = blocks,
				});
			}

			if (action != "add" && action != "remove" && action != "rm")
				return new ErrorResponse($"Unknown component action '{action}'. Use: list, add, remove.");

			var undoGroup = Undo.GetCurrentGroup();
			Undo.IncrementCurrentGroup();
			Undo.SetCurrentGroupName($"component {action}");

			var applied = new List<object>();
			var errors = new List<string>();
			foreach (var go in targets)
			{
				object single = action == "add"
					? DoAdd(go, typeArg, format)
					: DoRemove(go, typeArg, format);
				switch (single)
				{
					case SuccessResponse sr:
						applied.Add(sr.data);
						break;
					case ErrorResponse er:
						errors.Add($"{PathResolver.GetCanonicalPath(go)}: {er.message}");
						break;
				}
			}
			Undo.CollapseUndoOperations(undoGroup);

			if (applied.Count == 0)
				return new ErrorResponse(string.Join("\n", errors));

			var msg = errors.Count == 0
				? $"component {action} applied to {applied.Count} object(s)."
				: $"component {action} applied to {applied.Count} object(s); {errors.Count} failed.";
			return new SuccessResponse(msg, new Dictionary<string, object>
			{
				["applied"] = applied,
				["errors"] = errors,
			});
		}

		// ---- list ----

		private static object DoList(GameObject go, string format)
		{
			var comps = go.GetComponents<Component>();
			var entries = new List<Dictionary<string, object>>(comps.Length);

			// Track per-type counts so we know when [n] disambiguators are needed.
			var typeCounts = new Dictionary<Type, int>();
			foreach (var c in comps)
				if (c != null)
					typeCounts[c.GetType()] = typeCounts.TryGetValue(c.GetType(), out var n) ? n + 1 : 1;

			var seenIndex = new Dictionary<Type, int>();
			foreach (var c in comps)
			{
				if (c == null)
				{
					entries.Add(new Dictionary<string, object>
					{
						["type"] = "<missing script>",
						["enabled"] = false,
					});
					continue;
				}
				var t = c.GetType();
				var idx = seenIndex.TryGetValue(t, out var s) ? s : 0;
				seenIndex[t] = idx + 1;
				var label = typeCounts[t] > 1 ? $"{t.Name}[{idx}]" : t.Name;

				entries.Add(new Dictionary<string, object>
				{
					["type"] = t.Name,
					["label"] = label,
					["enabled"] = IsEnabled(c),
					["instanceId"] = c.GetInstanceID(),
				});
			}

			var data = new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(go),
				["components"] = entries,
			};

			if (format == "json") return new SuccessResponse("", data);

			var sb = new StringBuilder();
			sb.Append(data["path"]).Append('\n');
			foreach (var e in entries)
			{
				sb.Append("  ").Append(e.TryGetValue("label", out var lbl) ? lbl : e["type"]);
				if (e.TryGetValue("enabled", out var en) && en is bool b && !b)
					sb.Append("  (disabled)");
				sb.Append('\n');
			}
			if (entries.Count == 0) sb.Append("  (no components)\n");
			return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
		}

		// ---- add ----

		private static object DoAdd(GameObject go, string typeArg, string format)
		{
			if (string.IsNullOrWhiteSpace(typeArg))
				return new ErrorResponse("component add requires a type name.");

			// Strip any [n] the user might have supplied — meaningless for add.
			var compRefRes = PathParser.ParseComponentSpec(typeArg);
			if (!compRefRes.IsSuccess) return ErrorResponse.FromResult(compRefRes);
			var typeName = compRefRes.Value.TypeName;

			var type = TypeResolver.ResolveComponentType(typeName);
			if (type == null) return new ErrorResponse($"Unknown component type: '{typeName}'.");

			Component added;
			try
			{
				added = Undo.AddComponent(go, type);
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"Failed to add {type.Name}: {ex.Message}");
			}
			if (added == null)
				return new ErrorResponse(
					$"Could not add {type.Name} (likely DisallowMultipleComponent or missing dependency).");

			EditorUtility.SetDirty(go);

			// New component is always the last one of its type on the GameObject.
			var sameType = go.GetComponents(type);
			var newIndex = sameType.Length - 1;
			var label = sameType.Length > 1 ? $"{type.Name}[{newIndex}]" : type.Name;
			var canonicalPath = $"{PathResolver.GetCanonicalPath(go)}:{label}";

			var data = new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(go),
				["component"] = type.Name,
				["index"] = newIndex,
				["count"] = sameType.Length,
				["componentPath"] = canonicalPath,
				["instanceId"] = added.GetInstanceID(),
			};

			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse(canonicalPath, data);
		}

		// ---- remove ----

		private static object DoRemove(GameObject go, string typeArg, string format)
		{
			if (string.IsNullOrWhiteSpace(typeArg))
				return new ErrorResponse("component remove requires a type name (use Type[n] for duplicates).");

			var compRefRes = PathParser.ParseComponentSpec(typeArg);
			if (!compRefRes.IsSuccess) return ErrorResponse.FromResult(compRefRes);
			var compRef = compRefRes.Value;

			var type = TypeResolver.ResolveComponentType(compRef.TypeName);
			if (type == null) return new ErrorResponse($"Unknown component type: '{compRef.TypeName}'.");

			// Refuse to destroy structurally-required components — Unity would
			// throw anyway, but a clear message beats the raw exception.
			if (typeof(Transform).IsAssignableFrom(type))
				return new ErrorResponse($"Cannot remove {type.Name} — it's required for every GameObject.");

			var resolveRes = PathResolver.ResolveComponent(go, compRef);
			if (!resolveRes.IsSuccess) return ErrorResponse.FromResult(resolveRes);
			var component = resolveRes.Value;

			var goPath = PathResolver.GetCanonicalPath(go);
			var label = compRef.Index.HasValue ? $"{type.Name}[{compRef.Index.Value}]" : type.Name;

			try
			{
				Undo.DestroyObjectImmediate(component);
			}
			catch (Exception ex)
			{
				return new ErrorResponse(
					$"Failed to remove {type.Name} from {goPath}: {ex.Message}");
			}

			EditorUtility.SetDirty(go);

			var data = new Dictionary<string, object>
			{
				["path"] = goPath,
				["component"] = type.Name,
				["removed"] = label,
			};

			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse($"Removed {label} from {goPath}.", data);
		}

		// ---- helpers ----

		private static bool IsEnabled(Component c)
		{
			if (c is Behaviour beh) return beh.enabled;
			if (c is Renderer ren) return ren.enabled;
			if (c is Collider col) return col.enabled;
			return true;
		}
	}
}
