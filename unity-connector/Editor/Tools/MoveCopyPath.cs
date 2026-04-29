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



using UnityEngine;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Helpers shared by <c>cp</c> and <c>mv</c> for splitting destination
	/// paths into (parentPath, newName) and walking ancestry.
	/// </summary>
	internal static class MoveCopyPath
	{
		/// <summary>
		/// Sentinel returned in <c>parentPath</c> when the destination is the
		/// scene root (no parent). The handler interprets this as "use a null
		/// Transform parent".
		/// </summary>
		public const string SceneRootSentinel = "";

		/// <summary>
		/// Splits a destination path into (parentPath, newName).
		///   "A/B/C"  → ("A/B", "C")
		///   "A/B/"   → ("A/B", srcName)
		///   "/Name"  → (SceneRoot, "Name")
		///   "/"      → (SceneRoot, srcName)
		/// </summary>
		public static Result<(string parentPath, string newName)> Split(string dst, string srcName)
		{
			if (string.IsNullOrEmpty(dst))
				return Result<(string, string)>.Error("Destination path is empty.");

			// Scene-root forms.
			if (dst == "/")
				return Result<(string, string)>.Success((SceneRootSentinel, srcName));
			if (dst.StartsWith("/"))
			{
				var afterSlash = dst.Substring(1);
				if (afterSlash.Contains("/"))
				{
					// "/A/B" → scene root + nested target. Treat the leading
					// slash as scene-root anchor; first segment is the new name
					// only if there are no further slashes. Reject ambiguity.
					return Result<(string, string)>.Error(
						"A leading '/' anchors the scene root; the remainder must be a single name (no further slashes).");
				}
				return Result<(string, string)>.Success((SceneRootSentinel, afterSlash));
			}

			if (dst.EndsWith("/"))
			{
				var parent = dst.TrimEnd('/');
				return Result<(string, string)>.Success((parent, srcName));
			}

			var lastSlash = dst.LastIndexOf('/');
			if (lastSlash < 0)
				return Result<(string, string)>.Error(
					"Destination must include a parent path. Use 'parent/name', 'parent/' to keep the source name, or '/Name' for the scene root.");

			var parentPath = dst.Substring(0, lastSlash);
			var newName = dst.Substring(lastSlash + 1);
			if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(newName))
				return Result<(string, string)>.Error(
					"Destination parent and name must both be non-empty.");
			return Result<(string, string)>.Success((parentPath, newName));
		}

		/// <summary>True when <paramref name="parentPath"/> is the scene-root sentinel.</summary>
		public static bool IsSceneRoot(string parentPath) => parentPath == SceneRootSentinel;

		/// <summary>
		/// True when the active or some loaded scene already has a root
		/// GameObject named <paramref name="name"/>.
		/// </summary>
		public static bool SceneRootHasName(string name)
		{
			for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
			{
				var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
				if (!s.IsValid() || !s.isLoaded) continue;
				foreach (var r in s.GetRootGameObjects())
					if (r.name == name) return true;
			}
			return false;
		}

		/// <summary>
		/// Returns true if <paramref name="ancestor"/> is the same as or an
		/// ancestor of <paramref name="descendant"/>.
		/// </summary>
		public static bool IsAncestor(Transform ancestor, Transform descendant)
		{
			var t = descendant;
			while (t != null)
			{
				if (t == ancestor) return true;
				t = t.parent;
			}
			return false;
		}

		/// <summary>
		/// Picks a non-colliding sibling name under <paramref name="parent"/>
		/// using <paramref name="format"/> (with <c>{n}</c> as the index
		/// placeholder). When <paramref name="format"/> is null, the desired
		/// name is returned unchanged — Unity allows duplicate sibling names.
		/// </summary>
		public static string PickName(Transform parent, string desired, string format)
		{
			if (format == null) return desired;
			if (!HasCollision(parent, desired)) return desired;

			var n = 1;
			while (true)
			{
				var candidate = desired + format.Replace("{n}", n.ToString());
				if (!HasCollision(parent, candidate)) return candidate;
				n++;
				if (n > 100000) return candidate; // pathological guard
			}
		}

		private static bool HasCollision(Transform parent, string name)
		{
			if (parent == null) return SceneRootHasName(name);
			for (var i = 0; i < parent.childCount; i++)
				if (parent.GetChild(i).gameObject.name == name) return true;
			return false;
		}
	}
}
