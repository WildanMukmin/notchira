using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Notchira
{
    sealed class AiProvider
    {
        public string Id, Name, Kind, DefaultBase, DefaultModel;
        public string[] Suggested;
    }

    sealed class AiMessage
    {
        public string Role; // "user" or "assistant"
        public string Text;
        public AiMessage(string role, string text) { Role = role; Text = text; }
    }

    sealed class AiResult
    {
        public string Text, Provider, KeyLabel;
        public List<string> Actions = new List<string>();
    }

    // One parameter of a tool, described to the model as a small JSON Schema property.
    sealed class AiToolParam
    {
        public string Name, Type, Description;
        public AiToolParam(string name, string type, string description) { Name = name; Type = type; Description = description; }
    }

    // What a tool call produced: ForModel goes back into the conversation so the model can answer from it;
    // Action, if set, is a short line shown to the user in the chat (what actually happened on their machine).
    sealed class AiToolResult
    {
        public string ForModel, Action;
        public AiToolResult(string forModel, string action = null) { ForModel = forModel; Action = action; }
    }

    // A function the assistant can call. Run executes locally and must not throw for an expected failure
    // (a tool that simply did not find anything) — only for something the caller should see as an error.
    sealed class AiTool
    {
        public string Name, Description;
        public AiToolParam[] Params;
        public Func<Dictionary<string, object>, AiToolResult> Run;
    }

    // Text plus any tool actions taken while producing it, before it is wrapped into an AiResult.
    sealed class AiTurn
    {
        public string Text;
        public List<string> Actions;
    }

    // Chat with Groq (OpenAI-style API) and Gemini. Keys come from the key ring, and when
    // every key of a provider is resting or rejected the next provider with keys answers instead.
    static class Ai
    {
        public static readonly AiProvider[] Providers =
        {
            new AiProvider { Id = "groq", Name = "Groq", Kind = "openai", DefaultBase = "https://api.groq.com/openai/v1", DefaultModel = "openai/gpt-oss-120b",
                Suggested = new[] { "openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3.8-27b" } },
            new AiProvider { Id = "gemini", Name = "Gemini", Kind = "gemini", DefaultBase = "https://generativelanguage.googleapis.com/v1beta", DefaultModel = "gemini-3.5-flash",
                Suggested = new[] { "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-3.1-pro-preview" } }
        };

        public const string DefaultSystem =
            "Kamu asisten kecil di Notchira, aplikasi di tepi layar Windows. Jawab singkat, jelas, dan langsung ke inti, " +
            "dalam bahasa yang dipakai pengguna.";

        // Appended to every system prompt, also a custom one, so the answer to "who made you" stays the same.
        const string Creator = " Notchira dibuat oleh Wildan Mukmin. Kalau pengguna bertanya siapa yang membuat atau mengembangkan kamu atau aplikasi ini, " +
            "sebut nama Wildan Mukmin sebagai pembuatnya.";

        public static AiProvider Find(string id)
        {
            return Providers.FirstOrDefault(p => p.Id == id) ?? Providers[0];
        }

        public static string Base(AiProvider p)
        {
            string env = Environment.GetEnvironmentVariable("NOTCHIRA_AI_BASE_" + p.Id.ToUpperInvariant());
            if (!string.IsNullOrEmpty(env)) return env.TrimEnd('/');
            string s = Settings.Get("ai.base." + p.Id, "");
            return (s.Length > 0 ? s : p.DefaultBase).TrimEnd('/');
        }

        public static string Model(AiProvider p)
        {
            string s = Settings.Get("ai.model." + p.Id, "");
            return s.Length > 0 ? s : p.DefaultModel;
        }

        public static void SetModel(AiProvider p, string model)
        {
            Settings.Set("ai.model." + p.Id, model == p.DefaultModel ? "" : model);
        }

        // Models the user added on top of the suggested ones, kept per provider in settings.ini.
        public static List<string> Added(AiProvider p)
        {
            return Settings.Get("ai.models." + p.Id, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Suggested first, then the user's own, then the selected one if it came from somewhere else (the key panel).
        public static List<string> Models(AiProvider p)
        {
            var list = new List<string>(p.Suggested);
            foreach (string m in Added(p)) if (!list.Contains(m)) list.Add(m);
            string cur = Model(p);
            if (!list.Contains(cur)) list.Add(cur);
            return list;
        }

        public static void AddModel(AiProvider p, string model)
        {
            var list = Added(p);
            if (!list.Contains(model) && !p.Suggested.Contains(model)) list.Add(model);
            Settings.Set("ai.models." + p.Id, string.Join("|", list.ToArray()));
        }

        public static void RemoveModel(AiProvider p, string model)
        {
            var list = Added(p);
            list.Remove(model);
            Settings.Set("ai.models." + p.Id, string.Join("|", list.ToArray()));
            if (Model(p) == model) SetModel(p, p.DefaultModel);
        }

        // Chat models the provider offers to the active key. Used to check a model name before it is added.
        public static List<string> ListModels(AiProvider p)
        {
            string label;
            return Secrets.Call(p.Id, key =>
            {
                var result = new List<string>();
                if (p.Kind == "gemini")
                {
                    var json = GetJson(Base(p) + "/models?pageSize=1000", new Dictionary<string, string> { { "x-goog-api-key", key } });
                    var models = Child(json, "models") as object[];
                    if (models != null)
                        foreach (object m in models)
                        {
                            var methods = Child(m, "supportedGenerationMethods") as object[];
                            string name = Child(m, "name") as string;
                            if (name != null && methods != null && methods.Contains("generateContent"))
                                result.Add(name.StartsWith("models/") ? name.Substring(7) : name);
                        }
                }
                else
                {
                    var json = GetJson(Base(p) + "/models", new Dictionary<string, string> { { "Authorization", "Bearer " + key } });
                    var data = Child(json, "data") as object[];
                    if (data != null)
                        foreach (object m in data) { string id = Child(m, "id") as string; if (id != null) result.Add(id); }
                }
                return result;
            }, false, out label);
        }

        public static string System { get { string s = Settings.Get("ai.system", ""); return (s.Length > 0 ? s : DefaultSystem) + Creator; } }

        // Providers worth trying, the preferred one first, then the others that have keys.
        static List<AiProvider> Order(string preferred, bool fallback)
        {
            var list = new List<AiProvider> { Find(preferred) };
            if (fallback) list.AddRange(Providers.Where(p => p.Id != list[0].Id && Secrets.Has(p.Id)));
            return list;
        }

        // history ends with the user's latest message. Throws KeyException or Exception with a readable message.
        public static AiResult Ask(List<AiMessage> history, string system, string preferred, bool fallback, List<AiTool> tools = null)
        {
            var errors = new List<string>();
            foreach (AiProvider p in Order(preferred, fallback))
            {
                try
                {
                    string label;
                    AiTurn turn = Secrets.Call(p.Id, key => Send(p, key, history, system, tools), false, out label);
                    return new AiResult { Text = turn.Text, Provider = p.Name, KeyLabel = label, Actions = turn.Actions };
                }
                catch (KeyException ex) { errors.Add(ex.Message); }
                catch (Exception ex)
                {
                    int code; string body;
                    Describe(ex, out code, out body);
                    errors.Add(p.Name + ": " + (code > 0 ? "HTTP " + code + " " + Short(body) : ex.Message));
                }
            }
            throw new Exception(string.Join(" ", errors.ToArray()));
        }

        static void Describe(Exception ex, out int code, out string body)
        {
            code = 0; body = "";
            var api = ex as ApiException;
            if (api != null) { code = api.Code; body = api.Body; return; }
            var web = ex as WebException;
            if (web != null && web.Response is HttpWebResponse)
            {
                code = (int)((HttpWebResponse)web.Response).StatusCode;
                try { using (var rd = new StreamReader(web.Response.GetResponseStream(), Encoding.UTF8)) body = rd.ReadToEnd(); } catch { }
            }
        }

        static string Short(string s)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        // ---- wire formats -----------------------------------------------------------------------------------

        const int MaxToolRounds = 4;

        static AiTurn Send(AiProvider p, string key, List<AiMessage> history, string system, List<AiTool> tools)
        {
            switch (p.Kind)
            {
                case "gemini": return Gemini(p, key, history, system, tools);
                default: return OpenAi(p, key, history, system, tools);
            }
        }

        // Runs the named tool and records a user-visible Action line, if any. Never throws: an unknown tool
        // or a tool that throws becomes text the model reads back, so it can tell the user what went wrong.
        static string RunTool(List<AiTool> tools, string name, Dictionary<string, object> args, List<string> actions)
        {
            AiTool tool = tools.FirstOrDefault(t => t.Name == name);
            if (tool == null) return "Tool \"" + name + "\" tidak dikenal.";
            try
            {
                AiToolResult r = tool.Run(args ?? new Dictionary<string, object>());
                if (r == null) return "";
                if (!string.IsNullOrEmpty(r.Action)) actions.Add(r.Action);
                return r.ForModel ?? "";
            }
            catch (Exception ex) { return "Tool \"" + name + "\" gagal: " + ex.Message; }
        }

        static object OpenAiTools(List<AiTool> tools)
        {
            var list = new List<object>();
            foreach (AiTool t in tools)
            {
                var props = new Dictionary<string, object>();
                var required = new List<object>();
                foreach (AiToolParam pa in t.Params)
                {
                    props[pa.Name] = new Dictionary<string, object> { { "type", pa.Type }, { "description", pa.Description } };
                    required.Add(pa.Name);
                }
                list.Add(new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object>
                        {
                            { "name", t.Name }, { "description", t.Description },
                            { "parameters", new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "required", required } } }
                        }
                    }
                });
            }
            return list;
        }

        static AiTurn OpenAi(AiProvider p, string key, List<AiMessage> history, string system, List<AiTool> tools)
        {
            var msgs = new List<object>();
            if (!string.IsNullOrEmpty(system)) msgs.Add(new Dictionary<string, object> { { "role", "system" }, { "content", system } });
            foreach (var m in history) msgs.Add(new Dictionary<string, object> { { "role", m.Role }, { "content", m.Text } });
            var actions = new List<string>();
            for (int round = 0; round < MaxToolRounds; round++)
            {
                var body = new Dictionary<string, object> { { "model", Model(p) }, { "messages", msgs } };
                if (tools != null && tools.Count > 0) { body["tools"] = OpenAiTools(tools); body["tool_choice"] = "auto"; }
                var headers = new Dictionary<string, string>();
                if (key.Length > 0) headers["Authorization"] = "Bearer " + key;
                var json = Post(Base(p) + "/chat/completions", headers, body);
                var choices = Child(json, "choices") as object[];
                if (choices == null || choices.Length == 0) throw new Exception("Jawaban kosong dari " + p.Name + ".");
                var msg = Child(choices[0], "message") as Dictionary<string, object>;
                var calls = tools != null ? Child(msg, "tool_calls") as object[] : null;
                if (calls == null || calls.Length == 0)
                {
                    string text = Child(msg, "content") as string;
                    if (!string.IsNullOrEmpty(text)) return new AiTurn { Text = text.Trim(), Actions = actions };
                    throw new Exception("Jawaban kosong dari " + p.Name + ".");
                }
                msgs.Add(msg);
                foreach (object c in calls)
                {
                    var fn = Child(c, "function") as Dictionary<string, object>;
                    string name = Child(fn, "name") as string;
                    string argsJson = Child(fn, "arguments") as string;
                    var args = !string.IsNullOrEmpty(argsJson) ? new JavaScriptSerializer().DeserializeObject(argsJson) as Dictionary<string, object> : null;
                    string result = RunTool(tools, name, args, actions);
                    msgs.Add(new Dictionary<string, object> { { "role", "tool" }, { "tool_call_id", Child(c, "id") }, { "content", result } });
                }
            }
            throw new Exception(p.Name + " memanggil tool berulang-ulang tanpa menjawab.");
        }

        static object GeminiTools(List<AiTool> tools)
        {
            var decls = new List<object>();
            foreach (AiTool t in tools)
            {
                var props = new Dictionary<string, object>();
                var required = new List<object>();
                foreach (AiToolParam pa in t.Params)
                {
                    props[pa.Name] = new Dictionary<string, object> { { "type", pa.Type.ToUpperInvariant() }, { "description", pa.Description } };
                    required.Add(pa.Name);
                }
                decls.Add(new Dictionary<string, object>
                {
                    { "name", t.Name }, { "description", t.Description },
                    { "parameters", new Dictionary<string, object> { { "type", "OBJECT" }, { "properties", props }, { "required", required } } }
                });
            }
            return new object[] { new Dictionary<string, object> { { "functionDeclarations", decls } } };
        }

        static AiTurn Gemini(AiProvider p, string key, List<AiMessage> history, string system, List<AiTool> tools)
        {
            var contents = new List<object>();
            foreach (var m in history)
                contents.Add(new Dictionary<string, object>
                {
                    { "role", m.Role == "assistant" ? "model" : "user" },
                    { "parts", new object[] { new Dictionary<string, object> { { "text", m.Text } } } }
                });
            var actions = new List<string>();
            for (int round = 0; round < MaxToolRounds; round++)
            {
                var body = new Dictionary<string, object> { { "contents", contents } };
                if (!string.IsNullOrEmpty(system))
                    body["systemInstruction"] = new Dictionary<string, object> { { "parts", new object[] { new Dictionary<string, object> { { "text", system } } } } };
                if (tools != null && tools.Count > 0) body["tools"] = GeminiTools(tools);
                var headers = new Dictionary<string, string> { { "x-goog-api-key", key } };
                var json = Post(Base(p) + "/models/" + Uri.EscapeDataString(Model(p)) + ":generateContent", headers, body);
                var cands = Child(json, "candidates") as object[];
                if (cands == null || cands.Length == 0) throw new Exception("Jawaban kosong dari " + p.Name + " (mungkin diblokir).");
                var parts = Child(Child(cands[0], "content"), "parts") as object[];
                if (parts == null) throw new Exception("Jawaban kosong dari " + p.Name + " (mungkin diblokir).");
                var calls = new List<object>();
                var sb = new StringBuilder();
                foreach (object part in parts)
                {
                    if (tools != null && Child(part, "functionCall") != null) calls.Add(part);
                    else { string t = Child(part, "text") as string; if (t != null) sb.Append(t); }
                }
                if (calls.Count == 0)
                {
                    if (sb.Length > 0) return new AiTurn { Text = sb.ToString().Trim(), Actions = actions };
                    throw new Exception("Jawaban kosong dari " + p.Name + " (mungkin diblokir).");
                }
                contents.Add(new Dictionary<string, object> { { "role", "model" }, { "parts", parts } });
                var respParts = new List<object>();
                foreach (object part in calls)
                {
                    var fc = Child(part, "functionCall") as Dictionary<string, object>;
                    string name = Child(fc, "name") as string;
                    var args = Child(fc, "args") as Dictionary<string, object>;
                    string result = RunTool(tools, name, args, actions);
                    respParts.Add(new Dictionary<string, object>
                    {
                        { "functionResponse", new Dictionary<string, object>
                            { { "name", name }, { "response", new Dictionary<string, object> { { "result", result } } } }
                        }
                    });
                }
                contents.Add(new Dictionary<string, object> { { "role", "user" }, { "parts", respParts } });
            }
            throw new Exception(p.Name + " memanggil tool berulang-ulang tanpa menjawab.");
        }

        public static object Child(object node, string name)
        {
            var d = node as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(name, out v) ? v : null;
        }

        public static object GetJson(string url, Dictionary<string, string> headers)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Notchira/0.7";
            req.Timeout = 20000;
            foreach (var h in headers) req.Headers[h.Key] = h.Value;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(rd.ReadToEnd());
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) throw;
                string text = "";
                try { using (var rd = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) text = rd.ReadToEnd(); } catch { }
                throw new ApiException((int)r.StatusCode, text);
            }
        }

        static object Post(string url, Dictionary<string, string> headers, Dictionary<string, object> body)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = "Notchira/0.7";
            req.Timeout = 90000;
            foreach (var h in headers) req.Headers[h.Key] = h.Value;
            byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(body));
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(rd.ReadToEnd());
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) throw;
                string text = "";
                try { using (var rd = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) text = rd.ReadToEnd(); } catch { }
                throw new ApiException((int)r.StatusCode, text);
            }
        }

        // ---- one-shot helper for features inside Notchira ----------------------------------------------------

        public static AiResult Once(string system, string prompt)
        {
            return Ask(new List<AiMessage> { new AiMessage("user", prompt) }, system,
                Settings.Get("ai.provider", "groq"), Settings.Get("ai.fallback", "1") == "1");
        }
    }
}
