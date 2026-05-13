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

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Asset GUID ↔ path translation. Useful when working with .meta files,
	/// manual reference repair, reading scene files as text, or feeding
	/// other tools that speak GUIDs instead of paths.
	///
	/// One tool handles both directions; the <c>direction</c> param picks
	/// which. The CLI exposes two named commands (<c>guid</c> and <c>path</c>)
	/// that dispatch to this tool with the appropriate direction.
	///
	/// Output:
	///   - Plain (default): one value per input line, in input order. An
	///     unresolvable input emits an empty line and goes to stderr.
	///   - JSON: array of <c>{path, guid}</c> records, including failed
	///     ones (with <c>error</c>).
	///
	/// Exit code 3 (NotFound) if any input failed to resolve.
	/// </summary>
	[UnityCliTool(Name = "guid",
		Description = "Translate between asset paths and GUIDs. Direction via --direction.")]
	public static class GuidTool
	{
		public class Parameters
		{
			[ToolParameter("Direction: 'to-guid' (default, path→guid) or 'to-path' (guid→path).")]
			public string Direction { get; set; }

			[ToolParameter("Single input (asset path for to-guid, GUID for to-path). Or pass multiple via args.")]
			public string Input { get; set; }

			[ToolParameter("Output format: plain (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var direction = (p.Get("direction") ?? "to-guid").ToLowerInvariant();
			var format = (p.Get("format") ?? "plain").ToLowerInvariant();

			// Collect inputs from --input, args[], or both.
			var inputs = new List<string>();
			var single = p.Get("input");
			if (!string.IsNullOrWhiteSpace(single)) inputs.Add(single);
			var args = p.GetRaw("args") as JArray;
			if (args != null)
				foreach (var a in args)
				{
					var s = a?.ToString();
					if (!string.IsNullOrWhiteSpace(s)) inputs.Add(s);
				}
			if (inputs.Count == 0)
				return new ErrorResponse(
					direction == "to-path"
						? "guid → path requires at least one GUID."
						: "path → guid requires at least one asset path.",
					ErrorKind.Usage);

			System.Func<string, (string ok, string err)> convert = direction switch
			{
				"to-guid" => PathToGuid,
				"to_guid" => PathToGuid,
				"to-path" => GuidToPath,
				"to_path" => GuidToPath,
				_ => null,
			};
			if (convert == null)
				return new ErrorResponse(
					$"Unknown direction '{direction}'. Use 'to-guid' or 'to-path'.",
					ErrorKind.Usage);

			var results = new List<Dictionary<string, object>>(inputs.Count);
			var plainLines = new List<string>(inputs.Count);
			var errorLines = new List<string>();

			foreach (var input in inputs)
			{
				var (ok, err) = convert(input);
				if (err == null)
				{
					results.Add(new Dictionary<string, object>
					{
						["input"] = input,
						["output"] = ok,
					});
					plainLines.Add(ok);
				}
				else
				{
					results.Add(new Dictionary<string, object>
					{
						["input"] = input,
						["error"] = err,
					});
					plainLines.Add(""); // keep line alignment for parallel arrays
					errorLines.Add($"{input}: {err}");
				}
			}

			if (format == "json")
			{
				var jsonResp = new SuccessResponse("", new Dictionary<string, object>
				{
					["direction"] = direction,
					["count"] = inputs.Count,
					["results"] = results,
				});
				if (errorLines.Count > 0)
				{
					jsonResp.partialFailure = true;
					jsonResp.stderr = string.Join("\n", errorLines);
				}
				return jsonResp;
			}

			var resp = new SuccessResponse("", string.Join("\n", plainLines));
			if (errorLines.Count > 0)
			{
				resp.partialFailure = true;
				resp.stderr = string.Join("\n", errorLines);
			}
			return resp;
		}

		// path → guid. AssetPathToGUID returns "" when the asset doesn't exist.
		private static (string ok, string err) PathToGuid(string assetPath)
		{
			if (string.IsNullOrWhiteSpace(assetPath))
				return (null, "empty path");
			var guid = AssetDatabase.AssetPathToGUID(assetPath);
			if (string.IsNullOrEmpty(guid))
				return (null, "asset not found");
			return (guid, null);
		}

		// guid → path. GUIDToAssetPath returns "" when the GUID doesn't map to
		// anything in the current project.
		private static (string ok, string err) GuidToPath(string guid)
		{
			if (string.IsNullOrWhiteSpace(guid))
				return (null, "empty GUID");
			// Validate shape: 32 hex chars (Unity asset GUIDs).
			var trimmed = guid.Trim();
			if (trimmed.Length != 32 || !IsHex(trimmed))
				return (null, "not a 32-char hex GUID");
			var path = AssetDatabase.GUIDToAssetPath(trimmed);
			if (string.IsNullOrEmpty(path))
				return (null, "no asset for this GUID in the project");
			return (path, null);
		}

		private static bool IsHex(string s)
		{
			foreach (var c in s)
			{
				var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
				if (!ok) return false;
			}
			return true;
		}
	}
}
