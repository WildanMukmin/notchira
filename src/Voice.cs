using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Notchira
{
    // A spoken conversation: the microphone streams to the provider, its voice comes back and is played as it arrives.
    // One session runs on its own thread from Start() until Stop(), the provider closes it, or 15 seconds of silence.
    abstract class VoiceSession
    {
        public enum St { Connecting, Listening, Thinking, Speaking, Ended }

        public event Action Changed;                 // state, level or live text changed; raised on worker threads
        public event Action<string, string> Turn;    // role ("user" or "assistant") and the finished text of one turn

        public volatile St State = St.Connecting;
        public volatile float Level;
        public volatile string Live = "", LiveRole = "user";
        public string Error, KeyLabel = "";
        public volatile bool Quiet;
        public long AudioBytes { get { return spk == null ? 0 : spk.Received; } }                  // ended by itself after the silence timeout
        public abstract string Name { get; }
        public abstract string Model { get; }
        protected abstract string ProviderId { get; }

        protected ClientWebSocket ws;
        protected Speaker spk;
        protected static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        protected readonly bool bargeIn = Settings.Get("voice.bargein", "0") == "1";
        IMic mic;
        readonly object sendGate = new object();
        volatile bool stopping;
        protected volatile bool replyDone;
        long lastVoice, lastRaise;
        const int SilenceMs = 15000;

        public void Start()
        {
            new Thread(Run) { IsBackground = true, Name = "voice" }.Start();
        }

        public void Stop()
        {
            stopping = true;
            try { if (ws != null) ws.Abort(); } catch { }
        }

        protected void Set(St s) { if (State == s) return; State = s; Raise(true); }

        protected void Raise(bool now)
        {
            long t = Environment.TickCount;
            if (!now && t - lastRaise < 80) return;
            lastRaise = t;
            var h = Changed;
            if (h != null) h();
        }

        protected void Commit(string role, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            var h = Turn;
            if (h != null) h(role, text);
        }

        protected void Heard() { lastVoice = Environment.TickCount; }

        void Run()
        {
            try
            {
                string label;
                ws = Secrets.Call(ProviderId, key => Connect(key), false, out label);
                KeyLabel = label;
                spk = new Speaker();
                spk.Start();
                string wav = Environment.GetEnvironmentVariable("NOTCHIRA_VOICE_WAV");
                mic = wav != null ? (IMic)new FileMic(wav, OnMic) : new Mic(OnMic);
                mic.Start();
                Heard();
                Set(St.Listening);
                Loop();
            }
            catch (Exception ex) { if (!stopping) Error = Describe(ex); }
            finally
            {
                if (mic != null) mic.Dispose();
                if (spk != null) spk.Dispose();
                try { if (ws != null) ws.Dispose(); } catch { }
                Set(St.Ended);
            }
        }

        void Loop()
        {
            var buf = new byte[65536];
            var msg = new System.IO.MemoryStream();
            Task<WebSocketReceiveResult> pending = null;
            while (!stopping && ws.State == WebSocketState.Open)
            {
                if (pending == null) pending = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (pending.Wait(200))
                {
                    var r = pending.Result;
                    pending = null;
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        if (!stopping) Error = Ended(ws.CloseStatusDescription);
                        break;
                    }
                    msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    byte[] data = msg.ToArray();
                    msg.SetLength(0);
                    if (r.MessageType == WebSocketMessageType.Binary && !BinaryIsJson) { Heard(); Play(data); }
                    else Handle(Encoding.UTF8.GetString(data));
                }
                // the reply has been said and played out: back to listening
                if (replyDone && !spk.Busy) { replyDone = false; Heard(); Set(St.Listening); }
                if (spk.Busy) Heard();
                if (State == St.Listening && Environment.TickCount - lastVoice > SilenceMs)
                {
                    Quiet = true;
                    stopping = true;
                }
            }
            try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); } catch { }
        }

        protected void Play(byte[] pcm)
        {
            spk.Write(pcm);
            if (State != St.Speaking) Set(St.Speaking);
        }

        // Without echo cancellation the speaker would be heard by the microphone, so while the assistant talks the
        // microphone sends silence, unless barge-in (for headsets) is on.
        void OnMic(byte[] pcm)
        {
            if (stopping || ws == null || ws.State != WebSocketState.Open) return;
            float lv = Mic.Level(pcm);
            bool muted = !bargeIn && (State == St.Speaking || spk.Busy);
            Level = muted ? 0 : lv;
            if (!muted && lv > 0.05f) Heard();
            SendAudio(muted ? new byte[pcm.Length] : pcm);
            Raise(false);
        }

        protected void SendText(string s) { Send(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text); }

        protected void Send(byte[] data, WebSocketMessageType type)
        {
            lock (sendGate)
            {
                try { ws.SendAsync(new ArraySegment<byte>(data), type, true, CancellationToken.None).Wait(5000); }
                catch { }
            }
        }

        // Opens the socket for one key; fails with ApiException 401/403/429 so the key ring can move to the next key.
        protected static ClientWebSocket Open(ClientWebSocket w, string url)
        {
            try
            {
                if (!w.ConnectAsync(new Uri(url), CancellationToken.None).Wait(15000)) throw new Exception("Tidak tersambung (waktu habis).");
                return w;
            }
            catch (AggregateException ex)
            {
                string m = ex.GetBaseException().Message;
                var code = System.Text.RegularExpressions.Regex.Match(m, @"'(\d{3})'");
                if (code.Success) throw new ApiException(int.Parse(code.Groups[1].Value), m);
                throw new Exception(m);
            }
        }

        protected static string ReceiveOne(ClientWebSocket w, int timeoutMs)
        {
            var buf = new byte[65536];
            var ms = new System.IO.MemoryStream();
            while (true)
            {
                var t = w.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (!t.Wait(timeoutMs)) throw new Exception("Tidak ada jawaban dari server.");
                if (t.Result.MessageType == WebSocketMessageType.Close) throw Closed(w.CloseStatusDescription);
                ms.Write(buf, 0, t.Result.Count);
                if (t.Result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // A close reason that means "this key cannot be used now" becomes an HTTP-like code for the key ring.
        protected static Exception Closed(string reason)
        {
            string r = reason ?? "";
            string low = r.ToLowerInvariant();
            if (low.Contains("quota") || low.Contains("exhausted") || low.Contains("rate")) return new ApiException(429, r);
            if (low.Contains("api key") || low.Contains("permission") || low.Contains("unauth") || low.Contains("credential")) return new ApiException(403, r);
            return new Exception("Server menutup sambungan: " + (r.Length > 0 ? r : "tanpa alasan"));
        }

        static string Ended(string reason)
        {
            return string.IsNullOrEmpty(reason) ? "Sambungan suara ditutup server." : "Sambungan ditutup: " + reason;
        }

        static string Describe(Exception ex)
        {
            var api = ex as ApiException;
            if (api != null) return "HTTP " + api.Code + " " + (api.Body.Length > 140 ? api.Body.Substring(0, 140) + "..." : api.Body);
            return ex.GetBaseException().Message;
        }

        protected static object Child(object node, string name)
        {
            var d = node as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(name, out v) ? v : null;
        }

        protected virtual bool BinaryIsJson { get { return false; } }
        protected abstract ClientWebSocket Connect(string key);
        protected abstract void SendAudio(byte[] pcm);
        protected abstract void Handle(string json);
    }

    // Gemini Live API: JSON messages both ways, audio as base64 PCM inside them, with transcripts of both sides.
    sealed class GeminiVoice : VoiceSession
    {
        readonly string model = Voices.GeminiModel;
        string heard = "", said = "";

        public override string Name { get { return "Gemini Live"; } }
        public override string Model { get { return model; } }
        protected override string ProviderId { get { return "gemini"; } }
        protected override bool BinaryIsJson { get { return true; } }

        protected override ClientWebSocket Connect(string key)
        {
            var w = Open(new ClientWebSocket(),
                "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + Uri.EscapeDataString(key));
            var setup = new Dictionary<string, object>
            {
                { "setup", new Dictionary<string, object>
                    {
                        { "model", "models/" + model },
                        { "generationConfig", new Dictionary<string, object> { { "responseModalities", new[] { "AUDIO" } } } },
                        { "systemInstruction", new Dictionary<string, object> { { "parts", new object[] { new Dictionary<string, object> { { "text", Voices.System } } } } } },
                        { "inputAudioTranscription", new Dictionary<string, object>() },
                        { "outputAudioTranscription", new Dictionary<string, object>() }
                    }
                }
            };
            byte[] b = Encoding.UTF8.GetBytes(Json.Serialize(setup));
            w.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(10000);
            string first = ReceiveOne(w, 15000);
            if (!first.Contains("setupComplete")) { w.Dispose(); throw new Exception("Gemini menolak sesi: " + first); }
            return w;
        }

        protected override void SendAudio(byte[] pcm)
        {
            SendText("{\"realtimeInput\":{\"audio\":{\"data\":\"" + Convert.ToBase64String(pcm) + "\",\"mimeType\":\"audio/pcm;rate=16000\"}}}");
        }

        protected override void Handle(string text)
        {
            object j;
            try { j = Json.DeserializeObject(text); } catch { return; }
            if (Child(j, "goAway") != null) { Error = "Sesi Gemini Live mencapai batas waktu."; Stop(); return; }
            var sc = Child(j, "serverContent");
            if (sc == null) return;
            if (Child(sc, "interrupted") is bool && (bool)Child(sc, "interrupted")) spk.Clear();

            string inT = Child(Child(sc, "inputTranscription"), "text") as string;
            if (!string.IsNullOrEmpty(inT))
            {
                heard += inT;
                Live = heard.Trim();
                LiveRole = "user";
                if (State == St.Listening) Set(St.Thinking);
                Raise(false);
            }
            string outT = Child(Child(sc, "outputTranscription"), "text") as string;
            if (!string.IsNullOrEmpty(outT))
            {
                if (heard.Length > 0) { Commit("user", heard); heard = ""; }
                said += outT;
                Live = said.Trim();
                LiveRole = "assistant";
                Raise(false);
            }
            var parts = Child(Child(sc, "modelTurn"), "parts") as object[];
            if (parts != null)
                foreach (object p in parts)
                {
                    string data = Child(Child(p, "inlineData"), "data") as string;
                    if (data != null) { Heard(); Play(Convert.FromBase64String(data)); }
                }
            if (Child(sc, "turnComplete") is bool && (bool)Child(sc, "turnComplete"))
            {
                if (heard.Length > 0) { Commit("user", heard); heard = ""; }
                if (said.Length > 0) { Commit("assistant", said); said = ""; }
                replyDone = true;
            }
        }
    }

    // Deepgram Voice Agent: Deepgram listens (nova-3), thinks (an LLM it hosts) and speaks (Aura-2).
    // Audio goes both ways as binary frames; the rest is JSON events.
    sealed class DeepgramVoice : VoiceSession
    {
        readonly string voice = Voices.DeepgramVoice;

        public override string Name { get { return "Deepgram"; } }
        public override string Model { get { return voice; } }
        protected override string ProviderId { get { return "deepgram"; } }

        protected override ClientWebSocket Connect(string key)
        {
            var w = new ClientWebSocket();
            w.Options.SetRequestHeader("Authorization", "Token " + key);
            Open(w, "wss://agent.deepgram.com/v1/agent/converse");
            var settings = new Dictionary<string, object>
            {
                { "type", "Settings" },
                { "audio", new Dictionary<string, object>
                    {
                        { "input", new Dictionary<string, object> { { "encoding", "linear16" }, { "sample_rate", Mic.Rate } } },
                        { "output", new Dictionary<string, object> { { "encoding", "linear16" }, { "sample_rate", Speaker.Rate }, { "container", "none" } } }
                    }
                },
                { "agent", new Dictionary<string, object>
                    {
                        { "language", Settings.Get("voice.lang.deepgram", "id") },
                        { "listen", new Dictionary<string, object> { { "provider", new Dictionary<string, object> { { "type", "deepgram" }, { "model", "nova-3" } } } } },
                        { "think", new Dictionary<string, object>
                            {
                                { "provider", new Dictionary<string, object> { { "type", "open_ai" }, { "model", Settings.Get("voice.think.deepgram", "gpt-4o-mini") } } },
                                { "prompt", Voices.System }
                            }
                        },
                        { "speak", new Dictionary<string, object> { { "provider", new Dictionary<string, object> { { "type", "deepgram" }, { "model", voice } } } } }
                    }
                }
            };
            byte[] b = Encoding.UTF8.GetBytes(Json.Serialize(settings));
            w.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(10000);
            while (true)
            {
                string m = ReceiveOne(w, 15000);
                object j = Json.DeserializeObject(m);
                string type = Child(j, "type") as string;
                if (type == "SettingsApplied") return w;
                if (type == "Error") { w.Dispose(); throw new Exception("Deepgram: " + (Child(j, "description") as string ?? m)); }
            }
        }

        protected override void SendAudio(byte[] pcm) { Send(pcm, WebSocketMessageType.Binary); }

        protected override void Handle(string text)
        {
            object j;
            try { j = Json.DeserializeObject(text); } catch { return; }
            switch (Child(j, "type") as string)
            {
                case "ConversationText":
                    string role = Child(j, "role") as string == "assistant" ? "assistant" : "user";
                    string content = Child(j, "content") as string ?? "";
                    Live = content;
                    LiveRole = role;
                    Commit(role, content);
                    Raise(true);
                    break;
                case "UserStartedSpeaking":
                    if (bargeIn) spk.Clear();
                    Heard();
                    break;
                case "AgentThinking": Set(St.Thinking); break;
                case "AgentStartedSpeaking": Set(St.Speaking); break;
                case "AgentAudioDone": replyDone = true; break;
                case "Error":
                    Error = "Deepgram: " + (Child(j, "description") as string ?? text);
                    Stop();
                    break;
            }
        }
    }

    // The voice models and voices offered in the picker, the chosen one, and checks for names the user adds.
    static class Voices
    {
        public static readonly string[] GeminiSuggested = { "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-latest" };
        public static readonly string[] DeepgramSuggested = { "aura-2-thalia-en", "aura-2-apollo-en", "aura-2-amalthea-en" };

        public static string Provider { get { return Settings.Get("voice.provider", "gemini") == "deepgram" ? "deepgram" : "gemini"; } }
        public static string GeminiModel { get { return Settings.Get("voice.model.gemini", GeminiSuggested[0]); } }
        public static string DeepgramVoice { get { return Settings.Get("voice.voice.deepgram", DeepgramSuggested[0]); } }
        public static string Current { get { return Provider == "deepgram" ? DeepgramVoice : GeminiModel; } }

        public static string System
        {
            get { return Ai.System + " Ini percakapan suara: jawab pendek dan alami untuk diucapkan, tanpa daftar, tanda bintang, atau format markdown."; }
        }

        public static string Describe(string m)
        {
            switch (m)
            {
                case "aura-2-thalia-en": return "Thalia, perempuan";
                case "aura-2-apollo-en": return "Apollo, laki-laki";
                case "aura-2-amalthea-en": return "Amalthea, perempuan, logat Filipina";
                default: return "";
            }
        }

        public static bool IsDeepgram(string m) { return m.StartsWith("aura", StringComparison.OrdinalIgnoreCase); }

        public static List<string> Added(string provider)
        {
            return Settings.Get("voice.models." + provider, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Every choice as (provider, model): Gemini first, then Deepgram, each with the user's own additions.
        public static List<KeyValuePair<string, string>> All()
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (string prov in new[] { "gemini", "deepgram" })
            {
                var names = new List<string>(prov == "gemini" ? GeminiSuggested : DeepgramSuggested);
                foreach (string m in Added(prov)) if (!names.Contains(m)) names.Add(m);
                foreach (string m in names) list.Add(new KeyValuePair<string, string>(prov, m));
            }
            return list;
        }

        public static void Select(string provider, string model)
        {
            Settings.Set("voice.provider", provider);
            Settings.Set(provider == "gemini" ? "voice.model.gemini" : "voice.voice.deepgram", model);
        }

        public static void Add(string provider, string model)
        {
            var list = Added(provider);
            if (!list.Contains(model)) list.Add(model);
            Settings.Set("voice.models." + provider, string.Join("|", list.ToArray()));
        }

        public static void Remove(string provider, string model)
        {
            var list = Added(provider);
            list.Remove(model);
            Settings.Set("voice.models." + provider, string.Join("|", list.ToArray()));
            if (Current == model) Select(provider, provider == "gemini" ? GeminiSuggested[0] : DeepgramSuggested[0]);
        }

        // Names the provider accepts for a spoken session: Gemini models with bidiGenerateContent, Deepgram Aura voices.
        public static List<string> Available(string provider)
        {
            string label;
            return Secrets.Call(provider, key =>
            {
                var result = new List<string>();
                if (provider == "gemini")
                {
                    var json = Ai.GetJson("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000", new Dictionary<string, string> { { "x-goog-api-key", key } });
                    var models = Ai.Child(json, "models") as object[];
                    if (models != null)
                        foreach (object m in models)
                        {
                            var methods = Ai.Child(m, "supportedGenerationMethods") as object[];
                            string name = Ai.Child(m, "name") as string;
                            if (name != null && methods != null && methods.Contains("bidiGenerateContent")) result.Add(name.Substring(name.IndexOf('/') + 1));
                        }
                }
                else
                {
                    var json = Ai.GetJson("https://api.deepgram.com/v1/models", new Dictionary<string, string> { { "Authorization", "Token " + key } });
                    var tts = Ai.Child(json, "tts") as object[];
                    if (tts != null)
                        foreach (object m in tts) { string n = Ai.Child(m, "canonical_name") as string; if (n != null) result.Add(n); }
                }
                return result;
            }, false, out label);
        }

        public static VoiceSession Create()
        {
            return Provider == "deepgram" ? (VoiceSession)new DeepgramVoice() : new GeminiVoice();
        }
    }
}
