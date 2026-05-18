using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
	/// <summary>
	/// Thin façade over Unity's per-group settings singletons. Maps a
	/// "ProjectSettings/&lt;Group&gt;" path component to a
	/// <see cref="SerializedObject"/> that <c>get</c>/<c>set</c>/<c>inspect</c>
	/// can use the same way they use a component's serialized object.
	///
	/// The recognized groups match the most common Project Settings panes;
	/// a <c>list</c> entry exposes the catalog. Group names are
	/// case-insensitive on input but echoed back canonically.
	/// </summary>
	public static class ProjectSettingsResolver
	{
		private struct GroupEntry
		{
			public string CanonicalName;
			public System.Func<UnityEngine.Object[]> Loader;
		}

		// Loaders return UnityEngine.Object[] so callers can wrap them in a
		// SerializedObject (which prefers an array for multi-object editing
		// but works fine with one element).
		private static readonly Dictionary<string, GroupEntry> Groups
			= new(System.StringComparer.OrdinalIgnoreCase)
		{
			["Physics"] = new GroupEntry
			{
				CanonicalName = "Physics",
				Loader = () => LoadAssets("ProjectSettings/DynamicsManager.asset"),
			},
			["Physics2D"] = new GroupEntry
			{
				CanonicalName = "Physics2D",
				Loader = () => LoadAssets("ProjectSettings/Physics2DSettings.asset"),
			},
			["Graphics"] = new GroupEntry
			{
				CanonicalName = "Graphics",
				Loader = () => LoadAssets("ProjectSettings/GraphicsSettings.asset"),
			},
			["Quality"] = new GroupEntry
			{
				CanonicalName = "Quality",
				Loader = () => LoadAssets("ProjectSettings/QualitySettings.asset"),
			},
			["Player"] = new GroupEntry
			{
				CanonicalName = "Player",
				Loader = () => LoadAssets("ProjectSettings/ProjectSettings.asset"),
			},
			["Input"] = new GroupEntry
			{
				CanonicalName = "Input",
				Loader = () => LoadAssets("ProjectSettings/InputManager.asset"),
			},
			["InputManager"] = new GroupEntry
			{
				CanonicalName = "InputManager",
				Loader = () => LoadAssets("ProjectSettings/InputManager.asset"),
			},
			["Time"] = new GroupEntry
			{
				CanonicalName = "Time",
				Loader = () => LoadAssets("ProjectSettings/TimeManager.asset"),
			},
			["Audio"] = new GroupEntry
			{
				CanonicalName = "Audio",
				Loader = () => LoadAssets("ProjectSettings/AudioManager.asset"),
			},
			["Tags"] = new GroupEntry
			{
				CanonicalName = "Tags",
				Loader = () => LoadAssets("ProjectSettings/TagManager.asset"),
			},
			["TagManager"] = new GroupEntry
			{
				CanonicalName = "TagManager",
				Loader = () => LoadAssets("ProjectSettings/TagManager.asset"),
			},
			["Editor"] = new GroupEntry
			{
				CanonicalName = "Editor",
				Loader = () => LoadAssets("ProjectSettings/EditorSettings.asset"),
			},
			["EditorSettings"] = new GroupEntry
			{
				CanonicalName = "EditorSettings",
				Loader = () => LoadAssets("ProjectSettings/EditorSettings.asset"),
			},
			["EditorBuildSettings"] = new GroupEntry
			{
				CanonicalName = "EditorBuildSettings",
				Loader = () => LoadAssets("ProjectSettings/EditorBuildSettings.asset"),
			},
			["NavMesh"] = new GroupEntry
			{
				CanonicalName = "NavMesh",
				Loader = () => LoadAssets("ProjectSettings/NavMeshAreas.asset"),
			},
			["VFX"] = new GroupEntry
			{
				CanonicalName = "VFX",
				Loader = () => LoadAssets("ProjectSettings/VFXManager.asset"),
			},
		};

		/// <summary>Lists all recognized group names in canonical casing.</summary>
		public static List<string> ListGroups()
		{
			var seen = new HashSet<string>();
			var list = new List<string>();
			foreach (var kv in Groups)
			{
				if (seen.Add(kv.Value.CanonicalName))
					list.Add(kv.Value.CanonicalName);
			}
			list.Sort(System.StringComparer.Ordinal);
			return list;
		}

		/// <summary>
		/// Loads the SerializedObject for a settings group. Returns an error
		/// when the group name is unknown or the underlying asset can't be
		/// loaded. Caller owns disposal.
		/// </summary>
		public static Result<SerializedObject> LoadGroup(string group)
		{
			if (string.IsNullOrEmpty(group))
				return Result<SerializedObject>.Error(
					"ProjectSettings path needs a group, e.g. 'ProjectSettings/Physics'.", ErrorKind.Usage);
			if (!Groups.TryGetValue(group, out var entry))
				return Result<SerializedObject>.Error(
					$"Unknown ProjectSettings group '{group}'. Try one of: {string.Join(", ", ListGroups())}.",
					ErrorKind.NotFound);

			var assets = entry.Loader();
			if (assets == null || assets.Length == 0 || assets[0] == null)
				return Result<SerializedObject>.Error(
					$"Could not load asset for ProjectSettings/{group}.", ErrorKind.NotFound);
			return Result<SerializedObject>.Success(new SerializedObject(assets[0]));
		}

		/// <summary>
		/// Builds a canonical "ProjectSettings/&lt;Group&gt;" path for use
		/// in tool output.
		/// </summary>
		public static string CanonicalPath(string group)
		{
			if (string.IsNullOrEmpty(group)) return "ProjectSettings";
			if (Groups.TryGetValue(group, out var entry))
				return $"ProjectSettings/{entry.CanonicalName}";
			return $"ProjectSettings/{group}";
		}

		// ---- internals ----

		private static UnityEngine.Object[] LoadAssets(string path)
		{
			var loaded = AssetDatabase.LoadAllAssetsAtPath(path);
			if (loaded == null || loaded.Length == 0)
			{
				var single = AssetDatabase.LoadMainAssetAtPath(path);
				if (single == null) return new UnityEngine.Object[0];
				return new UnityEngine.Object[] { single };
			}
			// First non-null element wins (the main settings object usually
			// is the first; the rest may be sub-assets we don't care about).
			foreach (var obj in loaded)
				if (obj != null) return new UnityEngine.Object[] { obj };
			return new UnityEngine.Object[0];
		}
	}
}
