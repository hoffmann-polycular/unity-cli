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


#if !(UNITY_CLI_INPUT_SYSTEM && UNITY_CLI_UGUI)
using Newtonsoft.Json.Linq;

namespace UnityCliConnector.Tools
{
	/// <summary>
	/// Stands in for the real <c>input</c> tool (UnityCliConnector.InputSystem.Editor),
	/// whose assembly only compiles when its packages are installed.
	/// </summary>
	[UnityCliTool(Name = "input",
		Description = "Simulate real mouse/keyboard input in play mode. Unavailable: needs com.unity.inputsystem >= 1.7.0 and com.unity.ugui.")]
	public static class InputUnavailable
	{
		public static object HandleCommand(JObject parameters)
		{
			return new ErrorResponse("unity-cli input needs the Input System package (com.unity.inputsystem >= 1.7.0) "
				+ "and uGUI (com.unity.ugui) in this project.");
		}
	}
}
#endif
