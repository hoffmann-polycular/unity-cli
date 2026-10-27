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
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace UnityCliConnector.InputSimulation
{
	/// <summary>
	/// Owns the virtual devices unity-cli types and clicks with, and the
	/// InputSettings override that lets them reach the game, for one play session.
	///
	/// While a CLI drives the Editor, the Editor window is normally unfocused.
	/// With default settings the Input System then routes pointer and keyboard
	/// events to editor updates, and disables pointer/keyboard devices added in
	/// that state, so the game never sees them. Overriding the settings to
	/// IgnoreFocus + AllDeviceInputAlwaysGoesToGameView *before* adding the
	/// devices delivers queued events to player updates regardless of focus.
	/// </summary>
	internal static class SimulatedInput
	{
		const string MouseName = "unity-cli Mouse";
		const string KeyboardName = "unity-cli Keyboard";
		// SessionState outlives the domain reload a mid-play recompile causes.
		const string OriginalSettingsKey = "UnityCli.Input.OriginalSettingsId";
		const double UpdateTimeoutSeconds = 5;

		static Mouse s_Mouse;
		static Keyboard s_Keyboard;
		static MouseState s_MouseState;

		[InitializeOnLoadMethod]
		static void Init()
		{
			EditorApplication.playModeStateChanged += state =>
			{
				if (state == PlayModeStateChange.ExitingPlayMode)
					Reset();
			};
		}

		public static string NotReadyReason()
		{
#if !ENABLE_INPUT_SYSTEM
			return "Active Input Handling is 'Input Manager (Old)'. unity-cli input drives the Input System only; "
				+ "set Player Settings > Active Input Handling to 'Input System Package' or 'Both'.";
#else
			if (!EditorApplication.isPlaying)
				return "input requires play mode (unity-cli editor play --wait).";
			if (EditorApplication.isPaused)
				return "Play mode is paused; input events are only processed while the game runs.";
			if (InputSystem.settings.updateMode == InputSettings.UpdateMode.ProcessEventsManually)
				return "InputSettings.updateMode is ProcessEventsManually; unity-cli cannot know when the game pumps input.";
			return null;
#endif
		}

		public static Mouse Mouse
		{
			get { EnsureSession(); return s_Mouse; }
		}

		public static Keyboard Keyboard
		{
			get { EnsureSession(); return s_Keyboard; }
		}

		/// <summary>Last queued pointer position, in Unity screen space (bottom-left origin).</summary>
		public static Vector2 MousePosition => s_MouseState.position;

		static void EnsureSession()
		{
			if (s_Mouse != null && s_Mouse.added && s_Keyboard != null && s_Keyboard.added)
				return;

			ApplyFocusOverride();
			s_Mouse = InputSystem.GetDevice(MouseName) as Mouse ?? InputSystem.AddDevice<Mouse>(MouseName);
			s_Keyboard = InputSystem.GetDevice(KeyboardName) as Keyboard ?? InputSystem.AddDevice<Keyboard>(KeyboardName);
			s_MouseState = default;
		}

		static void ApplyFocusOverride()
		{
			var current = InputSystem.settings;
			if (current.backgroundBehavior == InputSettings.BackgroundBehavior.IgnoreFocus
				&& current.editorInputBehaviorInPlayMode == InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView)
				return;

			// Override a copy so a project's InputSettings asset is never dirtied.
			var copy = UnityEngine.Object.Instantiate(current);
			copy.name = current.name;
			copy.hideFlags = HideFlags.HideAndDontSave;
			copy.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
			copy.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
			// Keep the first original: settings swapped mid-session are not the project's.
			if (SessionState.GetInt(OriginalSettingsKey, 0) == 0)
				SessionState.SetInt(OriginalSettingsKey, current.GetInstanceID());
			InputSystem.settings = copy;
		}

		/// <summary>
		/// Releases every button and key on the virtual devices. With
		/// <paramref name="frames"/> 0 the release is only queued, which is
		/// what a failure path wants: waiting could time out again.
		/// </summary>
		public static Task ReleaseAll(int frames)
		{
			if (s_Mouse == null || !s_Mouse.added || s_Keyboard == null || !s_Keyboard.added)
				return Task.CompletedTask;
			s_MouseState.buttons = 0;
			s_MouseState.delta = Vector2.zero;
			s_MouseState.scroll = Vector2.zero;
			InputSystem.QueueStateEvent(s_Mouse, s_MouseState);
			InputSystem.QueueStateEvent(s_Keyboard, new KeyboardState());
			return frames > 0 ? WaitInputUpdates(frames) : Task.CompletedTask;
		}

		/// <summary>
		/// Removes the virtual devices and restores the original InputSettings.
		/// Call <see cref="ReleaseAll"/> first while the game runs, so held input
		/// ends with a release the game sees rather than a vanished device.
		/// </summary>
		public static void Reset()
		{
			foreach (var device in InputSystem.devices.Where(d => d.name == MouseName || d.name == KeyboardName).ToList())
				InputSystem.RemoveDevice(device);
			s_Mouse = null;
			s_Keyboard = null;
			s_MouseState = default;

			var originalId = SessionState.GetInt(OriginalSettingsKey, 0);
			if (originalId == 0)
				return;
			SessionState.EraseInt(OriginalSettingsKey);

			var original = EditorUtility.InstanceIDToObject(originalId) as InputSettings;
			if (original == null)
			{
				Debug.LogWarning("[UnityCliConnector] Could not restore the original InputSettings after input simulation; "
					+ "Background Behavior / Play Mode Input Behavior stay overridden until the next domain reload.");
				return;
			}
			var overridden = InputSystem.settings;
			InputSystem.settings = original;
			if (overridden != original)
				UnityEngine.Object.DestroyImmediate(overridden);
		}

		public static Task QueueMouse(Vector2 position, MouseButton? button, bool pressed, Vector2 scroll, int frames)
		{
			var mouse = Mouse;
			var state = s_MouseState;
			state.delta = position - state.position;
			state.position = position;
			state.scroll = scroll;
			if (button.HasValue)
				state = state.WithButton(button.Value, pressed);
			InputSystem.QueueStateEvent(mouse, state);
			state.scroll = Vector2.zero;
			s_MouseState = state;
			return WaitInputUpdates(frames);
		}

		public static Task QueueKeys(IEnumerable<Key> pressed, int frames)
		{
			InputSystem.QueueStateEvent(Keyboard, new KeyboardState(pressed.ToArray()));
			return WaitInputUpdates(frames);
		}

		public static Task QueueText(string text)
		{
			var keyboard = Keyboard;
			foreach (var c in text)
				InputSystem.QueueTextEvent(keyboard, c);
			return WaitInputUpdates(1);
		}

		/// <summary>
		/// Completes after <paramref name="count"/> input updates of the kind the
		/// game processes events in, so each queued event lands in its own frame
		/// and edge queries like <c>wasPressedThisFrame</c> see it.
		/// </summary>
		public static Task WaitInputUpdates(int count)
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var target = InputSystem.settings.updateMode == InputSettings.UpdateMode.ProcessEventsInFixedUpdate
				? InputUpdateType.Fixed
				: InputUpdateType.Dynamic;
			var deadline = EditorApplication.timeSinceStartup + UpdateTimeoutSeconds;
			var seen = 0;
			Action onAfterUpdate = null;
			EditorApplication.CallbackFunction tick = null;

			void Finish(Exception error)
			{
				InputSystem.onAfterUpdate -= onAfterUpdate;
				EditorApplication.update -= tick;
				if (error == null) tcs.TrySetResult(true);
				else tcs.TrySetException(error);
			}

			onAfterUpdate = () =>
			{
				if (InputState.currentUpdateType == target && ++seen >= count)
					Finish(null);
			};
			tick = () =>
			{
				if (EditorApplication.timeSinceStartup > deadline)
					Finish(new TimeoutException($"No {target} input update within {UpdateTimeoutSeconds}s; is the game running?"));
			};
			InputSystem.onAfterUpdate += onAfterUpdate;
			EditorApplication.update += tick;
			return tcs.Task;
		}

		const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		/// <summary>
		/// The input field that currently has keyboard focus, if any. uGUI InputField
		/// and TMP_InputField read keystrokes from the IMGUI event queue
		/// (Event.PopEvent), which the Input System never feeds and which has no
		/// public way in (GameView.SendEvent does not reach it). Found by shape so
		/// TextMesh Pro needs no assembly reference.
		/// </summary>
		public static Component FocusedTextField()
		{
			var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
			if (selected == null)
				return null;
			foreach (var component in selected.GetComponents<Component>())
			{
				if (component == null) continue;
				var type = component.GetType();
				if (type.GetProperty("isFocused")?.GetValue(component) is bool focused && focused
					&& FindMethod(type, "KeyPressed", typeof(Event)) != null)
					return component;
			}
			return null;
		}

		/// <summary>
		/// Mirrors one KeyDown iteration of the field's OnUpdateSelected loop:
		/// KeyPressed, then submit + deactivate on Finish, else a label refresh.
		/// ProcessEvent(Event) alone skips the refresh, leaving caret data stale
		/// (Backspace then indexes past the old character info).
		/// </summary>
		public static void SendToTextField(Component field, Event evt)
		{
			var type = field.GetType();
			var editState = FindMethod(type, "KeyPressed", typeof(Event)).Invoke(field, new object[] { evt });
			if (editState.ToString() == "Finish")
			{
				var wasCanceled = FindField(type, "m_WasCanceled")?.GetValue(field) is bool canceled && canceled;
				if (!wasCanceled)
					FindMethod(type, "SendOnSubmit")?.Invoke(field, null);
				var deactivate = FindMethod(type, "DeactivateInputField");
				if (deactivate == null)
					deactivate = FindMethod(type, "DeactivateInputField", typeof(bool));
				deactivate?.Invoke(field, deactivate.GetParameters().Length == 0 ? null : new object[] { false });
				return;
			}
			FindField(type, "m_IsTextComponentUpdateRequired")?.SetValue(field, true);
			FindMethod(type, "UpdateLabel")?.Invoke(field, null);
		}

		static MethodInfo FindMethod(Type type, string name, params Type[] parameters)
		{
			for (var t = type; t != null; t = t.BaseType)
			{
				var method = t.GetMethod(name, AnyInstance | BindingFlags.DeclaredOnly, null, parameters, null);
				if (method != null) return method;
			}
			return null;
		}

		static FieldInfo FindField(Type type, string name)
		{
			for (var t = type; t != null; t = t.BaseType)
			{
				var field = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
				if (field != null) return field;
			}
			return null;
		}
	}
}
