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
	/// <c>--overrides-only</c> trims to properties whose value differs
	/// from the prefab source — the same red-bar state the Inspector shows.
	/// </summary>
	[UnityCliTool(Name = "inspect",
		Description = "Inspect a GameObject, Component, or property. Mirrors the Inspector view.")]
	public static class Inspect
	{
		public class Parameters
		{
			[ToolParameter("Path to a GameObject, Component, or property.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("Only show values overridden from the prefab source.")]
			public bool OverridesOnly { get; set; }

			[ToolParameter("Output format: human (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var path = p.Get("path")
					   ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			var overridesOnly = p.GetBool("overrides_only");
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("inspect requires a path (GameObject, Component, or property).");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return new ErrorResponse(parseResult.ErrorMessage);
			var parsed = parseResult.Value;

			var goResult = PathResolver.ResolveGameObject(parsed);
			if (!goResult.IsSuccess) return new ErrorResponse(goResult.ErrorMessage);
			var go = goResult.Value;

			// No component → GameObject view.
			if (!parsed.Component.IsPresent)
				return RenderGameObject(go, overridesOnly, format);

			var compResult = PathResolver.ResolveComponent(go, parsed.Component);
			if (!compResult.IsSuccess) return new ErrorResponse(compResult.ErrorMessage);
			var component = compResult.Value;

			// Component with no properties → full component dump.
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return RenderComponent(go, component, overridesOnly, format);

			// Component + properties → drill down to the exact property.
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
					// trim trailing newline we just added
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
