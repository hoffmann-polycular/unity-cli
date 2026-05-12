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
	/// Writes a single serialized-property value through a
	/// <see cref="SerializedObject"/> (so prefab overrides register and
	/// Undo works like the Inspector does it).
	///
	/// v3: fan-out is the default. With a multi-target path the same value
	/// is broadcast to every resolved object, all writes share one Undo
	/// group, and per-target failures are reported but do not stop other
	/// writes. Value input is permissive: plain strings ("1 2 3", "#ff0000",
	/// "MyEnum"), JSON numbers/bools/objects (<c>--params '{"value":{"x":1,"y":2}}'</c>),
	/// component/asset references by path or instance ID, or <c>null</c>/<c>none</c>
	/// to clear an object reference.
	///
	/// Also supports <c>ProjectSettings/&lt;Group&gt;.&lt;property&gt;</c>.
	/// </summary>
	[UnityCliTool(Name = "set",
		Description = "Write a single property value. Registers Undo and dirties the target.")]
	public static class Set
	{
		public class Parameters
		{
			[ToolParameter("Path to a property, e.g. ':Transform.position.x' (selection-relative) or '/World/Player:Rigidbody.mass'.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Value to assign. Scalars, \"x y z\" / \"x,y,z\" vectors, " +
				"\"#rrggbb\" colors, enum names, object refs (\"Assets/...\" / \"#id\" / scene path), " +
				"or null/none to clear.", Required = true)]
			public string Value { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);

			// Positional form: `set <path> <value>` → args = [path, value]
			var args = p.GetRaw("args") as JArray;
			var path = p.Get("path") ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			// Value can arrive as --value <scalar>, --params '{"value":...}', or
			// as the second positional. JToken preserves full JSON shape.
			var rawValue = p.GetRaw("value");
			if ((rawValue == null || rawValue.Type == JTokenType.Null) && args != null && args.Count > 1)
				rawValue = args[1];

			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("set requires a path with :Component.property.");
			if (rawValue == null)
				return new ErrorResponse("set requires --value (or a second positional argument; piping is also accepted).");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
			var parsed = parseResult.Value;

			// ProjectSettings backend.
			if (parsed.Kind == PathKind.ProjectSettings)
				return SetProjectSettings(parsed, rawValue);

			if (!parsed.Component.IsPresent)
				return new ErrorResponse("set requires a component — add ':TypeName' to the path.");
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse("set requires a property — add '.propertyName' to the path.");

			// v3: fan out across selection (or resolve single absolute target).
			var targetsRes = PathResolver.ResolveTargets(parsed);
			if (!targetsRes.IsSuccess) return ErrorResponse.FromResult(targetsRes);
			var targets = targetsRes.Value;

			var applied = new List<object>();
			var errors = new List<string>();

			// Wrap the entire fan-out in a single Undo group so one Ctrl-Z
			// reverses the whole operation.
			var undoGroup = Undo.GetCurrentGroup();
			Undo.IncrementCurrentGroup();
			Undo.SetCurrentGroupName($"set {parsed.Component.TypeName}.{Join(parsed.Properties, parsed.Properties.Count)}");

			foreach (var go in targets)
			{
				// :GameObject pseudo-component — bypass SerializedObject entirely.
				if (GameObjectProxy.Is(parsed.Component.TypeName))
				{
					var propName = parsed.Properties[0];
					var setRes = GameObjectProxy.Set(go, propName, rawValue);
					if (!setRes.IsSuccess)
					{
						errors.Add($"{PathResolver.GetCanonicalPath(go)}: {setRes.ErrorMessage}");
						continue;
					}
					applied.Add(setRes.Value);
					continue;
				}

				var compResult = PathResolver.ResolveComponent(go, parsed.Component);
				if (!compResult.IsSuccess) { errors.Add($"{PathResolver.GetCanonicalPath(go)}: {compResult.ErrorMessage}"); continue; }
				var component = compResult.Value;

				using var so = new SerializedObject(component);
				var root = PathResolver.FindPropertyByUserName(so, parsed.Properties[0]);
				if (root == null)
				{
					errors.Add($"{PathResolver.GetCanonicalPath(go)}: no property '{parsed.Properties[0]}' on {component.GetType().Name}.");
					continue;
				}

				var current = root;
				var failedProp = false;
				for (var i = 1; i < parsed.Properties.Count; i++)
				{
					var next = PathResolver.FindRelativeByUserName(current, parsed.Properties[i]);
					if (next == null)
					{
						errors.Add($"{PathResolver.GetCanonicalPath(go)}: no sub-property '{parsed.Properties[i]}' under '{Join(parsed.Properties, i)}'.");
						failedProp = true;
						break;
					}
					current = next;
				}
				if (failedProp) continue;

				var oldValue = SerializedPropertyReader.Read(current);

				Undo.RecordObject(component, $"set {component.GetType().Name}.{Join(parsed.Properties, parsed.Properties.Count)}");

				var writeResult = SerializedPropertyWriter.Write(current, rawValue);
				if (!writeResult.IsSuccess) { errors.Add($"{PathResolver.GetCanonicalPath(go)}: {writeResult.ErrorMessage}"); continue; }

				so.ApplyModifiedProperties();
				EditorUtility.SetDirty(component);

				var newValue = SerializedPropertyReader.Read(current);

				applied.Add(new Dictionary<string, object>
				{
					["path"] = PathResolver.GetCanonicalPath(go),
					["component"] = component.GetType().Name,
					["property"] = Join(parsed.Properties, parsed.Properties.Count),
					["type"] = current.propertyType.ToString(),
					["oldValue"] = oldValue,
					["newValue"] = newValue,
					["override"] = current.prefabOverride,
				});
			}

			Undo.CollapseUndoOperations(undoGroup);

			if (applied.Count == 0)
				return new ErrorResponse(
					errors.Count == 1 ? errors[0] : $"set failed for all {targets.Count} target(s):\n  " + string.Join("\n  ", errors));

			// Single-target keeps the simple shape; multi-target wraps the
			// list so callers can iterate. Mixed success/failure surfaces as
			// SuccessResponse with a partial-failure message.
			if (applied.Count == 1 && targets.Count == 1)
			{
				var single = (Dictionary<string, object>)applied[0];
				return new SuccessResponse(
					$"{single["path"]}:{single["component"]}.{single["property"]} = {Describe(single["newValue"])}",
					single);
			}

			var message = errors.Count == 0
				? $"set applied to {applied.Count} object(s)."
				: $"set applied to {applied.Count} object(s); {errors.Count} failed.";

			return new SuccessResponse(message, new Dictionary<string, object>
			{
				["applied"] = applied,
				["errors"] = errors,
			});
		}

		private static object SetProjectSettings(ParsedPath parsed, JToken rawValue)
		{
			if (!parsed.Component.IsPresent || parsed.Component.TypeName != PathParser.SettingsRootSentinel
				|| parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse(
					"set requires a property under a ProjectSettings group, e.g. 'ProjectSettings/Physics.gravity'.",
					ErrorKind.Usage);

			var soRes = ProjectSettingsResolver.LoadGroup(parsed.SettingsGroup);
			if (!soRes.IsSuccess) return ErrorResponse.FromResult(soRes);
			using var so = soRes.Value;

			var root = PathResolver.FindPropertyByUserName(so, parsed.Properties[0]);
			if (root == null)
				return new ErrorResponse(
					$"No property '{parsed.Properties[0]}' on ProjectSettings/{parsed.SettingsGroup}.",
					ErrorKind.NotFound);
			var current = root;
			for (var i = 1; i < parsed.Properties.Count; i++)
			{
				var next = PathResolver.FindRelativeByUserName(current, parsed.Properties[i]);
				if (next == null)
					return new ErrorResponse(
						$"No sub-property '{parsed.Properties[i]}' under '{Join(parsed.Properties, i)}'.",
						ErrorKind.NotFound);
				current = next;
			}

			var oldValue = SerializedPropertyReader.Read(current);
			Undo.RecordObject(so.targetObject, $"set ProjectSettings/{parsed.SettingsGroup}.{Join(parsed.Properties, parsed.Properties.Count)}");

			var writeResult = SerializedPropertyWriter.Write(current, rawValue);
			if (!writeResult.IsSuccess) return ErrorResponse.FromResult(writeResult);

			so.ApplyModifiedProperties();
			EditorUtility.SetDirty(so.targetObject);
			AssetDatabase.SaveAssets();

			var newValue = SerializedPropertyReader.Read(current);
			return new SuccessResponse(
				$"{ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup)}.{Join(parsed.Properties, parsed.Properties.Count)} = {Describe(newValue)}",
				new Dictionary<string, object>
				{
					["path"] = ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup),
					["property"] = Join(parsed.Properties, parsed.Properties.Count),
					["type"] = current.propertyType.ToString(),
					["oldValue"] = oldValue,
					["newValue"] = newValue,
				});
		}

		private static string Join(List<string> parts, int count)
		{
			var sb = new System.Text.StringBuilder();
			for (var i = 0; i < count; i++)
			{
				if (i > 0) sb.Append('.');
				sb.Append(parts[i]);
			}
			return sb.ToString();
		}

		private static string Describe(object value)
		{
			if (value == null) return "null";
			if (value is Dictionary<string, object> dict)
			{
				var sb = new System.Text.StringBuilder("{");
				var first = true;
				foreach (var kv in dict)
				{
					if (!first) sb.Append(", ");
					first = false;
					sb.Append(kv.Key).Append('=').Append(Describe(kv.Value));
				}
				sb.Append('}');
				return sb.ToString();
			}
			if (value is List<object> list)
				return $"[{list.Count}]";
			return value.ToString();
		}
	}
}
