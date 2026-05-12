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
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Manage loaded scenes from the CLI: list, open, close, save, reload,
	/// set-active, new, dirty. Wraps <see cref="EditorSceneManager"/> and
	/// <see cref="SceneManager"/> in a single action-dispatched tool that
	/// matches the editor / prefab / profiler subcommand pattern.
	///
	/// Identifier resolution: every action that names a loaded scene accepts
	/// either the asset path (preferred — unambiguous) or the scene name.
	/// Ambiguous name matches fail loudly with the candidate paths listed,
	/// matching how other tools handle path ambiguity (exit code 2).
	/// </summary>
	[UnityCliTool(Name = "scene",
		Description = "Manage scenes: list, open, close, save, reload, set-active, new, dirty.")]
	public static class SceneTool
	{
		public class Parameters
		{
			[ToolParameter("Action: list, open, close, save, reload, set-active, new, dirty.", Required = true)]
			public string Action { get; set; }

			[ToolParameter("Scene asset path or name (for open/close/save/reload/set-active/dirty).")]
			public string Path { get; set; }

			[ToolParameter("Destination asset path for 'save --as' / 'new --as'.")]
			public string Asset { get; set; }

			[ToolParameter("Open mode: single (default), additive, additive-without-loading.")]
			public string Mode { get; set; }

			[ToolParameter("On close: save unsaved changes before closing.")]
			public bool Save { get; set; }

			[ToolParameter("On close: discard unsaved changes without prompting.")]
			public bool Discard { get; set; }

			[ToolParameter("Output format: human (default), json, plain.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var action = (p.Get("action") ?? "").ToLowerInvariant();
			if (string.IsNullOrEmpty(action))
				return new ErrorResponse("scene requires an action (list, open, close, save, reload, set-active, new, dirty).",
					ErrorKind.Usage);

			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			switch (action)
			{
				case "list":       return DoList(format);
				case "open":       return DoOpen(p, format);
				case "close":      return DoClose(p, format);
				case "save":       return DoSave(p, format);
				case "reload":     return DoReload(p, format);
				case "set-active":
				case "set_active":
				case "setactive":  return DoSetActive(p, format);
				case "new":        return DoNew(p, format);
				case "dirty":      return DoDirty(p, format);
				default:
					return new ErrorResponse(
						$"Unknown scene action '{action}'. Available: list, open, close, save, reload, set-active, new, dirty.",
						ErrorKind.Usage);
			}
		}

		// ── list ────────────────────────────────────────────────────────────

		private static object DoList(string format)
		{
			var entries = new List<Dictionary<string, object>>();
			var active = SceneManager.GetActiveScene();
			for (var i = 0; i < SceneManager.sceneCount; i++)
			{
				var s = SceneManager.GetSceneAt(i);
				entries.Add(new Dictionary<string, object>
				{
					["path"]       = s.path,
					["name"]       = s.name,
					["isLoaded"]   = s.isLoaded,
					["isDirty"]    = s.isDirty,
					["isActive"]   = s == active,
					["buildIndex"] = s.buildIndex,
				});
			}

			if (format == "json")
				return new SuccessResponse("", entries);

			if (format == "plain")
			{
				var sb = new StringBuilder();
				foreach (var e in entries)
				{
					sb.Append(e["isActive"] is bool b && b ? "* " : "  ");
					sb.Append(string.IsNullOrEmpty((string)e["path"]) ? (string)e["name"] : (string)e["path"]);
					sb.Append('\n');
				}
				return new SuccessResponse("", sb.ToString().TrimEnd('\n'));
			}

			// human
			var hsb = new StringBuilder();
			foreach (var e in entries)
			{
				hsb.Append(e["isActive"] is bool ba && ba ? "* " : "  ");
				hsb.Append(string.IsNullOrEmpty((string)e["path"]) ? (string)e["name"] : (string)e["path"]);
				if (e["isDirty"] is bool bd && bd) hsb.Append("  (modified)");
				if (e["isLoaded"] is bool bl && !bl) hsb.Append("  (unloaded)");
				hsb.Append('\n');
			}
			return new SuccessResponse("", hsb.ToString().TrimEnd('\n'));
		}

		// ── open ────────────────────────────────────────────────────────────

		private static object DoOpen(ToolParams p, string format)
		{
			var assetPath = p.Get("path") ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			if (string.IsNullOrWhiteSpace(assetPath))
				return new ErrorResponse("scene open requires an asset path.", ErrorKind.Usage);

			if (!File.Exists(assetPath))
				return new ErrorResponse($"Scene file not found: '{assetPath}'.", ErrorKind.NotFound);

			var modeStr = (p.Get("mode") ?? "single").ToLowerInvariant();
			OpenSceneMode mode;
			switch (modeStr)
			{
				case "single":   mode = OpenSceneMode.Single; break;
				case "additive": mode = OpenSceneMode.Additive; break;
				case "additive-without-loading":
				case "additive_without_loading":
				case "additivewithoutloading":
				case "without-loading":
					mode = OpenSceneMode.AdditiveWithoutLoading; break;
				default:
					return new ErrorResponse(
						$"Unknown open mode '{modeStr}'. Use: single, additive, additive-without-loading.",
						ErrorKind.Usage);
			}

			// Guard against silent data loss: Single mode replaces all currently
			// loaded scenes. If any are dirty, refuse — the user should save or
			// explicitly use Additive.
			if (mode == OpenSceneMode.Single)
			{
				for (var i = 0; i < SceneManager.sceneCount; i++)
				{
					var s = SceneManager.GetSceneAt(i);
					if (s.isDirty)
						return new ErrorResponse(
							$"Open with mode=single would discard unsaved changes in '{(string.IsNullOrEmpty(s.path) ? s.name : s.path)}'. " +
							"Save first, or use mode=additive.",
							ErrorKind.Usage);
				}
			}

			var openedScene = EditorSceneManager.OpenScene(assetPath, mode);
			return BuildSceneResult(openedScene, $"Opened {openedScene.path}", format);
		}

		// ── close ───────────────────────────────────────────────────────────

		private static object DoClose(ToolParams p, string format)
		{
			var ident = p.Get("path") ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			if (string.IsNullOrWhiteSpace(ident))
				return new ErrorResponse("scene close requires <pathOrName>.", ErrorKind.Usage);

			var save = p.GetBool("save");
			var discard = p.GetBool("discard");
			if (save && discard)
				return new ErrorResponse("scene close --save and --discard are mutually exclusive.", ErrorKind.Usage);

			var sceneRes = ResolveScene(ident);
			if (!sceneRes.IsSuccess) return ErrorResponse.FromResult(sceneRes);
			var scene = sceneRes.Value;

			if (scene.isDirty)
			{
				if (save)
				{
					if (string.IsNullOrEmpty(scene.path))
						return new ErrorResponse(
							"Cannot save an unsaved scene with no asset path. Use 'scene save --as <assetpath>' first.",
							ErrorKind.Usage);
					EditorSceneManager.SaveScene(scene);
				}
				else if (!discard)
				{
					return new ErrorResponse(
						$"Scene '{(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}' has unsaved changes. " +
						"Pass --save or --discard.",
						ErrorKind.Usage);
				}
			}

			var path = scene.path;
			var name = scene.name;
			var closed = EditorSceneManager.CloseScene(scene, removeScene: true);
			if (!closed)
				return new ErrorResponse(
					$"Failed to close scene '{(string.IsNullOrEmpty(path) ? name : path)}' — Unity refused " +
					"(closing the only loaded scene is not permitted).");

			var msg = $"Closed {(string.IsNullOrEmpty(path) ? name : path)}";
			if (format == "json")
				return new SuccessResponse(msg, new Dictionary<string, object>
				{
					["path"] = path, ["name"] = name, ["closed"] = true,
				});
			return new SuccessResponse(msg, msg);
		}

		// ── save ────────────────────────────────────────────────────────────

		private static object DoSave(ToolParams p, string format)
		{
			var ident = p.Get("path") ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			var asAsset = p.Get("asset");

			Scene scene;
			if (string.IsNullOrWhiteSpace(ident))
			{
				scene = SceneManager.GetActiveScene();
			}
			else
			{
				var sceneRes = ResolveScene(ident);
				if (!sceneRes.IsSuccess) return ErrorResponse.FromResult(sceneRes);
				scene = sceneRes.Value;
			}

			bool ok;
			string finalPath;
			if (!string.IsNullOrEmpty(asAsset))
			{
				ok = EditorSceneManager.SaveScene(scene, asAsset);
				finalPath = asAsset;
			}
			else
			{
				if (string.IsNullOrEmpty(scene.path))
					return new ErrorResponse(
						"Scene has no asset path yet — use 'scene save --as <assetpath>' to choose one.",
						ErrorKind.Usage);
				ok = EditorSceneManager.SaveScene(scene);
				finalPath = scene.path;
			}

			if (!ok) return new ErrorResponse($"SaveScene failed for '{finalPath}'.");
			// Re-fetch because SaveScene-as changes the scene's path field.
			scene = SceneManager.GetSceneByPath(finalPath);
			return BuildSceneResult(scene, $"Saved {finalPath}", format);
		}

		// ── reload ──────────────────────────────────────────────────────────

		private static object DoReload(ToolParams p, string format)
		{
			var ident = p.Get("path") ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			var save = p.GetBool("save");
			var discard = p.GetBool("discard");
			if (save && discard)
				return new ErrorResponse("scene reload --save and --discard are mutually exclusive.", ErrorKind.Usage);

			Scene scene;
			if (string.IsNullOrWhiteSpace(ident))
			{
				scene = SceneManager.GetActiveScene();
			}
			else
			{
				var sceneRes = ResolveScene(ident);
				if (!sceneRes.IsSuccess) return ErrorResponse.FromResult(sceneRes);
				scene = sceneRes.Value;
			}

			if (string.IsNullOrEmpty(scene.path))
				return new ErrorResponse(
					"Cannot reload a scene that has never been saved to disk.",
					ErrorKind.Usage);
			if (scene.isDirty)
			{
				if (save)
				{
					// Save then reload becomes a no-op for state but still reopens
					// from disk, which is what the user explicitly asked for.
					EditorSceneManager.SaveScene(scene);
				}
				else if (!discard)
				{
					return new ErrorResponse(
						$"Scene '{scene.path}' has unsaved changes — reload would discard them. " +
						"Pass --save to save first, or --discard to throw the edits away.",
						ErrorKind.Usage);
				}
			}

			var path = scene.path;
			var wasActive = SceneManager.GetActiveScene() == scene;
			// If this is the only loaded scene, reopen as Single (Additive would
			// leave nothing during the close→open gap). Otherwise Additive so the
			// rest of the loaded set is preserved.
			var mode = SceneManager.sceneCount > 1 ? OpenSceneMode.Additive : OpenSceneMode.Single;
			EditorSceneManager.CloseScene(scene, removeScene: true);
			var reopened = EditorSceneManager.OpenScene(path, mode);
			if (wasActive) EditorSceneManager.SetActiveScene(reopened);

			return BuildSceneResult(reopened, $"Reloaded {reopened.path}", format);
		}

		// ── set-active ──────────────────────────────────────────────────────

		private static object DoSetActive(ToolParams p, string format)
		{
			var ident = p.Get("path") ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			if (string.IsNullOrWhiteSpace(ident))
				return new ErrorResponse("scene set-active requires <pathOrName>.", ErrorKind.Usage);

			var sceneRes = ResolveScene(ident);
			if (!sceneRes.IsSuccess) return ErrorResponse.FromResult(sceneRes);
			var scene = sceneRes.Value;

			EditorSceneManager.SetActiveScene(scene);
			return BuildSceneResult(scene,
				$"Set active scene: {(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}",
				format);
		}

		// ── new ─────────────────────────────────────────────────────────────

		private static object DoNew(ToolParams p, string format)
		{
			var asAsset = p.Get("asset");

			// Refuse to clobber any currently-dirty scene — same guard as Open(Single).
			for (var i = 0; i < SceneManager.sceneCount; i++)
			{
				var s = SceneManager.GetSceneAt(i);
				if (s.isDirty)
					return new ErrorResponse(
						$"'scene new' would discard unsaved changes in '{(string.IsNullOrEmpty(s.path) ? s.name : s.path)}'. " +
						"Save first.",
						ErrorKind.Usage);
			}

			var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

			if (!string.IsNullOrEmpty(asAsset))
			{
				var ok = EditorSceneManager.SaveScene(newScene, asAsset);
				if (!ok) return new ErrorResponse($"Created new scene but SaveScene failed for '{asAsset}'.");
				newScene = SceneManager.GetSceneByPath(asAsset);
				return BuildSceneResult(newScene, $"Created new scene at {asAsset}", format);
			}
			return BuildSceneResult(newScene, "Created new (unsaved) scene", format);
		}

		// ── dirty (query) ──────────────────────────────────────────────────

		private static object DoDirty(ToolParams p, string format)
		{
			var ident = p.Get("path") ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
			Scene scene;
			if (string.IsNullOrWhiteSpace(ident))
			{
				scene = SceneManager.GetActiveScene();
			}
			else
			{
				var sceneRes = ResolveScene(ident);
				if (!sceneRes.IsSuccess) return ErrorResponse.FromResult(sceneRes);
				scene = sceneRes.Value;
			}

			if (format == "json")
				return new SuccessResponse("", new Dictionary<string, object>
				{
					["path"]    = scene.path,
					["name"]    = scene.name,
					["isDirty"] = scene.isDirty,
				});
			return new SuccessResponse("", scene.isDirty ? "true" : "false");
		}

		// ── helpers ─────────────────────────────────────────────────────────

		/// <summary>
		/// Resolves a scene identifier (asset path preferred, name fallback) to
		/// a currently-loaded <see cref="Scene"/>. Ambiguous name matches return
		/// <see cref="ErrorKind.Ambiguous"/> with each candidate path listed.
		/// </summary>
		private static Result<Scene> ResolveScene(string ident)
		{
			if (string.IsNullOrWhiteSpace(ident))
				return Result<Scene>.Error("Scene identifier is empty.");

			// 1. Exact asset-path match.
			for (var i = 0; i < SceneManager.sceneCount; i++)
			{
				var s = SceneManager.GetSceneAt(i);
				if (s.path == ident) return Result<Scene>.Success(s);
			}
			// 2. Name match (fail on ambiguity).
			var matches = new List<Scene>();
			for (var i = 0; i < SceneManager.sceneCount; i++)
			{
				var s = SceneManager.GetSceneAt(i);
				if (s.name == ident) matches.Add(s);
			}
			if (matches.Count == 1) return Result<Scene>.Success(matches[0]);
			if (matches.Count > 1)
			{
				var sb = new StringBuilder($"Scene name '{ident}' matches {matches.Count} loaded scenes:");
				foreach (var m in matches) sb.Append("\n  ").Append(string.IsNullOrEmpty(m.path) ? m.name : m.path);
				return Result<Scene>.Error(sb.ToString(), ErrorKind.Ambiguous);
			}
			return Result<Scene>.Error($"No loaded scene matches '{ident}'.", ErrorKind.NotFound);
		}

		private static object BuildSceneResult(Scene scene, string msg, string format)
		{
			if (format == "json")
				return new SuccessResponse(msg, new Dictionary<string, object>
				{
					["path"]       = scene.path,
					["name"]       = scene.name,
					["isLoaded"]   = scene.isLoaded,
					["isDirty"]    = scene.isDirty,
					["isActive"]   = SceneManager.GetActiveScene() == scene,
					["buildIndex"] = scene.buildIndex,
				});
			// Plain / human mutator output: emit the canonical asset path so
			// `scene open … | scene set-active` pipelines work.
			return new SuccessResponse(msg, string.IsNullOrEmpty(scene.path) ? scene.name : scene.path);
		}
	}
}
