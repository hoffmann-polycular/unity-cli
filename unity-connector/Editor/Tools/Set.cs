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
			var args = p.GetRaw("args") as JArray;
			var pathParam = p.Get("path");
			var rawValue = p.GetRaw("value");

			// Multi-path form: args has ≥3 entries → args[0..N-2] are paths,
			// args[N-1] is the value. Iterates each path applying the same
			// value, all inside a single Undo group.
			if (string.IsNullOrWhiteSpace(pathParam) && args != null && args.Count >= 3)
			{
				JToken valueTok = (rawValue != null && rawValue.Type != JTokenType.Null)
					? rawValue
					: args[args.Count - 1];
				var pathList = new JArray();
				for (var i = 0; i < args.Count - 1; i++) pathList.Add(args[i]);
				return SetMulti(pathList, valueTok);
			}

			// Positional form: `set <path> <value>` → args = [path, value]
			var path = pathParam ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			// Value can arrive as --value <scalar>, --params '{"value":...}', or
			// as the second positional. JToken preserves full JSON shape.
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

			// Asset importer backend (e.g. Assets/Foo.png:TextureImporter.maxTextureSize).
			if (parsed.Kind == PathKind.Asset && PathResolver.IsImporterComponent(parsed.Component))
				return SetOnImporter(parsed, rawValue);

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

		// Multi-path entry: broadcast one value across N independent paths.
		// Each path may have its own :Component.property suffix (e.g. when
		// the Go side appended the same suffix to every piped path, all the
		// parsed components/properties will be identical, but that's fine —
		// we iterate per-path uniformly). All writes share one Undo group.
		private static object SetMulti(JArray paths, JToken rawValue)
		{
			if (rawValue == null)
				return new ErrorResponse("set requires a value.");

			var applied = new List<object>();
			var errors = new List<string>();
			var totalTargets = 0;

			var undoGroup = Undo.GetCurrentGroup();
			Undo.IncrementCurrentGroup();
			Undo.SetCurrentGroupName("set (multi)");

			// Defer + coalesce all asset imports so a batch of importer
			// writes triggers a single Unity import pass instead of one per
			// path. No-op when no importer paths are in the batch.
			AssetDatabase.StartAssetEditing();
			try
			{

				foreach (var pathTok in paths)
				{
					var pathStr = pathTok?.ToString();
					if (string.IsNullOrWhiteSpace(pathStr)) continue;

					var parseRes = PathParser.Parse(pathStr);
					if (!parseRes.IsSuccess)
					{
						errors.Add($"{pathStr}: {parseRes.ErrorMessage}");
						continue;
					}
					var parsed = parseRes.Value;

					if (parsed.Kind == PathKind.ProjectSettings)
					{
						errors.Add($"{pathStr}: ProjectSettings paths are not supported in multi-path mode.");
						continue;
					}

					// Asset importer per-path: write through SerializedObject(importer).
					// The whole batch is wrapped in StartAssetEditing / StopAssetEditing
					// at the call site so per-path reimports coalesce into one pass.
					// The Undo group still wraps property writes — reimport itself
					// is not undoable, only the property revert.
					if (parsed.Kind == PathKind.Asset && PathResolver.IsImporterComponent(parsed.Component))
					{
						totalTargets++;
						var setRes = SetOnImporter(parsed, rawValue);
						if (setRes is SuccessResponse impSr && impSr.data is Dictionary<string, object> impDict)
							applied.Add(impDict);
						else if (setRes is ErrorResponse impEr)
							errors.Add($"{pathStr}: {impEr.message}");
						continue;
					}

					if (!parsed.Component.IsPresent)
					{
						errors.Add($"{pathStr}: set requires a component — add ':TypeName' to the path.");
						continue;
					}
					if (parsed.Properties == null || parsed.Properties.Count == 0)
					{
						errors.Add($"{pathStr}: set requires a property — add '.propertyName' to the path.");
						continue;
					}

					var targetsRes = PathResolver.ResolveTargets(parsed);
					if (!targetsRes.IsSuccess)
					{
						errors.Add($"{pathStr}: {targetsRes.ErrorMessage}");
						continue;
					}

					foreach (var go in targetsRes.Value)
					{
						totalTargets++;
						ApplyToOne(go, parsed, rawValue, applied, errors);
					}
				}

			}
			finally
			{
				AssetDatabase.StopAssetEditing();
			}

			Undo.CollapseUndoOperations(undoGroup);

			if (applied.Count == 0)
				return new ErrorResponse(
					errors.Count == 1 ? errors[0] : $"set failed for all {totalTargets} target(s):\n  " + string.Join("\n  ", errors));

			var message = errors.Count == 0
				? $"set applied to {applied.Count} object(s)."
				: $"set applied to {applied.Count} object(s); {errors.Count} failed.";

			var resp = new SuccessResponse(message, new Dictionary<string, object>
			{
				["applied"] = applied,
				["errors"] = errors,
			});
			if (errors.Count > 0)
			{
				resp.partialFailure = true;
				resp.stderr = string.Join("\n", errors);
			}
			return resp;
		}

		// Apply rawValue to a single (parsed, GameObject) pair. Mirrors the
		// inner loop of HandleCommand's selection fan-out; pulled out so
		// SetMulti can share it without duplicating the SerializedObject /
		// property walk / Undo / write logic.
		private static void ApplyToOne(
			GameObject go, ParsedPath parsed, JToken rawValue,
			List<object> applied, List<string> errors)
		{
			// :GameObject pseudo-component → bypass SerializedObject.
			if (GameObjectProxy.Is(parsed.Component.TypeName))
			{
				var setRes = GameObjectProxy.Set(go, parsed.Properties[0], rawValue);
				if (!setRes.IsSuccess) { errors.Add($"{PathResolver.GetCanonicalPath(go)}: {setRes.ErrorMessage}"); return; }
				applied.Add(setRes.Value);
				return;
			}

			var compResult = PathResolver.ResolveComponent(go, parsed.Component);
			if (!compResult.IsSuccess) { errors.Add($"{PathResolver.GetCanonicalPath(go)}: {compResult.ErrorMessage}"); return; }
			var component = compResult.Value;

			using var so = new SerializedObject(component);
			var root = PathResolver.FindPropertyByUserName(so, parsed.Properties[0]);
			if (root == null)
			{
				errors.Add($"{PathResolver.GetCanonicalPath(go)}: no property '{parsed.Properties[0]}' on {component.GetType().Name}.");
				return;
			}

			var current = root;
			for (var i = 1; i < parsed.Properties.Count; i++)
			{
				var next = PathResolver.FindRelativeByUserName(current, parsed.Properties[i]);
				if (next == null)
				{
					errors.Add($"{PathResolver.GetCanonicalPath(go)}: no sub-property '{parsed.Properties[i]}' under '{Join(parsed.Properties, i)}'.");
					return;
				}
				current = next;
			}

			var oldValue = SerializedPropertyReader.Read(current);
			Undo.RecordObject(component, $"set {component.GetType().Name}.{Join(parsed.Properties, parsed.Properties.Count)}");

			var writeResult = SerializedPropertyWriter.Write(current, rawValue);
			if (!writeResult.IsSuccess) { errors.Add($"{PathResolver.GetCanonicalPath(go)}: {writeResult.ErrorMessage}"); return; }

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

		// Writes a property on the asset's AssetImporter.
		//
		// Commits the change to the .meta file via
		// AssetDatabase.WriteImportSettingsIfDirty. Unity's AssetDatabase
		// notices the meta change and re-imports the asset on its own — no
		// explicit ImportAsset call needed, and skipping it sidesteps the
		// "Unsaved Changes Detected" popup and the Inspector working-copy
		// conflict (silent alternating writes) that hit us when we used to
		// force a synchronous ImportAsset(ForceUpdate) on every set.
		private static object SetOnImporter(ParsedPath parsed, JToken rawValue)
		{
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse("set requires a property — add '.propertyName' to the path.");

			var impRes = PathResolver.ResolveAssetImporter(parsed);
			if (!impRes.IsSuccess) return ErrorResponse.FromResult(impRes);
			var importer = impRes.Value;

			using var so = new SerializedObject(importer);
			var root = PathResolver.FindPropertyByUserName(so, parsed.Properties[0]);
			if (root == null)
				return new ErrorResponse(
					$"No property '{parsed.Properties[0]}' on {importer.GetType().Name}.",
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

			Undo.RecordObject(importer, $"set {importer.GetType().Name}.{Join(parsed.Properties, parsed.Properties.Count)}");
			var writeResult = SerializedPropertyWriter.Write(current, rawValue);
			if (!writeResult.IsSuccess) return ErrorResponse.FromResult(writeResult);

			so.ApplyModifiedProperties();
			EditorUtility.SetDirty(importer);

			var newValue = SerializedPropertyReader.Read(current);
			var propertyType = current.propertyType.ToString();

			// Commit the meta file. We do NOT call ImportAsset — see comment
			// on the method.
			AssetDatabase.WriteImportSettingsIfDirty(parsed.AssetPath);

			return new SuccessResponse(
				$"{parsed.AssetPath}:{importer.GetType().Name}.{Join(parsed.Properties, parsed.Properties.Count)} = {Describe(newValue)}",
				new Dictionary<string, object>
				{
					["path"] = parsed.AssetPath,
					["component"] = importer.GetType().Name,
					["property"] = Join(parsed.Properties, parsed.Properties.Count),
					["type"] = propertyType,
					["oldValue"] = oldValue,
					["newValue"] = newValue,
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
