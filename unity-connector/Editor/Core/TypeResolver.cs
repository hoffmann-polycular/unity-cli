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
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Resolves user-supplied type names (e.g. "Rigidbody", "MeshRenderer")
    /// to <see cref="Type"/> instances of Unity components.
    ///
    /// Lookup order (stops at first match):
    ///   1. Fully-qualified type name (any loaded assembly)
    ///   2. Simple name in a common Unity namespace (UnityEngine, UnityEngine.UI,
    ///      UnityEngine.Rendering, UnityEditor)
    ///   3. Simple-name scan across every loaded assembly (user scripts)
    ///
    /// Only types that derive from <see cref="Component"/> are returned.
    /// </summary>
    public static class TypeResolver
    {
        private static readonly string[] CommonNamespaces =
        {
            "UnityEngine",
            "UnityEngine.UI",
            "UnityEngine.Rendering",
            "UnityEngine.Animations",
            "UnityEditor",
        };

        public static Type ResolveComponentType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            // 1. Fully qualified
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(trimmed, throwOnError: false, ignoreCase: false);
                if (IsComponent(t)) return t;
            }

            // 2. Common namespaces
            foreach (var prefix in CommonNamespaces)
            {
                var qualified = $"{prefix}.{trimmed}";
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(qualified, throwOnError: false, ignoreCase: false);
                    if (IsComponent(t)) return t;
                }
            }

            // 3. Simple-name scan (user assemblies)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (!IsComponent(t)) continue;
                    if (t.Name == trimmed) return t;
                }
            }

            return null;
        }

        private static bool IsComponent(Type t)
        {
            return t != null && typeof(Component).IsAssignableFrom(t);
        }
    }
}
