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



using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Prefab lifecycle, overrides, and authoring operations.
	///
	/// Subcommands (mirror unity-cli-reference.md §prefab):
	///   status &lt;path&gt;                           — connection + override summary
	///   diff   &lt;path&gt;                           — git-style override delta
	///   apply  &lt;path&gt;[:Comp[.prop]]              — push overrides to source asset
	///   revert &lt;path&gt;[:Comp[.prop]]              — discard overrides
	///   create &lt;scenepath&gt; &lt;assetpath&gt;        — save scene object as prefab
	///
	/// All mutating subcommands run in <see cref="InteractionMode.AutomatedAction"/>
	/// so they never raise modal dialogs (safe for headless / CI use).
	/// </summary>
	[UnityCliTool(Name = "prefab",
		Description = "Prefab status, diff, apply, revert, create. Subcommand via positional or --action.")]
	public static class Prefab
	{
		public class Parameters
		{
			[ToolParameter("Subcommand: status, diff, apply, revert, create.", Required = true)]
			public string Action { get; set; }

			[ToolParameter("Scene path to a GameObject (or :Component / :Component.prop for apply/revert).")]
			public string Path { get; set; }

			[ToolParameter("Target asset path (only for 'prefab create').")]
			public string Asset { get; set; }

			[ToolParameter("Output format: human (default) or json.")]
			public string Format { get; set; }
		}

		public static object HandleCommand(JObject @params)
		{
			var p = new ToolParams(@params);
			var args = p.GetRaw("args") as JArray;
			var paths = p.GetRaw("paths") as JArray;

			// Positional layouts:
			//   status / diff           : [action, path]
			//   apply / revert          : [action, path]
			//   create                  : [action, scenepath, assetpath]
			var action = (p.Get("action")
				?? (args != null && args.Count > 0 ? args[0]?.ToString() : null) ?? "")
				.ToLowerInvariant();

			if (string.IsNullOrEmpty(action))
				return new ErrorResponse("prefab requires an action: status, diff, apply, revert, create.");

			var format = (p.Get("format") ?? "human").ToLowerInvariant();

			// Multi-path mode (stdin or args[]): iterate each path through the
			// same per-action handler. Supports the read actions (status, diff)
			// and the mutators that take a single target (apply, revert, unpack).
			// Mutators run inside one Undo group.
			if (paths != null && paths.Count > 0)
				return DoMulti(action, paths, p, format);

			var path = p.Get("path")
				?? (args != null && args.Count > 1 ? args[1]?.ToString() : null);

			switch (action)
			{
				case "status": return DoStatus(path, format);
				case "diff": return DoDiff(path, format);
				case "apply": return DoApply(path, format);
				case "revert": return DoRevert(path, format);
				case "create":
					{
						var asset = p.Get("asset")
							?? (args != null && args.Count > 2 ? args[2]?.ToString() : null);
						return DoCreate(path, asset, format);
					}
				case "unpack":
					return DoUnpack(path, p.GetBool("completely"), format);
				case "variant":
					{
						// For variant: path is the SOURCE asset, asset is the NEW asset.
						var newAsset = p.Get("asset")
							?? (args != null && args.Count > 2 ? args[2]?.ToString() : null);
						return DoVariant(path, newAsset, format);
					}
				case "open":
					// For open: path is the asset to open.
					return DoOpen(path, format);
				case "close":
					return DoClose(p.GetBool("discard"), format);
				default:
					return new ErrorResponse(
						$"Unknown prefab action '{action}'. Use: status, diff, apply, revert, create, unpack, variant, open, close.");
			}
		}

		// =========================================================================
		// multi-path entry (stdin / args[])
		// =========================================================================

		// Apply the same single-path action to every entry in `paths`. Read
		// actions (status, diff) just collect results. Mutators (apply, revert,
		// unpack) share one Undo group so a single Ctrl-Z reverses the whole
		// batch. Per-path failures route to stderr without halting the rest.
		private static object DoMulti(string action, JArray paths, ToolParams p, string format)
		{
			// Whitelist actions that make sense in multi-path mode. `create`,
			// `variant`, `open`, `close` are single-target-only by construction
			// (asset paths, stage open/close).
			var isMutator = action == "apply" || action == "revert" || action == "unpack";
			var isReader = action == "status" || action == "diff";
			if (!isMutator && !isReader)
				return new ErrorResponse(
					$"prefab {action} does not support multi-path mode (try one path at a time).",
					ErrorKind.Usage);

			int undoGroup = 0;
			if (isMutator)
			{
				undoGroup = Undo.GetCurrentGroup();
				Undo.IncrementCurrentGroup();
				Undo.SetCurrentGroupName($"prefab {action} (multi)");
			}

			var results = new List<object>();
			var errorLines = new List<string>();
			var successCount = 0;
			foreach (var pathTok in paths)
			{
				var pathStr = pathTok?.ToString();
				if (string.IsNullOrWhiteSpace(pathStr)) continue;

				object single = action switch
				{
					"status" => DoStatus(pathStr, "json"),
					"diff"   => DoDiff(pathStr, "json"),
					"apply"  => DoApply(pathStr, "json"),
					"revert" => DoRevert(pathStr, "json"),
					"unpack" => DoUnpack(pathStr, p.GetBool("completely"), "json"),
					_        => new ErrorResponse($"Unknown action '{action}'."),
				};

				switch (single)
				{
					case SuccessResponse sr:
						results.Add(sr.data);
						successCount++;
						break;
					case ErrorResponse er:
						results.Add(new Dictionary<string, object>
						{
							["path"]  = pathStr,
							["ok"]    = false,
							["error"] = er.message,
						});
						errorLines.Add($"{pathStr}: {er.message}");
						break;
				}
			}

			if (isMutator) Undo.CollapseUndoOperations(undoGroup);

			if (successCount == 0)
				return new ErrorResponse(
					errorLines.Count == 0
						? $"prefab {action}: no paths produced a result."
						: string.Join("\n", errorLines));

			var msg = errorLines.Count == 0
				? $"prefab {action} applied to {successCount} object(s)."
				: $"prefab {action} applied to {successCount} object(s); {errorLines.Count} failed.";

			var resp = new SuccessResponse(msg, new Dictionary<string, object>
			{
				["count"]   = paths.Count,
				["results"] = results,
			});
			if (errorLines.Count > 0)
			{
				resp.partialFailure = true;
				resp.stderr = string.Join("\n", errorLines);
			}
			return resp;
		}

		// =========================================================================
		// status
		// =========================================================================

		private static object DoStatus(string path, string format)
		{
			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("prefab status requires a GameObject path.");

			var go = ResolveScene(path, out var err);
			if (go == null) return new ErrorResponse(err);

			var goPath = PathResolver.GetCanonicalPath(go);
			var data = new Dictionary<string, object> { ["path"] = goPath };

			if (!PrefabUtility.IsPartOfAnyPrefab(go))
			{
				data["connected"] = false;
				if (format == "json") return new SuccessResponse("", data);
				return new SuccessResponse("", $"{goPath}\n  (no prefab connection)");
			}

			var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
			var assetType = PrefabUtility.GetPrefabAssetType(go).ToString();
			var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(go).ToString();

			data["connected"] = true;
			data["assetPath"] = assetPath;
			data["assetType"] = assetType;
			data["instanceStatus"] = instanceStatus;
			data["isInstanceRoot"] = instanceRoot == go;
			if (instanceRoot != null)
				data["instanceRootPath"] = PathResolver.GetCanonicalPath(instanceRoot);

			// Override counts — only meaningful at (or rooted at) the instance root.
			if (instanceRoot != null)
			{
				var propMods = PrefabUtility.GetPropertyModifications(instanceRoot);
				var added = PrefabUtility.GetAddedComponents(instanceRoot);
				var removed = PrefabUtility.GetRemovedComponents(instanceRoot);
				var addedGOs = PrefabUtility.GetAddedGameObjects(instanceRoot);

				data["overrides"] = new Dictionary<string, object>
				{
					["propertyModifications"] = CountUserModifications(propMods),
					["addedComponents"] = added != null ? added.Count : 0,
					["removedComponents"] = removed != null ? removed.Count : 0,
					["addedGameObjects"] = addedGOs != null ? addedGOs.Count : 0,
					["any"] = PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, includeDefaultOverrides: false),
				};
			}

			// Nesting chain — walk source links until we hit the on-disk root.
			data["nesting"] = BuildNestingChain(go);

			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse("", RenderStatusHuman(data));
		}

		private static string RenderStatusHuman(Dictionary<string, object> data)
		{
			var sb = new StringBuilder();
			sb.Append(data["path"]).Append('\n');
			sb.Append("  asset: ").Append(data["assetPath"]).Append('\n');
			sb.Append("  type: ").Append(data["assetType"]);
			sb.Append("  status: ").Append(data["instanceStatus"]).Append('\n');
			if (data.TryGetValue("isInstanceRoot", out var isRootObj) && isRootObj is bool isRoot)
				sb.Append("  isInstanceRoot: ").Append(isRoot ? "true" : "false").Append('\n');

			if (data.TryGetValue("overrides", out var ovObj) && ovObj is Dictionary<string, object> ov)
			{
				sb.Append("  overrides:")
				  .Append(" props=").Append(ov["propertyModifications"])
				  .Append("  +comp=").Append(ov["addedComponents"])
				  .Append("  -comp=").Append(ov["removedComponents"])
				  .Append("  +go=").Append(ov["addedGameObjects"])
				  .Append('\n');
			}

			if (data.TryGetValue("nesting", out var nestObj) && nestObj is List<object> nest && nest.Count > 1)
			{
				sb.Append("  nesting:");
				foreach (var item in nest)
					sb.Append("\n    ").Append(item);
			}

			return sb.ToString().TrimEnd('\n');
		}

		// =========================================================================
		// diff
		// =========================================================================

		private static object DoDiff(string path, string format)
		{
			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("prefab diff requires a GameObject path.");

			var go = ResolveScene(path, out var err);
			if (go == null) return new ErrorResponse(err);

			var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			if (instanceRoot == null)
				return new ErrorResponse(
					$"'{PathResolver.GetCanonicalPath(go)}' is not part of a prefab instance.");

			var entries = new List<Dictionary<string, object>>();
			CollectPropertyOverrides(instanceRoot, entries);
			CollectAddedComponents(instanceRoot, entries);
			CollectRemovedComponents(instanceRoot, entries);
			CollectAddedGameObjects(instanceRoot, entries);

			var data = new Dictionary<string, object>
			{
				["path"] = PathResolver.GetCanonicalPath(instanceRoot),
				["asset"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go),
				["entries"] = entries,
			};

			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse("", RenderDiffHuman(data, entries));
		}

		private static string RenderDiffHuman(Dictionary<string, object> data, List<Dictionary<string, object>> entries)
		{
			var sb = new StringBuilder();
			sb.Append(data["path"]).Append("  ←  ").Append(data["asset"]).Append('\n');
			if (entries.Count == 0)
			{
				sb.Append("  (no overrides)");
				return sb.ToString();
			}
			foreach (var e in entries)
			{
				var op = (string)e["op"];
				switch (op)
				{
					case "modify":
						e.TryGetValue("from", out var fromVal);
						e.TryGetValue("to", out var toVal);
						sb.Append("  ~ ").Append(e["target"])
						  .Append("   ").Append(FormatValue(fromVal))
						  .Append(" → ").Append(FormatValue(toVal))
						  .Append('\n');
						break;
					case "addComponent":
						sb.Append("  + ").Append(e["target"]).Append("   (added component)\n");
						break;
					case "removeComponent":
						sb.Append("  - ").Append(e["target"]).Append("   (removed component)\n");
						break;
					case "addGameObject":
						sb.Append("  + ").Append(e["target"]).Append("   (added GameObject)\n");
						break;
				}
			}
			return sb.ToString().TrimEnd('\n');
		}

		// =========================================================================
		// apply
		// =========================================================================

		private static object DoApply(string path, string format)
		{
			return ApplyOrRevert(path, format, isApply: true);
		}

		// =========================================================================
		// revert
		// =========================================================================

		private static object DoRevert(string path, string format)
		{
			return ApplyOrRevert(path, format, isApply: false);
		}

		private static object ApplyOrRevert(string path, string format, bool isApply)
		{
			var verb = isApply ? "apply" : "revert";
			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse($"prefab {verb} requires a path (GameObject, GameObject:Component, or GameObject:Component.prop).");

			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) return ErrorResponse.FromResult(parseResult);
			var parsed = parseResult.Value;

			var goResult = PathResolver.ResolveGameObject(parsed);
			if (!goResult.IsSuccess) return ErrorResponse.FromResult(goResult);
			var go = goResult.Value;

			var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			if (instanceRoot == null)
				return new ErrorResponse(
					$"'{PathResolver.GetCanonicalPath(go)}' is not part of a prefab instance.");

			var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);

			// Path with no component → whole-instance apply/revert.
			if (!parsed.Component.IsPresent)
			{
				try
				{
					if (isApply)
						PrefabUtility.ApplyPrefabInstance(instanceRoot, InteractionMode.AutomatedAction);
					else
						PrefabUtility.RevertPrefabInstance(instanceRoot, InteractionMode.AutomatedAction);
				}
				catch (Exception ex)
				{
					return new ErrorResponse($"prefab {verb} failed: {ex.Message}");
				}

				return Done(verb, "instance",
					PathResolver.GetCanonicalPath(instanceRoot), assetPath, format);
			}

			// Component on GO required.
			var compResult = PathResolver.ResolveComponent(go, parsed.Component);
			if (!compResult.IsSuccess) return ErrorResponse.FromResult(compResult);
			var component = compResult.Value;

			// Path with component but no property → object-level apply/revert.
			if (parsed.Properties == null || parsed.Properties.Count == 0)
			{
				try
				{
					if (isApply)
						PrefabUtility.ApplyObjectOverride(component, assetPath, InteractionMode.AutomatedAction);
					else
						PrefabUtility.RevertObjectOverride(component, InteractionMode.AutomatedAction);
				}
				catch (Exception ex)
				{
					return new ErrorResponse($"prefab {verb} failed: {ex.Message}");
				}

				return Done(verb, "component",
					$"{PathResolver.GetCanonicalPath(go)}:{component.GetType().Name}", assetPath, format);
			}

			// Component + property → property-level apply/revert.
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
						$"No sub-property '{parsed.Properties[i]}' under '{PathResolver.JoinPropertyPath(parsed.Properties, i)}'.");
				current = next;
			}

			if (isApply && !current.prefabOverride)
				return new ErrorResponse(
					$"Property '{PathResolver.JoinPropertyPath(parsed.Properties, parsed.Properties.Count)}' has no override to apply.");
			if (!isApply && !current.prefabOverride)
				return new ErrorResponse(
					$"Property '{PathResolver.JoinPropertyPath(parsed.Properties, parsed.Properties.Count)}' has no override to revert.");

			try
			{
				if (isApply)
					PrefabUtility.ApplyPropertyOverride(current, assetPath, InteractionMode.AutomatedAction);
				else
					PrefabUtility.RevertPropertyOverride(current, InteractionMode.AutomatedAction);
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"prefab {verb} failed: {ex.Message}");
			}

			return Done(verb, "property",
				$"{PathResolver.GetCanonicalPath(go)}:{component.GetType().Name}.{PathResolver.JoinPropertyPath(parsed.Properties, parsed.Properties.Count)}",
				assetPath, format);
		}

		private static object Done(string verb, string scope, string target, string assetPath, string format)
		{
			var data = new Dictionary<string, object>
			{
				["action"] = verb,
				["scope"] = scope,
				["target"] = target,
				["asset"] = assetPath,
			};
			if (format == "json") return new SuccessResponse("", data);
			var preposition = verb == "apply" ? "→" : "←";
			return new SuccessResponse($"{verb} {scope}: {target} {preposition} {assetPath}", data);
		}

		// =========================================================================
		// create
		// =========================================================================

		private static object DoCreate(string scenePath, string assetPath, string format)
		{
			if (string.IsNullOrWhiteSpace(scenePath))
				return new ErrorResponse("prefab create requires a scene GameObject path.");
			if (string.IsNullOrWhiteSpace(assetPath))
				return new ErrorResponse("prefab create requires a target asset path (e.g. Assets/Prefabs/Foo.prefab).");

			var go = ResolveScene(scenePath, out var err);
			if (go == null) return new ErrorResponse(err);

			// Validate / normalize asset path.
			var normalized = assetPath.Replace('\\', '/');
			if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) && normalized != "Assets")
				return new ErrorResponse($"Asset path must start with 'Assets/' (got '{assetPath}').");
			if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
				normalized += ".prefab";

			// Make sure the destination folder exists — Unity won't create it for us.
			var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
			if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
				return new ErrorResponse($"Folder '{folder}' does not exist. Create it first.");

			GameObject created;
			try
			{
				created = PrefabUtility.SaveAsPrefabAssetAndConnect(go, normalized, InteractionMode.AutomatedAction);
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"prefab create failed: {ex.Message}");
			}

			if (created == null)
				return new ErrorResponse($"Failed to create prefab at '{normalized}'.");

			AssetDatabase.SaveAssets();

			var data = new Dictionary<string, object>
			{
				["asset"] = normalized,
				["instancePath"] = PathResolver.GetCanonicalPath(go),
				["instanceId"] = go.GetInstanceID(),
			};
			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse(normalized, data);
		}

		// =========================================================================
		// unpack
		// =========================================================================

		private static object DoUnpack(string path, bool completely, string format)
		{
			if (string.IsNullOrWhiteSpace(path))
				return new ErrorResponse("prefab unpack requires a GameObject path.");

			var go = ResolveScene(path, out var err);
			if (go == null) return new ErrorResponse(err);

			var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			if (instanceRoot == null)
				return new ErrorResponse(
					$"'{PathResolver.GetCanonicalPath(go)}' is not a prefab instance.");

			// Capture asset path BEFORE unpack — afterwards it's gone.
			var wasAsset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
			var rootPath = PathResolver.GetCanonicalPath(instanceRoot);
			var mode = completely
				? PrefabUnpackMode.Completely
				: PrefabUnpackMode.OutermostRoot;

			try
			{
				PrefabUtility.UnpackPrefabInstance(instanceRoot, mode, InteractionMode.AutomatedAction);
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"prefab unpack failed: {ex.Message}");
			}

			EditorUtility.SetDirty(instanceRoot);

			var data = new Dictionary<string, object>
			{
				["path"] = rootPath,
				["wasAsset"] = wasAsset,
				["mode"] = mode.ToString(),
			};
			if (format == "json") return new SuccessResponse("", data);
			var modeNote = completely ? " (all nested layers)" : "";
			return new SuccessResponse(
				$"Unpacked {rootPath}{modeNote} (was instance of {wasAsset}).", data);
		}

		// =========================================================================
		// variant
		// =========================================================================

		private static object DoVariant(string sourceAssetPath, string newAssetPath, string format)
		{
			if (string.IsNullOrWhiteSpace(sourceAssetPath))
				return new ErrorResponse("prefab variant requires a source asset path.");
			if (string.IsNullOrWhiteSpace(newAssetPath))
				return new ErrorResponse("prefab variant requires a target asset path.");

			var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourceAssetPath);
			if (source == null)
				return new ErrorResponse($"Source prefab not found at '{sourceAssetPath}'.");

			var normalized = newAssetPath.Replace('\\', '/');
			if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
				return new ErrorResponse(
					$"Target asset path must start with 'Assets/' (got '{newAssetPath}').");
			if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
				normalized += ".prefab";

			var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
			if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
				return new ErrorResponse($"Folder '{folder}' does not exist. Create it first.");

			// Trick: SaveAsPrefabAsset on a *connected* instance creates a variant
			// (the instance keeps a reference to the source asset). So we
			// instantiate the source temporarily, save, then destroy.
			GameObject tempInstance = null;
			GameObject variant;
			try
			{
				tempInstance = (GameObject)PrefabUtility.InstantiatePrefab(source);
				if (tempInstance == null)
					return new ErrorResponse(
						$"Failed to instantiate source prefab '{sourceAssetPath}'.");

				variant = PrefabUtility.SaveAsPrefabAsset(tempInstance, normalized);
				if (variant == null)
					return new ErrorResponse($"Failed to save variant at '{normalized}'.");
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"prefab variant failed: {ex.Message}");
			}
			finally
			{
				if (tempInstance != null) UnityEngine.Object.DestroyImmediate(tempInstance);
			}

			AssetDatabase.SaveAssets();

			var data = new Dictionary<string, object>
			{
				["source"] = sourceAssetPath,
				["asset"] = normalized,
				["assetType"] = PrefabUtility.GetPrefabAssetType(variant).ToString(),
			};
			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse(normalized, data);
		}

		// =========================================================================
		// open
		// =========================================================================

		private static object DoOpen(string assetPath, string format)
		{
			if (string.IsNullOrWhiteSpace(assetPath))
				return new ErrorResponse("prefab open requires a prefab asset path.");

			var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (asset == null)
				return new ErrorResponse($"Prefab not found at '{assetPath}'.");

			UnityEditor.SceneManagement.PrefabStage stage;
			try
			{
				stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(assetPath);
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"Failed to open prefab stage: {ex.Message}");
			}

			if (stage == null || stage.prefabContentsRoot == null)
				return new ErrorResponse($"Prefab stage failed to open for '{assetPath}'.");

			var data = new Dictionary<string, object>
			{
				["asset"] = assetPath,
				["root"] = stage.prefabContentsRoot.name,
			};
			if (format == "json") return new SuccessResponse("", data);
			return new SuccessResponse(
				$"Opened prefab stage: {assetPath}\n  Paths now resolve under '{stage.prefabContentsRoot.name}'.",
				data);
		}

		// =========================================================================
		// close
		// =========================================================================

		private static object DoClose(bool discard, string format)
		{
			var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
			if (stage == null)
				return new ErrorResponse("No prefab stage is currently open.");

			var assetPath = stage.assetPath;
			var rootName = stage.prefabContentsRoot != null ? stage.prefabContentsRoot.name : "";

			try
			{
				if (discard)
				{
					// Suppress the "save changes?" prompt by clearing the stage's
					// dirty flag before returning to the main stage. The clearing
					// API is internal in older Unity versions, so we try the
					// public ClearDirtiness() first and fall back to reflection.
					ClearStageDirtiness(stage);
				}
				else if (stage.prefabContentsRoot != null)
				{
					// Save changes back to the asset. SaveAsPrefabAsset persists
					// the asset on disk but leaves the stage's in-memory preview
					// scene marked dirty, so the GoToMainStage() below would still
					// trigger Unity's modal "Save changes?" prompt — which blocks
					// the editor main thread and wedges the agent. Clear the stage
					// dirtiness after saving so the transition is silent.
					PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, assetPath);
					AssetDatabase.SaveAssets();
					ClearStageDirtiness(stage);
				}

				UnityEditor.SceneManagement.StageUtility.GoToMainStage();
			}
			catch (Exception ex)
			{
				return new ErrorResponse($"prefab close failed: {ex.Message}");
			}

			var data = new Dictionary<string, object>
			{
				["asset"] = assetPath,
				["root"] = rootName,
				["saved"] = !discard,
			};
			if (format == "json") return new SuccessResponse("", data);
			var note = discard ? "discarded changes" : "saved";
			return new SuccessResponse($"Closed prefab stage ({note}): {assetPath}", data);
		}

		// PrefabStage.ClearDirtiness is public in 2022.1+, internal earlier.
		// Reflection lets us run on either without a compile-time dependency.
		private static void ClearStageDirtiness(UnityEditor.SceneManagement.PrefabStage stage)
		{
			if (stage == null) return;
			var clear = typeof(UnityEditor.SceneManagement.PrefabStage).GetMethod(
				"ClearDirtiness",
				System.Reflection.BindingFlags.Instance
				| System.Reflection.BindingFlags.Public
				| System.Reflection.BindingFlags.NonPublic);
			if (clear != null)
			{
				clear.Invoke(stage, null);
				return;
			}
			// Fallback: clear dirty flag on the underlying scene via internal API.
			var clearScene = typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(
				"ClearSceneDirtiness",
				System.Reflection.BindingFlags.Static
				| System.Reflection.BindingFlags.NonPublic);
			if (clearScene != null) clearScene.Invoke(null, new object[] { stage.scene });
		}

		// =========================================================================
		// helpers
		// =========================================================================

		private static GameObject ResolveScene(string path, out string error)
		{
			error = null;
			var parseResult = PathParser.Parse(path);
			if (!parseResult.IsSuccess) { error = parseResult.ErrorMessage; return null; }
			var goResult = PathResolver.ResolveGameObject(parseResult.Value);
			if (!goResult.IsSuccess) { error = goResult.ErrorMessage; return null; }
			return goResult.Value;
		}

		private static int CountUserModifications(PropertyModification[] mods)
		{
			if (mods == null) return 0;
			var n = 0;
			foreach (var m in mods)
			{
				if (m == null) continue;
				if (IsDefaultOverrideProperty(m.propertyPath)) continue;
				n++;
			}
			return n;
		}

		// Unity stores a handful of always-overridden property paths on every
		// instance root (transform position/rotation, GameObject name, etc.).
		// They show up in GetPropertyModifications but are not user overrides.
		private static bool IsDefaultOverrideProperty(string propPath)
		{
			if (string.IsNullOrEmpty(propPath)) return false;
			return propPath == "m_Name"
				|| propPath == "m_LocalPosition.x" || propPath == "m_LocalPosition.y" || propPath == "m_LocalPosition.z"
				|| propPath == "m_LocalRotation.x" || propPath == "m_LocalRotation.y"
				|| propPath == "m_LocalRotation.z" || propPath == "m_LocalRotation.w"
				|| propPath == "m_LocalEulerAnglesHint.x" || propPath == "m_LocalEulerAnglesHint.y"
				|| propPath == "m_LocalEulerAnglesHint.z";
		}

		private static List<object> BuildNestingChain(GameObject go)
		{
			// Walk source links: instance → its source asset → its source asset, …
			// Each link represents one nested-prefab layer.
			var chain = new List<object>();
			chain.Add(PathResolver.GetCanonicalPath(go));

			var current = (UnityEngine.Object)go;
			var guard = 0;
			while (current != null && guard++ < 16)
			{
				var src = PrefabUtility.GetCorrespondingObjectFromSource(current);
				if (src == null || src == current) break;
				var srcPath = AssetDatabase.GetAssetPath(src);
				if (string.IsNullOrEmpty(srcPath)) break;
				chain.Add(srcPath);
				current = src;
			}
			return chain;
		}

		// ---- diff collectors ----

		private static void CollectPropertyOverrides(GameObject instanceRoot, List<Dictionary<string, object>> entries)
		{
			var overrides = PrefabUtility.GetObjectOverrides(instanceRoot, includeDefaultOverrides: false);
			if (overrides == null) return;
			foreach (var ov in overrides)
			{
				if (ov?.instanceObject == null) continue;
				var modified = ov.instanceObject;
				var source = PrefabUtility.GetCorrespondingObjectFromSource(modified);

				using var instSO = new SerializedObject(modified);
				using var srcSO = source != null ? new SerializedObject(source) : null;

				var targetPrefix = BuildTargetPrefix(instanceRoot, modified);

				var it = instSO.GetIterator();
				var enterChildren = true;
				while (it.NextVisible(enterChildren))
				{
					enterChildren = false;
					if (!it.prefabOverride) continue;
					if (IsDefaultOverrideProperty(it.propertyPath)) continue;
					if (it.propertyType == SerializedPropertyType.Generic) continue;

					var humanName = PathResolver.NormalizeSerializedName(it.name);
					var to = SerializedPropertyReader.Read(it);
					object from = null;
					if (srcSO != null)
					{
						var srcProp = srcSO.FindProperty(it.propertyPath);
						if (srcProp != null) from = SerializedPropertyReader.Read(srcProp);
					}

					entries.Add(new Dictionary<string, object>
					{
						["op"] = "modify",
						["target"] = $"{targetPrefix}.{humanName}",
						["from"] = from,
						["to"] = to,
					});
				}
			}
		}

		private static void CollectAddedComponents(GameObject instanceRoot, List<Dictionary<string, object>> entries)
		{
			var added = PrefabUtility.GetAddedComponents(instanceRoot);
			if (added == null) return;
			foreach (var a in added)
			{
				if (a?.instanceComponent == null) continue;
				var go = a.instanceComponent.gameObject;
				entries.Add(new Dictionary<string, object>
				{
					["op"] = "addComponent",
					["target"] = $"{PathResolver.GetCanonicalPath(go)}:{a.instanceComponent.GetType().Name}",
				});
			}
		}

		private static void CollectRemovedComponents(GameObject instanceRoot, List<Dictionary<string, object>> entries)
		{
			var removed = PrefabUtility.GetRemovedComponents(instanceRoot);
			if (removed == null) return;
			foreach (var r in removed)
			{
				if (r?.assetComponent == null) continue;
				// containingInstanceGameObject is where the removal applies.
				var go = r.containingInstanceGameObject;
				var path = go != null ? PathResolver.GetCanonicalPath(go) : "?";
				entries.Add(new Dictionary<string, object>
				{
					["op"] = "removeComponent",
					["target"] = $"{path}:{r.assetComponent.GetType().Name}",
				});
			}
		}

		private static void CollectAddedGameObjects(GameObject instanceRoot, List<Dictionary<string, object>> entries)
		{
			var added = PrefabUtility.GetAddedGameObjects(instanceRoot);
			if (added == null) return;
			foreach (var a in added)
			{
				if (a?.instanceGameObject == null) continue;
				entries.Add(new Dictionary<string, object>
				{
					["op"] = "addGameObject",
					["target"] = PathResolver.GetCanonicalPath(a.instanceGameObject),
				});
			}
		}

		// "GO[:Component]" prefix for diff entries — caller appends ".propertyName".
		private static string BuildTargetPrefix(GameObject instanceRoot, UnityEngine.Object modified)
		{
			GameObject hostGo;
			string compPart = null;
			if (modified is GameObject mgo)
			{
				hostGo = mgo;
			}
			else if (modified is Component mc)
			{
				hostGo = mc.gameObject;
				compPart = mc.GetType().Name;
			}
			else
			{
				return modified.name;
			}

			var goPath = PathResolver.GetCanonicalPath(hostGo);
			if (compPart != null) return $"{goPath}:{compPart}";
			return goPath;
		}


		private static string FormatValue(object value)
		{
			if (value == null) return "null";
			if (value is string s) return s;
			if (value is bool b) return b ? "true" : "false";
			if (value is Dictionary<string, object> dict)
			{
				if (LooksLikeVector(dict))
				{
					var sb = new StringBuilder("(");
					var first = true;
					foreach (var k in OrderedKeys(dict))
					{
						if (!first) sb.Append(' ');
						first = false;
						sb.Append(dict[k]);
					}
					sb.Append(')');
					return sb.ToString();
				}
				if (dict.TryGetValue("path", out var p) && p is string ps && !string.IsNullOrEmpty(ps))
					return ps;
				if (dict.TryGetValue("asset", out var a) && a is string assetStr && !string.IsNullOrEmpty(assetStr))
					return assetStr;
			}
			return value.ToString();
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

		private static IEnumerable<string> OrderedKeys(Dictionary<string, object> dict)
		{
			string[] order = { "x", "y", "z", "w", "r", "g", "b", "a" };
			foreach (var k in order)
				if (dict.ContainsKey(k)) yield return k;
		}
	}
}
