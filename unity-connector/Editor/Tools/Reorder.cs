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
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Reorder a GameObject among its siblings, or a Component on its
	/// GameObject. The mode is chosen by the path: a plain hierarchy path
	/// reorders siblings; a path with a ":Component" suffix reorders the
	/// component within the GameObject's component list.
	///
	/// Operations (mutually exclusive):
	///   --index N       Set absolute 0-based position (clamped to range).
	///   --first         Move to first.
	///   --last          Move to last.
	///   --up [N]        Move up by N (default 1).
	///   --down [N]      Move down by N (default 1).
	///   --before NAME   Insert immediately before sibling/component named NAME.
	///   --after NAME    Insert immediately after sibling/component named NAME.
	///
	/// Component reordering uses Unity's only public API for it
	/// (<c>ComponentUtility.MoveComponentUp</c> / <c>MoveComponentDown</c>),
	/// so absolute targets are reached by stepping repeatedly.
	/// </summary>
	[UnityCliTool(Name = "reorder",
		Description = "Reorder a GameObject among siblings or a Component on its object.")]
	public static class Reorder
	{
		public class Parameters
		{
			[ToolParameter("Target path. GameObject path for sibling reorder; 'path:Component' for component reorder.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Absolute 0-based index. Mutually exclusive with --first/--last/--up/--down/--before/--after.")]
			public int Index { get; set; }

			[ToolParameter("Move to the first position among siblings/components.")]
			public bool First { get; set; }

			[ToolParameter("Move to the last position among siblings/components.")]
			public bool Last { get; set; }

			[ToolParameter("Move up by N (default 1). Clamped to range.")]
			public int Up { get; set; }

			[ToolParameter("Move down by N (default 1). Clamped to range.")]
			public int Down { get; set; }

			[ToolParameter("Insert immediately before the named sibling/component.")]
			public string Before { get; set; }

			[ToolParameter("Insert immediately after the named sibling/component.")]
			public string After { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var pathArg = p.Get("path") ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			if (string.IsNullOrWhiteSpace(pathArg))
				return new ErrorResponse("reorder requires a target path.");

			var parseResult = PathParser.Parse(pathArg);
			if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
			var parsed = parseResult.Value;

			// v3: <p> fans out, <op> is broadcast.
			var targetsRes = PathResolver.ResolveTargets(parsed);
			if (!targetsRes.IsSuccess) return ErrorResponse.FromResult(targetsRes);
			var targets = targetsRes.Value;

			var op = ResolveOp(p);
			if (!op.IsSuccess) return ErrorResponse.FromResult(op);

			if (targets.Count == 1)
			{
				var go = targets[0];
				if (parsed.Component.IsPresent)
					return ReorderComponent(go, parsed.Component, op.Value);
				return ReorderSibling(go, op.Value);
			}

			// Fan-out: one Undo group, per-target results.
			var undoGroup = Undo.GetCurrentGroup();
			Undo.IncrementCurrentGroup();
			Undo.SetCurrentGroupName("reorder");

			var applied = new List<object>();
			var errors = new List<string>();
			foreach (var go in targets)
			{
				object result = parsed.Component.IsPresent
					? ReorderComponent(go, parsed.Component, op.Value)
					: ReorderSibling(go, op.Value);
				switch (result)
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
				? $"reorder applied to {applied.Count} object(s)."
				: $"reorder applied to {applied.Count} object(s); {errors.Count} failed.";
			return new SuccessResponse(msg, new Dictionary<string, object>
			{
				["applied"] = applied,
				["errors"] = errors,
			});
		}

		// --- sibling reorder ---

		private static object ReorderSibling(GameObject go, Op op)
		{
			Transform parent = go.transform.parent;
			List<GameObject> siblings = parent != null
				? PathResolver.GetImmediateChildren(parent.gameObject)
				: PathResolver.GetSceneRoots();

			var count = siblings.Count;
			var oldIndex = siblings.IndexOf(go);
			if (oldIndex < 0)
				return new ErrorResponse("Could not locate target among its siblings.");

			var target = ResolveTargetIndex(op, oldIndex, count, name => FindIndexByName(siblings, name));
			if (!target.IsSuccess) return ErrorResponse.FromResult(target);
			var newIndex = target.Value;

			if (newIndex == oldIndex)
				return BuildSiblingResponse(go, oldIndex, newIndex, count, "noop");

			Undo.SetTransformParent(go.transform, parent, $"Reorder {go.name}");
			go.transform.SetSiblingIndex(newIndex);
			EditorUtility.SetDirty(go);

			return BuildSiblingResponse(go, oldIndex, newIndex, count, "moved");
		}

		private static object BuildSiblingResponse(GameObject go, int oldIndex, int newIndex, int siblingCount, string status)
		{
			var canonical = PathResolver.GetCanonicalPath(go);
			var data = new Dictionary<string, object>
			{
				["path"] = canonical,
				["mode"] = "sibling",
				["from"] = oldIndex,
				["to"] = newIndex,
				["siblingCount"] = siblingCount,
				["status"] = status,
			};
			return new SuccessResponse(canonical, data);
		}

		// --- component reorder ---

		private static object ReorderComponent(GameObject go, ComponentRef compRef, Op op)
		{
			var compResolve = PathResolver.ResolveComponent(go, compRef);
			if (!compResolve.IsSuccess) return ErrorResponse.FromResult(compResolve);
			var component = compResolve.Value;

			if (component is Transform)
				return new ErrorResponse("The Transform / RectTransform cannot be reordered.");

			if ((component.hideFlags & HideFlags.HideInInspector) != 0)
				return new ErrorResponse(
					$"'{component.GetType().Name}' is hidden in the Inspector and cannot be reordered " +
					"(Unity's ComponentUtility refuses HideInInspector / [DisallowReorderComponent] components).");

			var components = go.GetComponents<Component>();
			var oldIndex = System.Array.IndexOf(components, component);
			if (oldIndex < 0)
				return new ErrorResponse("Could not locate component on the GameObject.");
			var count = components.Length;

			var target = ResolveTargetIndex(op, oldIndex, count, name => FindComponentIndexByName(components, name));
			if (!target.IsSuccess) return ErrorResponse.FromResult(target);
			var newIndex = target.Value;

			// Transform pins index 0; any move target lower is bumped to 1.
			if (newIndex == 0) newIndex = 1;

			if (newIndex == oldIndex)
				return BuildComponentResponse(go, component, oldIndex, newIndex, count, "noop");

			Undo.RegisterCompleteObjectUndo(go, $"Reorder {component.GetType().Name}");

			var current = oldIndex;
			while (current > newIndex)
			{
				if (!ComponentUtility.MoveComponentUp(component)) break;
				current--;
			}
			while (current < newIndex)
			{
				if (!ComponentUtility.MoveComponentDown(component)) break;
				current++;
			}

			if (current == oldIndex)
				return new ErrorResponse(
					$"Unity refused to reorder '{component.GetType().Name}' " +
					"(may be marked [DisallowReorderComponent], required by another component, or otherwise locked).");

			EditorUtility.SetDirty(go);
			var status = current == newIndex ? "moved" : "partial";
			return BuildComponentResponse(go, component, oldIndex, current, count, status);
		}

		private static object BuildComponentResponse(GameObject go, Component c, int oldIndex, int newIndex, int total, string status)
		{
			var canonical = PathResolver.GetCanonicalPath(go) + ":" + c.GetType().Name;
			var data = new Dictionary<string, object>
			{
				["path"] = canonical,
				["mode"] = "component",
				["from"] = oldIndex,
				["to"] = newIndex,
				["componentCount"] = total,
				["status"] = status,
			};
			return new SuccessResponse(canonical, data);
		}

		// --- op resolution ---

		private enum OpKind { Index, First, Last, Up, Down, Before, After }

		private struct Op
		{
			public OpKind Kind;
			public int Steps;
			public int Index;
			public string Name;
		}

		private static Result<Op> ResolveOp(ToolParams p)
		{
			var hasIndex = HasValue(p, "index");
			var first = p.GetBool("first");
			var last = p.GetBool("last");
			var hasUp = HasValue(p, "up");
			var hasDown = HasValue(p, "down");
			var before = p.Get("before");
			var after = p.Get("after");

			var count = 0;
			if (hasIndex) count++;
			if (first) count++;
			if (last) count++;
			if (hasUp) count++;
			if (hasDown) count++;
			if (!string.IsNullOrEmpty(before)) count++;
			if (!string.IsNullOrEmpty(after)) count++;

			if (count == 0)
				return Result<Op>.Error("reorder requires one of --index, --first, --last, --up, --down, --before, --after.");
			if (count > 1)
				return Result<Op>.Error("reorder operations are mutually exclusive — pick one of --index/--first/--last/--up/--down/--before/--after.");

			if (hasIndex)
			{
				var n = p.GetInt("index") ?? 0;
				return Result<Op>.Success(new Op { Kind = OpKind.Index, Index = n });
			}
			if (first) return Result<Op>.Success(new Op { Kind = OpKind.First });
			if (last) return Result<Op>.Success(new Op { Kind = OpKind.Last });
			if (hasUp)
			{
				var steps = ResolveStepCount(p, "up");
				return Result<Op>.Success(new Op { Kind = OpKind.Up, Steps = steps });
			}
			if (hasDown)
			{
				var steps = ResolveStepCount(p, "down");
				return Result<Op>.Success(new Op { Kind = OpKind.Down, Steps = steps });
			}
			if (!string.IsNullOrEmpty(before))
				return Result<Op>.Success(new Op { Kind = OpKind.Before, Name = before });
			return Result<Op>.Success(new Op { Kind = OpKind.After, Name = after });
		}

		private static bool HasValue(ToolParams p, string key)
		{
			var raw = p.GetRaw(key);
			if (raw == null || raw.Type == JTokenType.Null) return false;
			// Bare "--up" sets the value to "true" via buildParams. Treat that
			// as "present, defaults to 1".
			return true;
		}

		private static int ResolveStepCount(ToolParams p, string key)
		{
			var raw = p.GetRaw(key);
			if (raw == null) return 1;
			if (raw.Type == JTokenType.Boolean) return 1;
			var asInt = p.GetInt(key);
			return asInt.HasValue && asInt.Value > 0 ? asInt.Value : 1;
		}

		private static Result<int> ResolveTargetIndex(Op op, int oldIndex, int count, System.Func<string, int> findByName)
		{
			switch (op.Kind)
			{
				case OpKind.Index:
					return Result<int>.Success(Clamp(op.Index, 0, count - 1));
				case OpKind.First:
					return Result<int>.Success(0);
				case OpKind.Last:
					return Result<int>.Success(count - 1);
				case OpKind.Up:
					return Result<int>.Success(Clamp(oldIndex - op.Steps, 0, count - 1));
				case OpKind.Down:
					return Result<int>.Success(Clamp(oldIndex + op.Steps, 0, count - 1));
				case OpKind.Before:
				{
					var idx = findByName(op.Name);
					if (idx < 0) return Result<int>.Error($"No sibling/component named '{op.Name}'.");
					if (idx == oldIndex) return Result<int>.Error($"Cannot place '{op.Name}' before itself.");
					// Inserting before idx means the new index is idx (if moving up)
					// or idx-1 (if moving down past it).
					return Result<int>.Success(idx > oldIndex ? idx - 1 : idx);
				}
				case OpKind.After:
				{
					var idx = findByName(op.Name);
					if (idx < 0) return Result<int>.Error($"No sibling/component named '{op.Name}'.");
					if (idx == oldIndex) return Result<int>.Error($"Cannot place '{op.Name}' after itself.");
					return Result<int>.Success(idx > oldIndex ? idx : idx + 1);
				}
				default:
					return Result<int>.Error("Unknown operation.");
			}
		}

		private static int Clamp(int v, int lo, int hi)
		{
			if (hi < lo) return lo;
			if (v < lo) return lo;
			if (v > hi) return hi;
			return v;
		}

		private static int FindIndexByName(List<GameObject> siblings, string name)
		{
			for (var i = 0; i < siblings.Count; i++)
				if (siblings[i] != null && siblings[i].name == name) return i;
			return -1;
		}

		private static int FindComponentIndexByName(Component[] components, string name)
		{
			for (var i = 0; i < components.Length; i++)
			{
				if (components[i] == null) continue;
				if (components[i].GetType().Name == name) return i;
			}
			return -1;
		}
	}
}
