using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Strodio.McpAddressables
{
    [InitializeOnLoad]
    public static class McpHttpServer
    {
        const int Port = 8091;
        const string Prefix = "http://localhost:8091/";

        static HttpListener _listener;
        static Thread _listenerThread;
        static volatile bool _running;
        static readonly ConcurrentQueue<(HttpListenerContext ctx, string endpoint, string query, string body, string method)> _pending = new();

        public static bool IsRunning => _running;

        static McpHttpServer()
        {
            Start();
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.update += ProcessPending;
            EditorApplication.quitting += Stop;
        }

        public static void Start()
        {
            if (_running) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(Prefix);
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop) { IsBackground = true };
                _listenerThread.Start();

                Debug.Log($"[MCP Addressables] HTTP Server started on port {Port}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP Addressables] Failed to start HTTP server: {e.Message}");
            }
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }

            _listener = null;
            Debug.Log("[MCP Addressables] HTTP Server stopped");
        }

        static void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                    string query = ctx.Request.Url.Query;
                    string method = ctx.Request.HttpMethod;

                    // Handle ping directly on background thread
                    if (path == "/api/ping")
                    {
                        Respond(ctx, "{\"status\":\"ok\"}");
                        continue;
                    }

                    // Read body for POST requests
                    string body = null;
                    if (method == "POST" && ctx.Request.HasEntityBody)
                    {
                        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        {
                            body = reader.ReadToEnd();
                        }
                    }

                    // Queue for main thread
                    _pending.Enqueue((ctx, path, query, body, method));
                }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
                catch (Exception e)
                {
                    if (_running)
                        Debug.LogError($"[MCP Addressables] Listener error: {e.Message}");
                }
            }
        }

        static void ProcessPending()
        {
            int processed = 0;
            while (_pending.TryDequeue(out var item) && processed < 5)
            {
                processed++;
                try
                {
                    HandleRequest(item.ctx, item.endpoint, item.query, item.body, item.method);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MCP Addressables] Error handling {item.endpoint}: {e}");
                    RespondError(item.ctx, 500, e.Message);
                }
            }
        }

        static void HandleRequest(HttpListenerContext ctx, string endpoint, string query, string body, string method)
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

            var queryParams = ParseQuery(query);

            switch (endpoint)
            {
                // ─── READ endpoints (GET) ───

                case "/api/list-groups":
                {
                    var result = AddressablesAnalyzer.ListGroups();
                    Respond(ctx, JsonDictList("groups", result));
                    break;
                }
                case "/api/get-group-entries":
                {
                    string groupName = queryParams.GetValueOrDefault("group_name", "");
                    if (string.IsNullOrEmpty(groupName))
                    {
                        RespondError(ctx, 400, "Missing 'group_name' parameter");
                        return;
                    }
                    var result = AddressablesAnalyzer.GetGroupEntries(groupName);
                    Respond(ctx, JsonDictList("entries", result));
                    break;
                }
                case "/api/get-group-settings":
                {
                    string groupName = queryParams.GetValueOrDefault("group_name", "");
                    if (string.IsNullOrEmpty(groupName))
                    {
                        RespondError(ctx, 400, "Missing 'group_name' parameter");
                        return;
                    }
                    var result = AddressablesAnalyzer.GetGroupSettings(groupName);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/get-entry-dependencies":
                {
                    string groupName = queryParams.GetValueOrDefault("group_name", "");
                    if (string.IsNullOrEmpty(groupName))
                    {
                        RespondError(ctx, 400, "Missing 'group_name' parameter");
                        return;
                    }
                    var result = AddressablesAnalyzer.GetEntryDependencies(groupName);
                    Respond(ctx, JsonDictList("dependencies", result));
                    break;
                }
                case "/api/find-entry-by-address":
                {
                    string address = queryParams.GetValueOrDefault("address", "");
                    if (string.IsNullOrEmpty(address))
                    {
                        RespondError(ctx, 400, "Missing 'address' parameter");
                        return;
                    }
                    var result = AddressablesAnalyzer.FindEntryByAddress(address);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/get-addressables-settings":
                {
                    var result = AddressablesAnalyzer.GetAddressablesSettings();
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/analyze-group-dependencies":
                {
                    var result = AddressablesAnalyzer.AnalyzeGroupDependencies();
                    Respond(ctx, JsonDictList("shared_assets", result));
                    break;
                }
                case "/api/list-labels":
                {
                    var result = AddressablesAnalyzer.ListLabels();
                    Respond(ctx, JsonList("labels", result));
                    break;
                }

                // ─── WRITE endpoints (POST) ───

                case "/api/create-group":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    string name = ParseJsonString(body, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        RespondError(ctx, 400, "Missing 'name' in request body");
                        return;
                    }
                    var schemas = ParseJsonStringArray(body, "schemas");
                    var result = AddressablesAnalyzer.CreateGroup(name, schemas);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/move-entries":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    var guids = ParseJsonStringArray(body, "guids");
                    string targetGroup = ParseJsonString(body, "target_group");
                    if (guids == null || guids.Count == 0 || string.IsNullOrEmpty(targetGroup))
                    {
                        RespondError(ctx, 400, "Missing 'guids' array or 'target_group' in request body");
                        return;
                    }
                    var result = AddressablesAnalyzer.MoveEntries(guids, targetGroup);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/set-entry-address":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    string guid = ParseJsonString(body, "guid");
                    string address = ParseJsonString(body, "address");
                    if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(address))
                    {
                        RespondError(ctx, 400, "Missing 'guid' or 'address' in request body");
                        return;
                    }
                    var result = AddressablesAnalyzer.SetEntryAddress(guid, address);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/add-entry":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    string assetPath = ParseJsonString(body, "asset_path");
                    string groupName = ParseJsonString(body, "group_name");
                    if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(groupName))
                    {
                        RespondError(ctx, 400, "Missing 'asset_path' or 'group_name' in request body");
                        return;
                    }
                    string addr = ParseJsonString(body, "address");
                    var result = AddressablesAnalyzer.AddEntry(assetPath, groupName, addr);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/remove-entry":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    string guid = ParseJsonString(body, "guid");
                    if (string.IsNullOrEmpty(guid))
                    {
                        RespondError(ctx, 400, "Missing 'guid' in request body");
                        return;
                    }
                    var result = AddressablesAnalyzer.RemoveEntry(guid);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/set-entry-labels":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    string guid = ParseJsonString(body, "guid");
                    var labels = ParseJsonStringArray(body, "labels");
                    if (string.IsNullOrEmpty(guid) || labels == null)
                    {
                        RespondError(ctx, 400, "Missing 'guid' or 'labels' in request body");
                        return;
                    }
                    bool exclusive = ParseJsonBool(body, "exclusive", false);
                    var result = AddressablesAnalyzer.SetEntryLabels(guid, labels, exclusive);
                    Respond(ctx, JsonDict(result));
                    break;
                }
                case "/api/rename-group":
                {
                    if (method != "POST") { RespondError(ctx, 405, "Use POST"); return; }
                    string oldName = ParseJsonString(body, "old_name");
                    string newName = ParseJsonString(body, "new_name");
                    if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                    {
                        RespondError(ctx, 400, "Missing 'old_name' or 'new_name' in request body");
                        return;
                    }
                    var result = AddressablesAnalyzer.RenameGroup(oldName, newName);
                    Respond(ctx, JsonDict(result));
                    break;
                }

                default:
                    RespondError(ctx, 404, $"Unknown endpoint: {endpoint}");
                    break;
            }
        }

        // ─── JSON body parsing (lightweight regex-based) ───

        static string ParseJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!match.Success) return null;
            return match.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t");
        }

        static List<string> ParseJsonStringArray(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!match.Success) return null;
            var result = new List<string>();
            var items = Regex.Matches(match.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"");
            foreach (Match item in items)
            {
                result.Add(item.Groups[1].Value
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\"));
            }
            return result;
        }

        static bool ParseJsonBool(string json, string key, bool defaultValue)
        {
            if (string.IsNullOrEmpty(json)) return defaultValue;
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(true|false)");
            if (!match.Success) return defaultValue;
            return match.Groups[1].Value == "true";
        }

        // ─── Query string parsing ───

        static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return result;

            query = query.TrimStart('?');
            foreach (var pair in query.Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                    result[UrlDecode(kv[0])] = UrlDecode(kv[1]);
            }
            return result;
        }

        static string UrlDecode(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        // ─── JSON serialization helpers ───

        static void Respond(HttpListenerContext ctx, string json)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = data.Length;
                ctx.Response.OutputStream.Write(data, 0, data.Length);
                ctx.Response.Close();
            }
            catch { }
        }

        static void RespondError(HttpListenerContext ctx, int code, string message)
        {
            try
            {
                ctx.Response.StatusCode = code;
                Respond(ctx, $"{{\"error\":{JsonEscape(message)}}}");
            }
            catch { }
        }

        static string JsonEscape(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                           .Replace("\n", "\\n").Replace("\r", "\\r")
                           .Replace("\t", "\\t") + "\"";
        }

        static string JsonList(string key, List<string> items)
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"{key}\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonEscape(items[i]));
            }
            sb.Append($"],\"count\":{items.Count}}}");
            return sb.ToString();
        }

        static string JsonDictList(string key, List<Dictionary<string, object>> items)
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"{key}\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonDict(items[i]));
            }
            sb.Append($"],\"count\":{items.Count}}}");
            return sb.ToString();
        }

        public static string JsonDict(Dictionary<string, object> dict)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonEscape(kv.Key));
                sb.Append(':');
                sb.Append(JsonValue(kv.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string JsonValue(object val)
        {
            if (val == null) return "null";
            if (val is string s) return JsonEscape(s);
            if (val is bool b) return b ? "true" : "false";
            if (val is int i) return i.ToString();
            if (val is long l) return l.ToString();
            if (val is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (val is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (val is List<string> list)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int idx = 0; idx < list.Count; idx++)
                {
                    if (idx > 0) sb.Append(',');
                    sb.Append(JsonEscape(list[idx]));
                }
                sb.Append(']');
                return sb.ToString();
            }
            if (val is List<Dictionary<string, object>> dictList)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int idx = 0; idx < dictList.Count; idx++)
                {
                    if (idx > 0) sb.Append(',');
                    sb.Append(JsonDict(dictList[idx]));
                }
                sb.Append(']');
                return sb.ToString();
            }
            if (val is Dictionary<string, object> dict)
            {
                return JsonDict(dict);
            }
            return JsonEscape(val.ToString());
        }
    }
}
