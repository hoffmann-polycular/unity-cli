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
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Dumps the Inspector view of whatever a path points at:
	/// GameObject + component list, a single component's serialized
	/// properties, or one property value.
	///
	/// v3: paths fan out across the current selection. <c>inspect</c> with
	/// a multi-selection emits one block per target. <c>--overrides-only</c>
	/// trims to properties whose value differs from the prefab source.
	///
	/// Also handles ProjectSettings paths.
	/// </summary>
	[UnityCliTool(Name = "inspect",
		Description = "Inspect a GameObject, Component, or property. Mirrors the Inspector view.")]
	public static class Inspect
	{
		public class Parameters
		{
			[ToolParameter("Path to a GameObject, Component, property, or ProjectSettings group.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Only show values overridden from the prefab source.")]
			public bool OverridesOnly { get; set; }

			[ToolParameter("Output format: human (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var pathParam = p.Get("path");
			var overridesOnly = p.GetBool("overrides_only");
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			// Multi-path mode: args has more than one entry → each is an
			// independent path, all results stream out in input order.
			if (string.IsNullOrWhiteSpace(pathParam) && args != null && args.Count > 1)
				return InspectMulti(args, overridesOnly, format);

			var path = pathParam ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			// Empty path → default to the current selection ("." semantics).
			// Mirrors how `ls` and `select --get` behave; consistent with `get`
			// fan-out on the selection.
			if (string.IsNullOrWhiteSpace(path))
				path = ".";

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
			var parsed = parseResult.Value;

			// ProjectSettings has its own backend.
			if (parsed.Kind == PathKind.ProjectSettings)
				return InspectProjectSettings(parsed, format);

			// Asset importer access. `:Importer` (alias) or a concrete subclass
			// name → dump the importer's SerializedObject (or a single property).
			if (parsed.Kind == PathKind.Asset && PathResolver.IsImporterComponent(parsed.Component))
				return InspectImporter(parsed, format);

			// All other kinds resolve to GameObject targets via the v3 fan-out resolver.
			var targetsRes = PathResolver.ResolveTargets(parsed);
			if (!targetsRes.IsSuccess) return ErrorResponse.FromResult(targetsRes);
			var targets = targetsRes.Value;

			// Single target → simple shape (back-compat with single-target callers).
			if (targets.Count == 1)
				return RenderTarget(targets[0], parsed, overridesOnly, format);

			// Multi-target fan-out → array of per-target results.
			var results = new List<object>(targets.Count);
			foreach (var go in targets)
			{
				var single = RenderTarget(go, parsed, overridesOnly, format);
				results.Add(WrapForFanOut(go, single, format));
			}

			if (format == "json")
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["count"] = targets.Count,
					["results"] = results,
				});
			return new SuccessResponse("", JoinHumanBlocks(results));
		}

		// Multi-path entry point: iterate each input path independently and
		// stream results. Each path resolves to its own target set (which may
		// itself fan out via selection); all results concatenate in input
		// order. Per-path parse / resolve failures are routed to stderr without
		// halting other paths.
		private static object InspectMulti(JArray paths, bool overridesOnly, string format)
		{
			var results = new List<object>();
			var errorLines = new List<string>();
			foreach (var pathTok in paths)
			{
				var pathStr = pathTok?.ToString();
				if (string.IsNullOrWhiteSpace(pathStr)) continue;

				var parseRes = PathParser.Parse(pathStr);
				if (!parseRes.IsSuccess)
				{
					errorLines.Add($"{pathStr}: {parseRes.ErrorMessage}");
					results.Add(new Dictionary<string, object>
					{
						["path"] = pathStr, ["ok"] = false, ["error"] = parseRes.ErrorMessage,
					});
					continue;
				}
				var parsed = parseRes.Value;

				if (parsed.Kind == PathKind.ProjectSettings)
				{
					var ps = InspectProjectSettings(parsed, format);
					results.Add(WrapForFanOut(go: null, ps, format, fallbackPath: ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup)));
					continue;
				}

				// Asset importer per-path.
				if (parsed.Kind == PathKind.Asset && PathResolver.IsImporterComponent(parsed.Component))
				{
					var imp = InspectImporter(parsed, format);
					results.Add(WrapForFanOut(go: null, imp, format, fallbackPath: parsed.AssetPath));
					continue;
				}

				var targetsRes = PathResolver.ResolveTargets(parsed);
				if (!targetsRes.IsSuccess)
				{
					errorLines.Add($"{pathStr}: {targetsRes.ErrorMessage}");
					results.Add(new Dictionary<string, object>
					{
						["path"] = pathStr, ["ok"] = false, ["error"] = targetsRes.ErrorMessage,
					});
					continue;
				}

				foreach (var go in targetsRes.Value)
				{
					var single = RenderTarget(go, parsed, overridesOnly, format);
					results.Add(WrapForFanOut(go, single, format));
				}
			}

			if (format == "json")
			{
				var resp = new SuccessResponse("", new Dictionary<string, object>
				{
					["count"] = results.Count,
					["results"] = results,
				});
				if (errorLines.Count > 0)
				{
					resp.partialFailure = true;
					resp.stderr = string.Join("\n", errorLines);
				}
				return resp;
			}

			var humanResp = new SuccessResponse("", JoinHumanBlocks(results));
			if (errorLines.Count > 0)
			{
				humanResp.partialFailure = true;
				humanResp.stderr = string.Join("\n", errorLines);
			}
			return humanResp;
		}

		private static object RenderTarget(GameObject go, ParsedPath parsed, bool overridesOnly, string format)
		{
			if (!parsed.Component.IsPresent)
				return RenderGameObject(go, overridesOnly, format);

			// :GameObject pseudo-component — virtual fields, no SerializedObject.
			if (GameObjectProxy.Is(parsed.Component.TypeName))
				return RenderGameObjectProxy(go, parsed, format);

			var compResult = PathResolver.ResolveComponent(go, parsed.Component);
			if (!compResult.IsSuccess) return ErrorResponse.FromResult(compResult);
			var component = compResult.Value;

			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return RenderComponent(go, component, overridesOnly, format);

			using var so = new SerializedObject(component);
			var root = PathResolver.FindPropertyByUserName(so, parsed.Properties[0]);
			if (root == null)
				return new ErrorResponse(
					$"No property '{parsed.Properties[0]}' on {component.GetType().Name}.");

			var current = root;
			for (var i = 1; i < parsed.Properties.Count; i++)
			{
				var next = PathResolver.FindRelativeByUserName(current, parsed.Properties[i]);
				if (next == null)
					return new ErrorResponse(
						$"No sub-property '{parsed.Properties[i]}' under '{JoinProps(parsed.Properties, i)}'.");
				current = next;
			}

			return RenderProperty(go, component, parsed.Properties, current, format);
		}

		private static object WrapForFanOut(GameObject go, object renderResult, string format, string fallbackPath = null)
		{
			// Renderers return SuccessResponse / ErrorResponse; pull out the
			// data (or error message) so the wrapping array carries clean
			// per-target records.
			var canonical = go != null ? PathResolver.GetCanonicalPath(go) : (fallbackPath ?? "");
			switch (renderResult)
			{
				case SuccessResponse sr:
					return new Dictionary<string, object>
					{
						["path"] = canonical,
						["ok"] = true,
						["data"] = sr.data,
					};
				case ErrorResponse er:
					return new Dictionary<string, object>
					{
						["path"] = canonical,
						["ok"] = false,
						["error"] = er.message,
					};
				default:
					return new Dictionary<string, object>
					{
						["path"] = canonical,
						["data"] = renderResult,
					};
			}
		}

		private static string JoinHumanBlocks(List<object> entries)
		{
			var sb = new StringBuilder();
			var first = true;
			foreach (var entry in entries)
			{
				if (!(entry is Dictionary<string, object> dict)) continue;
				if (!first) sb.Append("\n\n");
				first = false;
				if (dict.TryGetValue("ok", out var okObj) && okObj is bool ok && !ok)
				{
					sb.Append("# ").Append(dict["path"]).Append('\n')
					  .Append("error: ").Append(dict["error"]);
					continue;
				}
				if (dict.TryGetValue("data", out var data) && data is string s)
				{
					sb.Append(s);
				}
			}
			return sb.ToString();
		}

		// ---- ProjectSettings view ----

		// ---- Asset importer view ----

		private static object InspectImporter(ParsedPath parsed, string format)
		{
			var impRes = PathResolver.ResolveAssetImporter(parsed);
			if (!impRes.IsSuccess) return ErrorResponse.FromResult(impRes);
			var importer = impRes.Value;

			using var so = new SerializedObject(importer);

			// With a property suffix → render the single property.
			if (parsed.Properties != null && parsed.Properties.Count > 0)
			{
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
							$"No sub-property '{parsed.Properties[i]}' under '{JoinProps(parsed.Properties, i)}'.",
							ErrorKind.NotFound);
					current = next;
				}
				var value = SerializedPropertyReader.Read(current);
				if (format == "json")
					return new SuccessResponse("", new Dictionary<string, object>
					{
						["path"] = parsed.AssetPath,
						["component"] = importer.GetType().Name,
						["property"] = JoinProps(parsed.Properties, parsed.Properties.Count),
						["type"] = current.propertyType.ToString(),
						["value"] = value,
					});
				var sb = new StringBuilder();
				sb.Append(parsed.AssetPath).Append(':').Append(importer.GetType().Name)
				  .Append('.').Append(JoinProps(parsed.Properties, parsed.Properties.Count)).Append('\n');
				AppendValueHuman(value, sb, depth: 1);
				return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
			}

			// No property → dump all importer properties.
			var props = SerializedPropertyReader.ReadAll(so, overridesOnly: false);
			if (format == "json")
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = parsed.AssetPath,
					["component"] = importer.GetType().Name,
					["properties"] = props,
				});
			var hsb = new StringBuilder();
			hsb.Append(parsed.AssetPath).Append(':').Append(importer.GetType().Name).Append('\n');
			AppendPropsHuman(props, hsb, depth: 1);
			return new SuccessResponse("", hsb.ToString().TrimEnd('\n'));
		}

		private static object InspectProjectSettings(ParsedPath parsed, string format)
		{
			if (string.IsNullOrEmpty(parsed.SettingsGroup))
			{
				// "ProjectSettings/" or "ProjectSettings" — list groups.
				var groups = ProjectSettingsResolver.ListGroups();
				if (format == "json")
					return new SuccessResponse("", new Dictionary<string, object>
					{
						["path"] = "ProjectSettings",
						["groups"] = groups,
					});
				var sb = new StringBuilder("ProjectSettings\n");
				foreach (var g in groups) sb.Append("  ").Append(g).Append('\n');
				return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
			}

			var soRes = ProjectSettingsResolver.LoadGroup(parsed.SettingsGroup);
			if (!soRes.IsSuccess) return ErrorResponse.FromResult(soRes);
			using var so = soRes.Value;

			// Property drilling under settings (e.g. "ProjectSettings/Physics.gravity").
			// The parser maps that to Component=__settings, Properties=["gravity"].
			if (parsed.Component.IsPresent && parsed.Component.TypeName == PathParser.SettingsRootSentinel
				&& parsed.Properties != null && parsed.Properties.Count > 0)
			{
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
							$"No sub-property '{parsed.Properties[i]}' under '{JoinProps(parsed.Properties, i)}'.",
							ErrorKind.NotFound);
					current = next;
				}
				var value = SerializedPropertyReader.Read(current);
				if (format == "json")
					return new SuccessResponse("", new Dictionary<string, object>
					{
						["path"] = ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup),
						["property"] = JoinProps(parsed.Properties, parsed.Properties.Count),
						["type"] = current.propertyType.ToString(),
						["value"] = value,
					});
				var sb = new StringBuilder();
				sb.Append(ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup))
				  .Append('.').Append(JoinProps(parsed.Properties, parsed.Properties.Count)).Append('\n');
				AppendValueHuman(value, sb, depth: 1);
				return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
			}

			// Group root → dump all top-level properties.
			var props = SerializedPropertyReader.ReadAll(so, overridesOnly: false);
			if (format == "json")
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup),
					["properties"] = props,
				});
			var hsb = new StringBuilder();
			hsb.Append(ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup)).Append('\n');
			AppendPropsHuman(props, hsb, depth: 1);
			return new SuccessResponse("", hsb.ToString().TrimEnd('\n'));
		}

		// ---- :GameObject pseudo-component view ----

		private static object RenderGameObjectProxy(GameObject go, ParsedPath parsed, string format)
		{
			// Property drilling: inspect Player:GameObject.activeSelf
			if (parsed.Properties != null && parsed.Properties.Count > 0)
			{
				var propName = parsed.Properties[0];
				var res = GameObjectProxy.Get(go, propName);
				if (!res.IsSuccess) return ErrorResponse.FromResult(res);
				var value = res.Value;

				if (format == "json")
					return new SuccessResponse("", new Dictionary<string, object>
					{
						["path"]      = PathResolver.GetCanonicalPath(go),
						["component"] = GameObjectProxy.PseudoTypeName,
						["property"]  = propName,
						["type"]      = "GameObjectProperty",
						["value"]     = value,
					});

				var sb = new StringBuilder();
				sb.Append(PathResolver.GetCanonicalPath(go))
				  .Append(":GameObject.").Append(propName).Append('\n');
				AppendValueHuman(value, sb, depth: 1);
				return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
			}

			// Component-level view: inspect Player:GameObject
			var props = GameObjectProxy.InspectAll(go);

			if (format == "json")
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"]       = PathResolver.GetCanonicalPath(go),
					["component"]  = GameObjectProxy.PseudoTypeName,
					["properties"] = props,
				});

			var hsb = new StringBuilder();
			hsb.Append(PathResolver.GetCanonicalPath(go)).Append(":GameObject\n");
			foreach (var kv in props)
				hsb.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
			return new SuccessResponse("", hsb.ToString().TrimEnd('\n'));
		}

		// ---- GameObject view ----

		private static object RenderGameObject(GameObject go, bool overridesOnly, string format)
		{
			var components = go.GetComponents<Component>();
			var data = new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(go),
				["name"] = go.name,
				["active"] = go.activeInHierarchy,
				["activeSelf"] = go.activeSelf,
				["tag"] = go.tag,
				["layer"] = LayerMask.LayerToName(go.layer),
				["isStatic"] = go.isStatic,
				["instanceId"] = go.GetInstanceID(),
			};

			var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			if (prefabRoot != null)
			{
				var source = PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
				if (source != null)
				{
					data["prefab"] = new Dictionary<string, object>
					{
						["asset"] = AssetDatabase.GetAssetPath(source),
						["isRoot"] = prefabRoot == go,
						["hasOverrides"] = prefabRoot == go
							&& PrefabUtility.HasPrefabInstanceAnyOverrides(prefabRoot, includeDefaultOverrides: false),
					};
				}
			}

			var compList = new List<object>(components.Length);
			foreach (var c in components)
			{
				if (c == null)
				{
					compList.Add(new Dictionary<string, object>
					{
						["type"] = "<missing script>",
					});
					continue;
				}
				using var so = new SerializedObject(c);
				compList.Add(new Dictionary<string, object>
				{
					["type"] = c.GetType().Name,
					["enabled"] = IsEnabled(c),
					["properties"] = SerializedPropertyReader.ReadAll(so, overridesOnly),
				});
			}
			data["components"] = compList;

			if (format == "json")
				return new SuccessResponse("", data);
			return new SuccessResponse("", RenderGameObjectHuman(data));
		}

		private static string RenderGameObjectHuman(Dictionary<string, object> data)
		{
			var sb = new StringBuilder();
			sb.Append(data["path"]).Append('\n');
			sb.Append("  active: ").Append(data["active"]);
			sb.Append("  tag: ").Append(data["tag"]);
			sb.Append("  layer: ").Append(data["layer"]).Append('\n');

			if (data.TryGetValue("prefab", out var prefabObj) && prefabObj is Dictionary<string, object> prefab)
			{
				sb.Append("  prefab: ").Append(prefab["asset"]);
				if (prefab.TryGetValue("hasOverrides", out var ho) && ho is bool b && b)
					sb.Append("  (overrides)");
				sb.Append('\n');
			}

			if (data["components"] is List<object> comps)
			{
				foreach (var entry in comps)
				{
					if (entry is not Dictionary<string, object> dict) continue;
					sb.Append("  ").Append(dict["type"]);
					if (dict.TryGetValue("enabled", out var en) && en is bool eb && !eb)
						sb.Append("  (disabled)");
					sb.Append('\n');
					if (dict.TryGetValue("properties", out var pObj)
						&& pObj is Dictionary<string, object> props)
					{
						AppendPropsHuman(props, sb, depth: 2);
					}
				}
			}

			return sb.ToString().TrimEnd('\n');
		}

		// ---- Component view ----

		private static object RenderComponent(GameObject go, Component c, bool overridesOnly, string format)
		{
			if (c == null) return new ErrorResponse("Component is null.");
			using var so = new SerializedObject(c);
			var props = SerializedPropertyReader.ReadAll(so, overridesOnly);

			var data = new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(go),
				["component"] = c.GetType().Name,
				["enabled"] = IsEnabled(c),
				["instanceId"] = c.GetInstanceID(),
				["properties"] = props,
			};

			if (format == "json")
				return new SuccessResponse("", data);

			var sb = new StringBuilder();
			sb.Append(data["path"]).Append(':').Append(data["component"]);
			if (data["enabled"] is bool eb && !eb) sb.Append("  (disabled)");
			sb.Append('\n');
			AppendPropsHuman(props, sb, depth: 1);
			if (props.Count == 0) sb.Append(overridesOnly ? "  (no overrides)\n" : "  (no properties)\n");
			return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
		}

		// ---- Property view ----

		private static object RenderProperty(
			GameObject go, Component c, List<string> propPath,
			SerializedProperty prop, string format)
		{
			var value = SerializedPropertyReader.Read(prop);
			var joined = JoinProps(propPath, propPath.Count);

			if (format == "json")
			{
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = PathResolver.GetCanonicalPath(go),
					["component"] = c.GetType().Name,
					["property"] = joined,
					["type"] = prop.propertyType.ToString(),
					["override"] = prop.prefabOverride,
					["value"] = value,
				});
			}

			var sb = new StringBuilder();
			sb.Append(PathResolver.GetCanonicalPath(go))
			  .Append(':').Append(c.GetType().Name).Append('.').Append(joined);
			if (prop.prefabOverride) sb.Append("  (override)");
			sb.Append('\n');
			AppendValueHuman(value, sb, depth: 1);
			return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
		}

		// ---- human-render helpers ----

		private static void AppendPropsHuman(Dictionary<string, object> props, StringBuilder sb, int depth)
		{
			foreach (var kv in props)
			{
				sb.Append(' ', depth * 2).Append(kv.Key).Append(": ");
				AppendValueInline(kv.Value, sb, depth);
				sb.Append('\n');
			}
		}

		private static void AppendValueInline(object value, StringBuilder sb, int depth)
		{
			switch (value)
			{
				case null:
					sb.Append("null");
					return;
				case string s:
					sb.Append(s);
					return;
				case bool b:
					sb.Append(b ? "true" : "false");
					return;
				case Dictionary<string, object> dict:
					if (LooksLikeVector(dict))
					{
						sb.Append(FormatVectorLike(dict));
						return;
					}
					sb.Append('\n');
					AppendPropsHuman(dict, sb, depth + 1);
					if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
					return;
				case List<object> list:
					if (list.Count == 0) { sb.Append("[]"); return; }
					sb.Append('[').Append(list.Count).Append(']');
					sb.Append('\n');
					for (var i = 0; i < list.Count; i++)
					{
						sb.Append(' ', (depth + 1) * 2).Append('[').Append(i).Append("] ");
						AppendValueInline(list[i], sb, depth + 1);
						sb.Append('\n');
					}
					if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
					return;
				default:
					sb.Append(value);
					return;
			}
		}

		private static void AppendValueHuman(object value, StringBuilder sb, int depth)
		{
			sb.Append(' ', depth * 2);
			AppendValueInline(value, sb, depth);
		}

		private static bool LooksLikeVector(Dictionary<string, object> dict)
		{
			if (dict.Count is < 2 or > 4) return false;
			foreach (var k in dict.Keys)
				if (k != "x" && k != "y" && k != "z" && k != "w"
					&& k != "r" && k != "g" && k != "b" && k != "a")
					return false;
			return true;
		}

		private static string FormatVectorLike(Dictionary<string, object> dict)
		{
			var sb = new StringBuilder("(");
			var first = true;
			foreach (var kv in dict)
			{
				if (!first) sb.Append(", ");
				first = false;
				sb.Append(kv.Key).Append('=').Append(kv.Value);
			}
			sb.Append(')');
			return sb.ToString();
		}

		private static string JoinProps(List<string> parts, int count)
		{
			var sb = new StringBuilder();
			for (var i = 0; i < count; i++)
			{
				if (i > 0) sb.Append('.');
				sb.Append(parts[i]);
			}
			return sb.ToString();
		}

		private static bool IsEnabled(Component c)
		{
			if (c is Behaviour beh) return beh.enabled;
			if (c is Renderer ren) return ren.enabled;
			if (c is Collider col) return col.enabled;
			return true;
		}
	}
}
