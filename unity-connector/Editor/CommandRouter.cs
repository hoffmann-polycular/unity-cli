// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Routes incoming command requests to the appropriate tool handler.
    /// All requests are serialized through a single queue to prevent
    /// race conditions when multiple CLI agents access the same Unity instance.
    /// </summary>
    public static class CommandRouter
    {
        static readonly SemaphoreSlim s_Lock = new(1, 1);

        public static async Task<object> Dispatch(string command, JObject parameters)
        {
            await s_Lock.WaitAsync();
            try
            {
                return await DispatchInternal(command, parameters);
            }
            finally
            {
                s_Lock.Release();
            }
        }

        /// <summary>
        /// `list` is routed here rather than being a [UnityCliTool] so it still
        /// answers when tool discovery itself is the thing being inspected.
        /// Optional `name` / `group` filters (or a bare positional name) narrow
        /// the result to a single tool or one group.
        /// </summary>
        static object List(JObject parameters)
        {
            var p = new ToolParams(parameters ?? new JObject());
            var name = p.Get("name") ?? (p.GetRaw("args") as JArray)?.First?.ToString();
            var group = p.Get("group");

            var tools = ToolDiscovery.GetToolSchemas(name, group);
            if (tools.Count == 0 && (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(group)))
            {
                var what = !string.IsNullOrEmpty(name)
                    ? $"tool '{name}'"
                    : $"tools in group '{group}'";
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(group))
                    what = $"tool '{name}' in group '{group}'";
                return ErrorResponse.NotFound($"No registered {what}.");
            }

            return new SuccessResponse("Available tools", tools);
        }

        static async Task<object> DispatchInternal(string command, JObject parameters)
        {
            if (command == "list")
                return List(parameters);

            var handler = ToolDiscovery.FindHandler(command);
            if (handler == null)
                return new ErrorResponse($"Unknown command: {command}");

            try
            {
                var result = handler.Invoke(null, new object[] { parameters ?? new JObject() });

                if (result is Task<object> asyncTask)
                    return await asyncTask;

                if (result is Task task)
                {
                    await task;
                    return new SuccessResponse($"{command} completed");
                }

                return result ?? new SuccessResponse($"{command} completed");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Debug.LogException(inner);
                return new ErrorResponse($"{command} failed: {inner.Message}");
            }
        }
    }
}
