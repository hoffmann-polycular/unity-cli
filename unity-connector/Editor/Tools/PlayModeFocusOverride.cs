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
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    /// <summary>
    /// Overrides the Game view's "Play Focused / Play Maximized / Play Unfocused"
    /// choice for a single play-mode entry, then restores the user's setting.
    /// The setting lives on the internal PlayModeView.enterPlayModeBehavior, so
    /// it is reached by reflection.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeFocusOverride
    {
        // SessionState outlives the domain reload that entering play mode causes.
        // Format: "instanceId=EnumName;instanceId=EnumName".
        const string OriginalsKey = "UnityCli.PlayModeFocus.Originals";

        static readonly Type s_ViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
        static readonly PropertyInfo s_Behavior = s_ViewType?.GetProperty("enterPlayModeBehavior",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static readonly Dictionary<string, string> s_EnumNames = new Dictionary<string, string>
        {
            { "focused", "PlayFocused" },
            { "unfocused", "PlayUnfocused" },
            { "maximized", "PlayMaximized" },
        };

        static PlayModeFocusOverride()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                // Deferred: Unity consumes the setting in its own handlers for
                // this same event, which may run after this one.
                if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
                    EditorApplication.delayCall += Restore;
            };
        }

        /// <summary>
        /// Sets every open play-mode view to <paramref name="focus"/> (focused,
        /// unfocused or maximized). Returns an error message, or null on success.
        /// <paramref name="applied"/> is false when no Game view is open: Unity
        /// then creates one with its default behavior.
        /// </summary>
        public static string Apply(string focus, out bool applied)
        {
            applied = false;
            if (!s_EnumNames.TryGetValue(focus.ToLowerInvariant(), out var enumName))
                return $"Invalid play_focus '{focus}'. Valid: focused, unfocused, maximized.";
            if (s_Behavior == null)
                return "This Unity version does not expose PlayModeView.enterPlayModeBehavior; "
                    + "play without a focus flag to use the Game view's own setting.";

            var target = Enum.Parse(s_Behavior.PropertyType, enumName);
            var views = Resources.FindObjectsOfTypeAll(s_ViewType);
            if (views.Length == 0)
                return null;

            // Keep the first originals: a second override before a restore would
            // otherwise record our own value as the user's.
            if (string.IsNullOrEmpty(SessionState.GetString(OriginalsKey, null)))
            {
                SessionState.SetString(OriginalsKey, string.Join(";",
                    views.Select(v => $"{v.GetInstanceID()}={s_Behavior.GetValue(v)}")));
            }
            foreach (var view in views)
                s_Behavior.SetValue(view, target);
            applied = true;

            // Entering play mode can be refused (e.g. compile errors) without any
            // play-mode event, which would leave the override in place.
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    Restore();
            };
            return null;
        }

        static void Restore()
        {
            var originals = SessionState.GetString(OriginalsKey, null);
            if (string.IsNullOrEmpty(originals) || s_Behavior == null)
                return;
            SessionState.EraseString(OriginalsKey);

            foreach (var entry in originals.Split(';'))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var id))
                    continue;
                var view = EditorUtility.InstanceIDToObject(id);
                if (view == null || !s_ViewType.IsInstanceOfType(view))
                    continue;
                s_Behavior.SetValue(view, Enum.Parse(s_Behavior.PropertyType, parts[1]));
            }
        }
    }
}
