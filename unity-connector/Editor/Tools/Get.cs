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
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Reads a single serialized-property value. The path MUST include a
	/// component and at least one property segment (e.g.
	/// <c>Player:Transform.position.x</c>).
	///
	/// Default human output is scripting-friendly: scalars print raw,
	/// vectors/colors as space-separated components, references as
	/// canonical paths — so <c>get | set</c> and <c>get | inspect</c>
	/// pipelines round-trip without quoting tricks. <c>--json</c> wraps
	/// the value with metadata for tooling.
	/// </summary>
	[UnityCliTool(Name = "get",
		Description = "Read a single property value. Path must include :Component.property.")]
	public static class Get
	{
		public class Parameters
		{
			[ToolParameter("Path to a property, e.g. Player:Transform.position.x.", Required = true)]
			public string Path { get; set; }

			[ToolParameter("For prefab instances, read the prefab source value (ignoring overrides).")]
			public bool Source { get; set; }

			[ToolParameter("Output format: human (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var path = p.Get("path")
					   ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			var sourceMode = p.GetBool("source");
			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("get requires a path with :Component.property.");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return new ErrorResponse(parseResult.ErrorMessage);
			var parsed = parseResult.Value;

			if (!parsed.Component.IsPresent)
				return new ErrorResponse("get requires a component — add ':TypeName' to the path.");
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse("get requires a property — add '.propertyName' to the path.");

			var goResult = PathResolver.ResolveGameObject(parsed);
			if (!goResult.IsSuccess) return new ErrorResponse(goResult.ErrorMessage);

			var compResult = PathResolver.ResolveComponent(goResult.Value, parsed.Component);
			if (!compResult.IsSuccess) return new ErrorResponse(compResult.ErrorMessage);
			var component = compResult.Value;

			// --source: swap the live component for the prefab-source one and
			// read the same property path off it. Same code path, different root.
			Component readTarget = component;
			if (sourceMode)
			{
				var src = PrefabUtility.GetCorrespondingObjectFromSource(component);
				if (src == null)
					return new ErrorResponse(
						$"--source requires a prefab-instance target; '{PathResolver.GetCanonicalPath(goResult.Value)}' is not connected to a prefab.");
				readTarget = src;
			}

			using var so = new SerializedObject(readTarget);
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

			var value = SerializedPropertyReader.Read(current);

			if (format == "json")
			{
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = PathResolver.GetCanonicalPath(goResult.Value),
					["component"] = component.GetType().Name,
					["property"] = JoinProps(parsed.Properties, parsed.Properties.Count),
					["type"] = current.propertyType.ToString(),
					["override"] = current.prefabOverride,
					["source"] = sourceMode,
					["value"] = value,
				});
			}

			return new SuccessResponse("", FormatPipeFriendly(value));
		}

		// ---- pipe-friendly rendering ----
		//
		// Everything that round-trips through `set` should come out in a form
		// `set` accepts verbatim. Vectors/colors → space-separated, references
		// → canonical paths, null → "null".

		private static string FormatPipeFriendly(object value)
		{
			switch (value)
			{
				case null: return "null";
				case string s: return s;
				case bool b: return b ? "true" : "false";
				case float f: return f.ToString("R", CultureInfo.InvariantCulture);
				case double d: return d.ToString("R", CultureInfo.InvariantCulture);
				case int i: return i.ToString(CultureInfo.InvariantCulture);
				case long l: return l.ToString(CultureInfo.InvariantCulture);
				case Dictionary<string, object> dict:
					return FormatDict(dict);
				case List<object> list:
					return FormatList(list);
				default: return value.ToString();
			}
		}

		private static string FormatDict(Dictionary<string, object> dict)
		{
			// Object reference shapes from SerializedPropertyReader.
			if (dict.TryGetValue("path", out var pathVal) && pathVal is string ps && !string.IsNullOrEmpty(ps))
				return ps;
			if (dict.TryGetValue("asset", out var assetVal) && assetVal is string a && !string.IsNullOrEmpty(a))
				return a;

			// Vector / color: emit components in canonical order.
			if (LooksLikeVector(dict))
			{
				var sb = new StringBuilder();
				var first = true;
				foreach (var key in OrderedKeys(dict))
				{
					if (!first) sb.Append(' ');
					first = false;
					sb.Append(FormatPipeFriendly(dict[key]));
				}
				return sb.ToString();
			}

			// Anonymous object refs without a path/asset: fall back to instance ID.
			if (dict.TryGetValue("instanceId", out var idVal))
				return "#" + idVal;

			// Anything else: key=val per line.
			var multi = new StringBuilder();
			foreach (var kv in dict)
			{
				multi.Append(kv.Key).Append('=').Append(FormatPipeFriendly(kv.Value)).Append('\n');
			}
			return multi.ToString().TrimEnd('\n');
		}

		private static string FormatList(List<object> list)
		{
			if (list.Count == 0) return "";
			var sb = new StringBuilder();
			for (var i = 0; i < list.Count; i++)
			{
				if (i > 0) sb.Append('\n');
				sb.Append(FormatPipeFriendly(list[i]));
			}
			return sb.ToString();
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

		// Keep canonical xyzw / rgba ordering even if the dict was constructed
		// in some other insertion order.
		private static IEnumerable<string> OrderedKeys(Dictionary<string, object> dict)
		{
			string[] order = { "x", "y", "z", "w", "r", "g", "b", "a" };
			foreach (var k in order)
				if (dict.ContainsKey(k)) yield return k;
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
	}
}
