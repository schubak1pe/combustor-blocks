using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CombustorBlocks
{
    /// <summary>
    /// Executes actions on Unity's main thread. HTTP requests arrive on worker
    /// threads, but every game object must be touched from the main thread.
    /// </summary>
    public static class ApiBridge
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        public static void Enqueue(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        public static void Pump(int max = 16)
        {
            for (int i = 0; i < max; i++)
            {
                if (!Queue.TryDequeue(out var action)) break;
                try { action(); }
                catch (Exception e) { MelonLogger.Error("ApiBridge: " + e); }
            }
        }

        public static T Run<T>(Func<T> fn, int timeoutMs = 10000)
        {
            if (fn == null) return default;
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Enqueue(() =>
            {
                try { tcs.TrySetResult(fn()); }
                catch (Exception e) { tcs.TrySetException(e); }
            });
            if (!tcs.Task.Wait(timeoutMs))
                throw new TimeoutException("El juego no respondió a tiempo. ¿Está en primer plano o cargando?");
            return tcs.Task.GetAwaiter().GetResult();
        }

        public static void Run(Action action, int timeoutMs = 10000)
        {
            Run(() => { action(); return true; }, timeoutMs);
        }
    }

    /// <summary>JSON snapshots of the live game, always built on the main thread.</summary>
    public static class ApiState
    {
        private static volatile string _state = "{\"ok\":false,\"reason\":\"not connected\"}";
        private static volatile string _status = "{}";

        public static string State => _state;
        public static string Status => _status;
        public static string CachedStatus => _status;

        public static string BuildState()
        {
            try
            {
                var parts = new List<object>();
                foreach (var p in GameApi.Parts)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var members = new List<object>();
                    foreach (var m in BlockSpecs.CommonMembers.Concat(BlockSpecs.MembersFor(p.Kind)))
                    {
                        if (!seen.Add(m.Name)) continue;
                        members.Add(new
                        {
                            name = m.Name,
                            method = m.Method,
                            read = m.Read,
                            write = m.Write,
                            range = m.Range ?? "",
                            doc = m.Doc ?? ""
                        });
                    }
                    parts.Add(new
                    {
                        name = p.Name,
                        kind = p.Kind.ToString(),
                        kindLabel = PartInfo.SpanishKind(p.Kind),
                        members
                    });
                }

                var gases = GameApi.Gases.Select(g => new { name = g.Name, display = g.Display }).ToList();
                var rig = GameApi.Rig;

                var obj = new
                {
                    ok = true,
                    inGame = GameApi.Sim != null,
                    connected = rig != null,
                    ship = GameApi.ShipAvailable,
                    shop = GameApi.ShopAvailable,
                    injectorHeatFixed = GameApi.InjectorHeatFixed,
                    parts,
                    gases,
                    shipMembers = BlockSpecs.ShipMembers.Select(MemberJson).ToList(),
                    shopMembers = BlockSpecs.ShopMembers.Select(MemberJson).ToList()
                };
                _state = JsonConvert.SerializeObject(obj);
                return _state;
            }
            catch (Exception e)
            {
                _state = JsonConvert.SerializeObject(new { ok = false, error = e.Message });
                return _state;
            }
        }

        private static object MemberJson(MemberInfo m)
        {
            return new { name = m.Name, method = m.Method, read = m.Read, write = m.Write, range = m.Range ?? "", doc = m.Doc ?? "" };
        }

        public static string BuildStatus()
        {
            try
            {
                var rig = GameApi.Rig;
                var log = new List<string>();
                if (rig?.Log != null)
                {
                    for (int i = 0; i < rig.Log.Count && i < 200; i++) log.Add(rig.Log[i]);
                }
                var obj = new
                {
                    ok = true,
                    paused = GameApi.Sim != null && GameApi.Sim.paused,
                    running = rig != null && rig.Running,
                    sleeping = rig != null && rig.Sleeping,
                    finished = rig != null && rig.Finished,
                    status = ScriptRunner.StatusText(),
                    error = rig != null ? (rig.ErrorText ?? "") : "",
                    errorLine = rig != null ? rig.ErrorLine : 0,
                    log
                };
                _status = JsonConvert.SerializeObject(obj);
                return _status;
            }
            catch (Exception e)
            {
                _status = JsonConvert.SerializeObject(new { ok = false, error = e.Message });
                return _status;
            }
        }
    }

    /// <summary>
    /// Tiny local HTTP server. Binds to 127.0.0.1 only; serves the web editor and
    /// exposes the game to it. No URL ACL is needed because it uses TcpListener.
    /// </summary>
    public static class WebServer
    {
        private static TcpListener _listener;
        private static volatile bool _running;
        private static int _port = 8765;
        private static string _webRoot;

        public static string Url => "http://127.0.0.1:" + _port + "/";
        public static bool Running => _running;

        public static void Start()
        {
            try
            {
                _webRoot = Path.Combine(ProjectStore.Root, "web");
                Directory.CreateDirectory(_webRoot);
                ExtractEmbeddedWeb();

                for (int p = 8765; p < 8795; p++)
                {
                    try
                    {
                        var listener = new TcpListener(IPAddress.Loopback, p);
                        listener.Start();
                        _listener = listener;
                        _port = p;
                        break;
                    }
                    catch (SocketException) { }
                }

                if (_listener == null)
                {
                    MelonLogger.Error("No hay puerto libre para el editor web (8765-8794).");
                    return;
                }

                _running = true;
                var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "CombustorBlocks.Web" };
                thread.Start();
                MelonLogger.Msg("Editor web: " + Url);
                WriteShortcuts();
            }
            catch (Exception e)
            {
                MelonLogger.Error("WebServer.Start: " + e);
            }
        }

        private static void WriteShortcuts()
        {
            try
            {
                var urlFile = Path.Combine(ProjectStore.Root, "Abrir editor.url");
                File.WriteAllText(urlFile, "[InternetShortcut]\r\nURL=" + Url + "\r\n");
                var bat = Path.Combine(ProjectStore.Root, "Abrir editor.bat");
                File.WriteAllText(bat, "@echo off\r\nstart \"\" \"" + Url + "\"\r\n");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("No se pudieron crear los accesos: " + e.Message);
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        public static void OpenBrowser()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });
                MelonLogger.Msg("Abriendo " + Url);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("No se pudo abrir el navegador: " + e.Message);
            }
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    var c = client;
                    Task.Run(() => Handle(c));
                }
                catch (Exception)
                {
                    if (!_running) return;
                    try { client?.Close(); } catch { }
                }
            }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 10000;
                    stream.WriteTimeout = 10000;

                    var request = ReadRequest(stream);
                    if (request == null) return;

                    var response = Route(request);
                    WriteResponse(stream, response);
                }
            }
            catch (Exception e)
            {
                try { MelonLogger.Warning("Web request: " + e.Message); } catch { }
            }
        }

        private sealed class Request
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = Array.Empty<byte>();
            public string Text => Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body);
        }

        private sealed class Response
        {
            public int Status = 200;
            public string ContentType = "application/json; charset=utf-8";
            public byte[] Body = Array.Empty<byte>();
        }

        private static Request ReadRequest(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            var one = new byte[1];
            int matched = 0;
            while (buffer.Length < 65536)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) return null;
                buffer.WriteByte(one[0]);
                if ((matched == 0 && one[0] == 13) || (matched == 1 && one[0] == 10) ||
                    (matched == 2 && one[0] == 13) || (matched == 3 && one[0] == 10))
                {
                    matched++;
                    if (matched == 4) break;
                }
                else matched = 0;
            }

            var head = Encoding.ASCII.GetString(buffer.ToArray());
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var req = new Request { Method = first[0].ToUpperInvariant(), Path = Uri.UnescapeDataString(first[1].Split('?')[0]) };
            for (int i = 1; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf(':');
                if (idx > 0) req.Headers[lines[i].Substring(0, idx).Trim()] = lines[i].Substring(idx + 1).Trim();
            }

            if (req.Headers.TryGetValue("Content-Length", out var lenText) && int.TryParse(lenText, out var len) && len > 0)
            {
                len = Math.Min(len, 4 * 1024 * 1024);
                var body = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = stream.Read(body, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                req.Body = body;
            }
            return req;
        }

        private static void WriteResponse(NetworkStream stream, Response response)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.Status).Append(" ").Append(StatusText(response.Status)).Append("\r\n");
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("Connection: close\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (response.Body.Length > 0) stream.Write(response.Body, 0, response.Body.Length);
            stream.Flush();
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }

        private static Response Json(object obj, int status = 200)
        {
            return new Response
            {
                Status = status,
                ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj))
            };
        }

        private static Response Text(string text, string type = "text/plain; charset=utf-8", int status = 200)
        {
            return new Response { Status = status, ContentType = type, Body = Encoding.UTF8.GetBytes(text ?? "") };
        }

        private static Response Route(Request req)
        {
            // Same-origin only for API calls. This stops a random website from
            // driving the game through a cross-origin form POST.
            if (req.Path.StartsWith("/api/", StringComparison.Ordinal) &&
                req.Headers.TryGetValue("Origin", out var origin))
            {
                var a = "http://127.0.0.1:" + _port;
                var b = "http://localhost:" + _port;
                if (!string.Equals(origin, a, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(origin, b, StringComparison.OrdinalIgnoreCase))
                    return Json(new { ok = false, error = "origin not allowed" }, 403);
            }

            switch (req.Path)
            {
                case "/api/health":
                    return Json(new { ok = true, app = "Combustor Blocks", version = "0.2.1" });

                case "/api/state":
                    return Text(ApiState.State, "application/json; charset=utf-8");

                case "/api/connect":
                {
                    try
                    {
                        ApiBridge.Run(() =>
                        {
                            GameApi.Tick(true);
                            ApiState.BuildState();
                            return true;
                        });
                        return Text(ApiState.State, "application/json; charset=utf-8");
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, error = e.Message }, 500);
                    }
                }

                case "/api/status":
                    return Text(ApiState.CachedStatus, "application/json; charset=utf-8");

                case "/api/lang":
                {
                    try
                    {
                        var lang = ApiBridge.Run(() => Il2CppGasSim.L.Language ?? "");
                        return Json(new { ok = true, language = lang });
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, language = "", error = e.Message });
                    }
                }

                case "/api/ai":
                {
                    try
                    {
                        return AiRequest(req);
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, error = e.Message }, 200);
                    }
                }

                case "/api/validate":
                {
                    string code = ReadCode(req);
                    try
                    {
                        var res = ApiBridge.Run(() => ScriptRunner.Validate(code, out var err, out var line)
                            ? new { ok = true, error = "", line = 0 }
                            : new { ok = false, error = err, line });
                        return Json(res);
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, error = e.Message, line = 0 }, 500);
                    }
                }

                case "/api/run":
                {
                    string code = ReadCode(req);
                    try
                    {
                        var res = ApiBridge.Run(() =>
                        {
                            bool ok = ScriptRunner.Run(code, out var err, out var line);
                            return new { ok, error = err ?? "", line, status = ScriptRunner.StatusText() };
                        }, 15000);
                        return Json(res);
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, error = e.Message, line = 0 }, 500);
                    }
                }

                case "/api/resume":
                {
                    try
                    {
                        ApiBridge.Run(() =>
                        {
                            if (GameApi.Sim != null) GameApi.Sim.paused = false;
                            return true;
                        });
                        return Json(new { ok = true });
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, error = e.Message }, 500);
                    }
                }

                case "/api/stop":
                {
                    try
                    {
                        ApiBridge.Run(() => ScriptRunner.Stop());
                        return Json(new { ok = true, status = ApiBridge.Run(() => ScriptRunner.StatusText()) });
                    }
                    catch (Exception e)
                    {
                        return Json(new { ok = false, error = e.Message }, 500);
                    }
                }

                case "/api/project":
                {
                    var path = Path.Combine(ProjectStore.Root, "project.json");
                    if (req.Method == "GET")
                    {
                        if (!File.Exists(path)) return Json(new { ok = true, project = (object)null });
                        return Text(File.ReadAllText(path), "application/json; charset=utf-8");
                    }
                    if (req.Method == "POST")
                    {
                        File.WriteAllText(path, req.Text ?? "{}");
                        return Json(new { ok = true });
                    }
                    return Json(new { ok = false, error = "method" }, 400);
                }

                case "/api/open-browser":
                    OpenBrowser();
                    return Json(new { ok = true });
            }

            // ---- static files ----
            var path2 = req.Path == "/" ? "/index.html" : req.Path;
            var full = Path.GetFullPath(Path.Combine(_webRoot, path2.TrimStart('/')));
            if (!full.StartsWith(Path.GetFullPath(_webRoot), StringComparison.OrdinalIgnoreCase))
                return Text("forbidden", "text/plain", 404);
            if (!File.Exists(full))
            {
                if (req.Path == "/" || req.Path == "/index.html")
                    return Text(MissingWebHtml(), "text/html; charset=utf-8", 200);
                return Text("not found: " + path2, "text/plain", 404);
            }
            return new Response { Status = 200, ContentType = Mime(full), Body = File.ReadAllBytes(full) };
        }

        private static Response AiRequest(Request req)
        {
            var body = JObject.Parse(req.Text);
            var provider = ((string)body["provider"] ?? "ollama").ToLowerInvariant();
            var url = ((string)body["url"] ?? "http://127.0.0.1:11434").TrimEnd('/');
            var model = (string)body["model"] ?? "llama3.1";
            var key = (string)body["apiKey"] ?? "";
            var messages = body["messages"] as JArray;
            if (messages == null || messages.Count == 0)
                return Json(new { ok = false, error = "messages required" });

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(60);
                HttpResponseMessage resp;

                if (provider == "ollama")
                {
                    var payload = new JObject
                    {
                        ["model"] = model,
                        ["stream"] = false,
                        ["messages"] = messages
                    };
                    var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    resp = http.PostAsync(url + "/api/chat", content).GetAwaiter().GetResult();
                }
                else
                {
                    var endpoint = url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                        ? url + "/chat/completions"
                        : url + "/v1/chat/completions";
                    var payload = new JObject
                    {
                        ["model"] = model,
                        ["temperature"] = 0.2,
                        ["messages"] = messages
                    };
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrWhiteSpace(key))
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                    resp = http.SendAsync(request).GetAwaiter().GetResult();
                }

                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    return Json(new { ok = false, error = "HTTP " + (int)resp.StatusCode + ": " + Shorten(text, 500) });

                try
                {
                    var obj = JObject.Parse(text);
                    var content = provider == "ollama"
                        ? (string)obj["message"]?["content"]
                        : (string)obj["choices"]?[0]?["message"]?["content"];
                    if (string.IsNullOrEmpty(content))
                        return Json(new { ok = false, error = "empty response" });
                    return Json(new { ok = true, text = content });
                }
                catch
                {
                    return Json(new { ok = false, error = "bad response: " + Shorten(text, 500) });
                }
            }
        }

        private static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static string ReadCode(Request req)
        {
            try
            {
                var obj = JObject.Parse(req.Text);
                return (string)obj["code"] ?? "";
            }
            catch
            {
                return req.Text ?? "";
            }
        }

        private static string Mime(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".svg": return "image/svg+xml";
                case ".woff2": return "font/woff2";
                case ".ico": return "image/x-icon";
                default: return "application/octet-stream";
            }
        }

        private static string MissingWebHtml()
        {
            return "<!doctype html><meta charset='utf-8'><title>Combustor Blocks</title>" +
                   "<body style='font:16px Segoe UI;background:#14161c;color:#e9edf5;padding:40px'>" +
                   "<h1>Combustor Blocks</h1><p>El servidor está vivo, pero faltan los archivos del editor.</p>" +
                   "<p>Busca la carpeta <code>" + WebRoot + "</code> y copia dentro <code>index.html</code>, <code>app.js</code> y <code>style.css</code>.</p></body>";
        }

        public static string WebRoot => _webRoot ?? Path.Combine(ProjectStore.Root, "web");

        private static void ExtractEmbeddedWeb()
        {
            // The ZIP ships the web folder; nothing to extract. Kept as a hook for
            // single-DLL distributions.
        }
    }
}
