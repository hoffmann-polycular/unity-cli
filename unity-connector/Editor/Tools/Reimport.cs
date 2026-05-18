using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Force Unity to re-run the import pipeline for one or more assets.
	/// Different from <c>reserialize</c> — reserialize rewrites a file
	/// through Unity's YAML serializer without touching the importer;
	/// <c>reimport</c> actually re-runs <c>TextureImporter</c> /
	/// <c>ModelImporter</c> / etc. to regenerate the imported result.
	///
	/// Not normally needed after <c>set &lt;asset&gt;:Importer.*</c> — Unity
	/// re-imports automatically when a meta file changes. Use this for the
	/// cases where the import wasn't triggered by a meta change: an external
	/// tool rewrote source files on disk, or you want to force a re-import
	/// to recover from a partial / corrupted import.
	///
	/// Usage:
	///   unity-cli reimport Assets/Foo.png
	///   unity-cli reimport Assets/Textures/ --recursive
	///   find Assets/Sprites/ --type Texture --plain | unity-cli reimport
	/// </summary>
	[UnityCliTool(Name = "reimport",
		Description = "Re-run the import pipeline on one or more assets. --recursive walks folders.")]
	public static class Reimport
	{
		public class Parameters
		{
			[ToolParameter("Asset path (file or folder).", Required = false)]
			public string Path { get; set; }

			[ToolParameter("Walk into directories; reimport every asset under them.")]
			public bool Recursive { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var recursive = p.GetBool("recursive");

			// Collect input paths from --path, args[], or stdin (paths array
			// when Go-side multi-path is used). Mirrors the rm tool's input
			// surface.
			var paths = new List<string>();
			var single = p.Get("path");
			if (!string.IsNullOrWhiteSpace(single)) paths.Add(single);
			if (args != null)
				foreach (var a in args)
				{
					var s = a?.ToString();
					if (!string.IsNullOrWhiteSpace(s)) paths.Add(s);
				}

			if (paths.Count == 0)
				return new ErrorResponse("reimport requires at least one asset path.", ErrorKind.Usage);

			var reimported = new List<string>();
			var errors = new List<string>();

			try
			{
				AssetDatabase.StartAssetEditing();
				foreach (var path in paths)
				{
					ReimportOne(path, recursive, reimported, errors);
				}
			}
			finally
			{
				AssetDatabase.StopAssetEditing();
			}

			if (reimported.Count == 0)
				return new ErrorResponse(
					errors.Count == 0 ? "No assets matched." : string.Join("\n", errors),
					ErrorKind.NotFound);

			var data = new Dictionary<string, object>
			{
				["reimported"] = reimported,
				["count"] = reimported.Count,
			};
			if (errors.Count > 0) data["errors"] = errors;

			var msg = errors.Count == 0
				? $"Reimported {reimported.Count} asset(s)."
				: $"Reimported {reimported.Count} asset(s); {errors.Count} failed.";

			var resp = new SuccessResponse(msg, data);
			if (errors.Count > 0)
			{
				resp.partialFailure = true;
				resp.stderr = string.Join("\n", errors);
			}
			return resp;
		}

		private static void ReimportOne(string path, bool recursive,
			List<string> reimported, List<string> errors)
		{
			// Directory vs. file. AssetDatabase.IsValidFolder is the canonical
			// check (handles Packages/ paths too).
			if (AssetDatabase.IsValidFolder(path))
			{
				if (!recursive)
				{
					errors.Add($"{path}: is a folder — pass --recursive to walk it.");
					return;
				}
				// FindAssets with the folder filter yields every asset under it.
				var guids = AssetDatabase.FindAssets("", new[] { path });
				foreach (var guid in guids)
				{
					var p = AssetDatabase.GUIDToAssetPath(guid);
					if (string.IsNullOrEmpty(p)) continue;
					if (AssetDatabase.IsValidFolder(p)) continue;
					try
					{
						AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
						reimported.Add(p);
					}
					catch (System.Exception ex)
					{
						errors.Add($"{p}: {ex.Message}");
					}
				}
				return;
			}

			if (!File.Exists(path) && AssetDatabase.LoadMainAssetAtPath(path) == null)
			{
				errors.Add($"{path}: asset not found.");
				return;
			}

			try
			{
				AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
				reimported.Add(path);
			}
			catch (System.Exception ex)
			{
				errors.Add($"{path}: {ex.Message}");
			}
		}
	}
}
