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
			var args = p.GetRaw("args") as JArray;
			var pathParam = p.Get("path");
			var sourceMode = p.GetBool("source");
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();
			// --with-path: prefix every plain-mode line with `path:Component.prop`
			// so multi-target reads have context without giving up `--plain`'s
			// other ergonomics (no human-mode column alignment, single value
			// per line). Spec §4.5 keeps the default unprefixed.
			var withPath = p.GetBool("with_path");

			// Multi-path mode: args has >1 entries → fan out across all of them.
			// Each entry is an independent path with its own :Component.property
			// suffix; their target sets union into one flat result list.
			if (string.IsNullOrWhiteSpace(pathParam) && args != null && args.Count > 1)
				return GetMulti(args, sourceMode, format, withPath);

			var path = pathParam ?? (args != null && args.Count > 0 ? args[0]?.ToString() : null);

			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("get requires a path with :Component.property.");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
			var parsed = parseResult.Value;

			// ProjectSettings handling: read serialized property off the settings group.
			if (parsed.Kind == PathKind.ProjectSettings)
				return GetProjectSettings(parsed, format);

			// Asset importer access: Assets/Foo.png:TextureImporter.maxTextureSize
			// or :Importer.<prop> (pseudo-name that resolves to the asset's actual
			// importer). Reads through SerializedObject(importer); no re-import.
			if (parsed.Kind == PathKind.Asset && PathResolver.IsImporterComponent(parsed.Component))
				return GetFromImporter(parsed, format);

			if (!parsed.Component.IsPresent)
				return new ErrorResponse("get requires a component — add ':TypeName' to the path.");
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse("get requires a property — add '.propertyName' to the path.");

			// v3: fan out across selection. Single-target preserves the legacy
			// scalar output; multi-target emits one prefixed line per result.
			var targetsRes = PathResolver.ResolveTargets(parsed);
			if (!targetsRes.IsSuccess) return ErrorResponse.FromResult(targetsRes);
			var targets = targetsRes.Value;

			if (targets.Count == 1)
				return GetOne(targets[0], parsed, sourceMode, format, prefixWithPath: false);

			// Fan-out: per-target results. Per §4.6 successes go to stdout,
			// failures to stderr, and any failure makes the exit code non-zero.
			var entries = new List<object>(targets.Count);
			var successLines = new List<string>(targets.Count);
			var errorLines = new List<string>(targets.Count);
			var successCount = 0;
			foreach (var go in targets)
			{
				var single = GetOne(go, parsed, sourceMode, format: "json", prefixWithPath: false);
				if (single is SuccessResponse sr && sr.data is Dictionary<string, object> dict)
				{
					entries.Add(dict);
					successCount++;
					if (format != "json")
					{
						var rendered = FormatPipeFriendly(dict.TryGetValue("value", out var vv) ? vv : null);
						if (format == "plain" && !withPath)
						{
							// --plain default: value only, no path prefix (§4.5).
							successLines.Add(rendered);
						}
						else
						{
							var canon = dict.TryGetValue("path", out var pp) ? pp?.ToString() : PathResolver.GetCanonicalPath(go);
							var compName = dict.TryGetValue("component", out var cc) ? cc?.ToString() : "";
							var propName = dict.TryGetValue("property", out var pn) ? pn?.ToString() : "";
							successLines.Add($"{canon}:{compName}.{propName}  {rendered}");
						}
					}
				}
				else if (single is ErrorResponse er)
				{
					entries.Add(new Dictionary<string, object>
					{
						["path"] = PathResolver.GetCanonicalPath(go),
						["ok"] = false,
						["error"] = er.message,
					});
					if (format != "json")
						errorLines.Add($"{PathResolver.GetCanonicalPath(go)}: {er.message}");
				}
			}

			if (format == "json")
			{
				var jsonResp = new SuccessResponse("", new Dictionary<string, object>
				{
					["count"] = targets.Count,
					["results"] = entries,
				});
				if (successCount < targets.Count)
				{
					jsonResp.partialFailure = true;
					jsonResp.stderr = string.Join("\n", errorLines);
				}
				return jsonResp;
			}

			var resp = new SuccessResponse("", string.Join("\n", successLines));
			if (errorLines.Count > 0)
			{
				resp.partialFailure = true;
				resp.stderr = string.Join("\n", errorLines);
			}
			return resp;
		}

		// Multi-path entry point: process each input path independently,
		// then unify the per-target results into one fan-out response.
		// Each path can have its own component/property suffix; errors on
		// one path don't stop the others.
		private static object GetMulti(JArray paths, bool sourceMode, string format, bool withPath)
		{
			var entries = new List<object>();
			var successLines = new List<string>();
			var errorLines = new List<string>();
			var successCount = 0;
			var totalCount = 0;

			foreach (var pathTok in paths)
			{
				var pathStr = pathTok?.ToString();
				if (string.IsNullOrWhiteSpace(pathStr)) continue;

				var parseRes = PathParser.Parse(pathStr);
				if (!parseRes.IsSuccess)
				{
					errorLines.Add($"{pathStr}: {parseRes.ErrorMessage}");
					totalCount++;
					continue;
				}
				var parsed = parseRes.Value;

				if (parsed.Kind == PathKind.ProjectSettings)
				{
					errorLines.Add($"{pathStr}: ProjectSettings paths are not supported in multi-path mode.");
					totalCount++;
					continue;
				}

				// Asset importer per-path handling.
				if (parsed.Kind == PathKind.Asset && PathResolver.IsImporterComponent(parsed.Component))
				{
					totalCount++;
					var imp = GetFromImporter(parsed, "json");
					if (imp is SuccessResponse impSr && impSr.data is Dictionary<string, object> impDict)
					{
						entries.Add(impDict);
						successCount++;
						if (format != "json")
						{
							var rendered = FormatPipeFriendly(impDict.TryGetValue("value", out var vv) ? vv : null);
							if (format == "plain" && !withPath) successLines.Add(rendered);
							else
							{
								var canon = impDict.TryGetValue("path", out var pp) ? pp?.ToString() : parsed.AssetPath;
								var compName = impDict.TryGetValue("component", out var cc) ? cc?.ToString() : "";
								var propName = impDict.TryGetValue("property", out var pn) ? pn?.ToString() : "";
								successLines.Add($"{canon}:{compName}.{propName}  {rendered}");
							}
						}
					}
					else if (imp is ErrorResponse impEr)
					{
						errorLines.Add($"{pathStr}: {impEr.message}");
					}
					continue;
				}

				if (!parsed.Component.IsPresent)
				{
					errorLines.Add($"{pathStr}: get requires a component — add ':TypeName' to the path.");
					totalCount++;
					continue;
				}
				if (parsed.Properties == null || parsed.Properties.Count == 0)
				{
					errorLines.Add($"{pathStr}: get requires a property — add '.propertyName' to the path.");
					totalCount++;
					continue;
				}

				var targetsRes = PathResolver.ResolveTargets(parsed);
				if (!targetsRes.IsSuccess)
				{
					errorLines.Add($"{pathStr}: {targetsRes.ErrorMessage}");
					totalCount++;
					continue;
				}

				foreach (var go in targetsRes.Value)
				{
					totalCount++;
					var single = GetOne(go, parsed, sourceMode, "json", prefixWithPath: false);
					if (single is SuccessResponse sr && sr.data is Dictionary<string, object> dict)
					{
						entries.Add(dict);
						successCount++;
						if (format != "json")
						{
							var rendered = FormatPipeFriendly(dict.TryGetValue("value", out var vv) ? vv : null);
							if (format == "plain" && !withPath)
							{
								successLines.Add(rendered);
							}
							else
							{
								var canon = dict.TryGetValue("path", out var pp) ? pp?.ToString() : PathResolver.GetCanonicalPath(go);
								var compName = dict.TryGetValue("component", out var cc) ? cc?.ToString() : "";
								var propName = dict.TryGetValue("property", out var pn) ? pn?.ToString() : "";
								successLines.Add($"{canon}:{compName}.{propName}  {rendered}");
							}
						}
					}
					else if (single is ErrorResponse er)
					{
						entries.Add(new Dictionary<string, object>
						{
							["path"] = PathResolver.GetCanonicalPath(go),
							["ok"] = false,
							["error"] = er.message,
						});
						if (format != "json")
							errorLines.Add($"{PathResolver.GetCanonicalPath(go)}: {er.message}");
					}
				}
			}

			if (format == "json")
			{
				var jsonResp = new SuccessResponse("", new Dictionary<string, object>
				{
					["count"] = totalCount,
					["results"] = entries,
				});
				if (successCount < totalCount)
				{
					jsonResp.partialFailure = true;
					jsonResp.stderr = string.Join("\n", errorLines);
				}
				return jsonResp;
			}

			var resp = new SuccessResponse("", string.Join("\n", successLines));
			if (errorLines.Count > 0)
			{
				resp.partialFailure = true;
				resp.stderr = string.Join("\n", errorLines);
			}
			return resp;
		}

		private static object GetOne(GameObject go, ParsedPath parsed, bool sourceMode, string format, bool prefixWithPath)
		{
			// :GameObject pseudo-component — bypass SerializedObject entirely.
			if (GameObjectProxy.Is(parsed.Component.TypeName))
			{
				if (sourceMode)
					return new ErrorResponse(
						"--source is not applicable to :GameObject (it has no prefab-override serialization).");
				var propName = parsed.Properties[0];
				var propRes = GameObjectProxy.Get(go, propName);
				if (!propRes.IsSuccess) return ErrorResponse.FromResult(propRes);
				var goValue = propRes.Value;
				if (format == "json")
				{
					return new SuccessResponse("", new Dictionary<string, object>
					{
						["path"]      = PathResolver.GetCanonicalPath(go),
						["component"] = GameObjectProxy.PseudoTypeName,
						["property"]  = propName,
						["type"]      = "GameObjectProperty",
						["value"]     = goValue,
					});
				}
				return new SuccessResponse("", FormatPipeFriendly(goValue));
			}

			var compResult = PathResolver.ResolveComponent(go, parsed.Component);
			if (!compResult.IsSuccess) return ErrorResponse.FromResult(compResult);
			var component = compResult.Value;

			// --source: swap the live component for the prefab-source one and
			// read the same property path off it. Same code path, different root.
			Component readTarget = component;
			if (sourceMode)
			{
				var src = PrefabUtility.GetCorrespondingObjectFromSource(component);
				if (src == null)
					return new ErrorResponse(
						$"--source requires a prefab-instance target; '{PathResolver.GetCanonicalPath(go)}' is not connected to a prefab.");
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
					["path"] = PathResolver.GetCanonicalPath(go),
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

		private static object GetFromImporter(ParsedPath parsed, string format)
		{
			if (parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse("get requires a property — add '.propertyName' to the path.");

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
						$"No sub-property '{parsed.Properties[i]}' under '{JoinProps(parsed.Properties, i)}'.",
						ErrorKind.NotFound);
				current = next;
			}

			var value = SerializedPropertyReader.Read(current);
			if (format == "json")
			{
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = parsed.AssetPath,
					["component"] = importer.GetType().Name,
					["property"] = JoinProps(parsed.Properties, parsed.Properties.Count),
					["type"] = current.propertyType.ToString(),
					["value"] = value,
				});
			}
			return new SuccessResponse("", FormatPipeFriendly(value));
		}

		private static object GetProjectSettings(ParsedPath parsed, string format)
		{
			if (!parsed.Component.IsPresent || parsed.Component.TypeName != PathParser.SettingsRootSentinel
				|| parsed.Properties == null || parsed.Properties.Count == 0)
				return new ErrorResponse(
					"get requires a property under a ProjectSettings group, e.g. 'ProjectSettings/Physics.gravity'.",
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
						$"No sub-property '{parsed.Properties[i]}' under '{JoinProps(parsed.Properties, i)}'.",
						ErrorKind.NotFound);
				current = next;
			}

			var value = SerializedPropertyReader.Read(current);
			if (format == "json")
			{
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"] = ProjectSettingsResolver.CanonicalPath(parsed.SettingsGroup),
					["property"] = JoinProps(parsed.Properties, parsed.Properties.Count),
					["type"] = current.propertyType.ToString(),
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
