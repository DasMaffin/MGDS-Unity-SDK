using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace DasMaffin.Mgds
{
    /// <summary>A push message: the trigger to run, and its arguments.</summary>
    public sealed class PushMessage
    {
        public long Id { get; set; }

        /// <summary>The trigger's name: the method the game runs.</summary>
        public string Trigger { get; set; }

        public JObject Arguments { get; set; } = new JObject();
        public DateTime SentAt { get; set; }
        public DateTime ExpiresAt { get; set; }

        /// <summary>sticky or live.</summary>
        public string Delivery { get; set; }

        /// <summary>The connection it came down, for acknowledging.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string Connection { get; set; }

        /// <summary>An argument as <typeparamref name="T"/>, or <paramref name="fallback"/> when it was not sent.</summary>
        public T Get<T>(string name, T fallback = default) =>
            Arguments != null && Arguments.TryGetValue(name, out JToken value) && value.Type != JTokenType.Null ? MgdsJson.To<T>(value) : fallback;
    }

    /// <summary>A message in the player's history.</summary>
    public sealed class PushHistoryItem
    {
        public long Id { get; set; }
        public string Trigger { get; set; }
        public JObject Arguments { get; set; }
        public DateTime SentAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Delivery { get; set; }
        public DateTime? ReceivedAt { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public DateTime? RecalledAt { get; set; }
    }

    /// <summary>
    /// Push messages: the dashboard makes every running game run a method - start maintenance,
    /// show a banner, hand out a gift. Register a handler per trigger with <see cref="On"/> and
    /// <see cref="Connect"/>; the SDK holds the connection, reconnects, catches up on what it
    /// missed, never hands the same message twice, and tells the dashboard each one was run.
    /// </summary>
    /// <remarks>
    /// Messages arrive as they are sent. On WebGL, where Unity cannot read a response while it is
    /// still arriving, the SDK asks again and again instead (a long poll), which arrives just as fast.
    /// </remarks>
    public sealed class MgdsPush
    {
        private const string LastIdKey = "push.last";
        private const int WaitSeconds = 25;

        private readonly MgdsManager m_Manager;
        private readonly Dictionary<string, List<Action<PushMessage>>> m_Handlers = new Dictionary<string, List<Action<PushMessage>>>();
        private readonly HashSet<long> m_Seen = new HashSet<long>();
        private readonly Queue<long> m_SeenOrder = new Queue<long>();

        private int m_Generation;
        private UnityWebRequest m_Open;
        private long m_LastId;

        internal MgdsPush(MgdsManager manager)
        {
            m_Manager = manager;
            long.TryParse(manager.Store.Get(LastIdKey), out m_LastId);

            // A token sent along decides what the player gets: reconnect when it changes.
            manager.Auth.SignedIn += Refresh;
            manager.Auth.SignedOut += Refresh;
        }

        /// <summary>Raised for every message, before its trigger's handlers.</summary>
        public event Action<PushMessage> MessageReceived;

        /// <summary>A message that went out was recalled: undo it if the game can. The message id and its trigger.</summary>
        public event Action<long, string> Recalled;

        /// <summary>Raised each time a connection opens.</summary>
        public event Action Connected;

        /// <summary>Whether the game wants to be connected (it may be between reconnects).</summary>
        public bool IsListening { get; private set; }

        /// <summary>The open connection's id, or null.</summary>
        public string ConnectionId { get; private set; }

        public IReadOnlyList<string> Channels { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// What conditions on a message are checked against, as for remote config. Starts as
        /// <see cref="MgdsRemoteConfig.Attributes"/>; change it and call <see cref="Refresh"/>.
        /// </summary>
        public JObject Attributes { get; set; }

        /// <summary>Tells the dashboard a message was run when its handlers finished without an exception. On by default.</summary>
        public bool AutoAcknowledge { get; set; } = true;

        /// <summary>Runs <paramref name="handler"/> for every message of <paramref name="trigger"/>.</summary>
        public void On(string trigger, Action<PushMessage> handler)
        {
            if (!m_Handlers.TryGetValue(trigger, out List<Action<PushMessage>> list))
                m_Handlers[trigger] = list = new List<Action<PushMessage>>();

            list.Add(handler);
        }

        public void Off(string trigger, Action<PushMessage> handler)
        {
            if (m_Handlers.TryGetValue(trigger, out List<Action<PushMessage>> list))
                list.Remove(handler);
        }

        /// <summary>
        /// Starts listening, on <paramref name="channels"/> too when given (a region, a guild, a
        /// match: up to 20). Keeps going until <see cref="Disconnect"/>. Calling it again changes the channels.
        /// </summary>
        public void Connect(IEnumerable<string> channels = null)
        {
            Channels = (channels ?? Channels).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
            IsListening = true;
            Restart();
        }

        /// <summary>Stops listening.</summary>
        public void Disconnect()
        {
            IsListening = false;
            m_Generation++;
            Close();
        }

        /// <summary>Reconnects now, with the current channels, attributes and player.</summary>
        public void Refresh()
        {
            if (IsListening)
                Restart();
        }

        /// <summary>Tells the dashboard the game ran a message. Done by itself unless <see cref="AutoAcknowledge"/> is off.</summary>
        public async Task AcknowledgeAsync(PushMessage message, CancellationToken cancel = default)
        {
            JObject body = m_Manager.Auth.IsSignedIn || message.Connection == null ? null : new JObject { ["connection"] = message.Connection };
            await m_Manager.Http.JsonAsync("POST", $"/push/messages/{message.Id}/ack", body, Token.IfSignedIn, cancel);
        }

        /// <summary>Every message the signed-in player's games were handed, newest first: for an inbox.</summary>
        public async Task<List<PushHistoryItem>> GetHistoryAsync(int limit = 50, long? before = null, CancellationToken cancel = default)
        {
            string path = $"/me/push?limit={limit}" + (before == null ? "" : "&before=" + before);
            return (await m_Manager.Http.JsonAsync("GET", path, null, Token.Required, cancel)).Json["results"]?
                .ToObject<List<PushHistoryItem>>(MgdsJson.Serializer) ?? new List<PushHistoryItem>();
        }

        // --- the connection ----------------------------------------------------------------------

        private void Restart()
        {
            int generation = ++m_Generation;
            Close();
            _ = RunAsync(generation);
        }

        private void Close()
        {
            ConnectionId = null;

            if (m_Open != null)
            {
                try { m_Open.Abort(); } catch (Exception) { }
                m_Open = null;
            }
        }

        private async Task RunAsync(int generation)
        {
            bool longPoll = Application.platform == RuntimePlatform.WebGLPlayer;
            float delay = 0;

            while (generation == m_Generation && m_Manager != null)
            {
                if (delay > 0)
                {
                    try
                    {
                        await m_Manager.DelayAsync(delay, m_Manager.Stopping);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (generation != m_Generation)
                        return;
                }

                delay = 3;

                try
                {
                    string token = null;
                    if (m_Manager.Auth.IsSignedIn)
                    {
                        await m_Manager.Auth.EnsureFreshAsync(m_Manager.Stopping);
                        token = m_Manager.Auth.SessionToken;
                    }

                    var reader = new SseReader();

                    // WebGL hands a response over only once it is complete: there, the whole poll is read at the end.
                    DownloadHandler handler = longPoll
                        ? new DownloadHandlerBuffer()
                        : (DownloadHandler)new StreamHandler(reader, e => Handle(e, generation));

                    using (var request = new UnityWebRequest(Address(longPoll), "GET") { downloadHandler = handler })
                    {
                        request.SetRequestHeader("Accept", "text/event-stream");
                        if (token != null)
                            request.SetRequestHeader("Authorization", "Bearer " + token);

                        m_Open = request;
                        await request.SendAsync(m_Manager.Stopping);

                        if (generation != m_Generation)
                            return;

                        m_Open = null;

                        if (longPoll)
                        {
                            foreach (SseEvent e in reader.Feed(request.downloadHandler.text ?? ""))
                                Handle(e, generation);
                        }

                        ConnectionId = null;

                        long status = request.responseCode;

                        if (status == 401 && token != null)
                        {
                            // The token stopped working: refresh, or carry on as nobody.
                            await m_Manager.Auth.RecoverAsync(token, m_Manager.Stopping);
                            delay = 0;
                        }
                        else if (status == 503)
                        {
                            delay = float.TryParse(request.GetResponseHeader("Retry-After"), out float wait) ? wait : 30;
                        }
                        else if (status >= 400)
                        {
                            Debug.LogError($"[MGDS] Push messages refused the connection ({status}): {reader.Leftover}. Not reconnecting.");
                            IsListening = false;
                            return;
                        }
                        else if (reader.EndedOnPurpose || longPoll)
                        {
                            delay = 0;
                        }
                        else if (reader.RetryMilliseconds > 0)
                        {
                            delay = reader.RetryMilliseconds / 1000f;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (m_Manager.LogRequests)
                        Debug.LogWarning("[MGDS] Push connection dropped: " + e.Message);
                }
            }
        }

        private string Address(bool longPoll)
        {
            var query = new List<string>();

            if (!string.IsNullOrEmpty(m_Manager.Environment))
                query.Add("environment=" + MgdsHttp.Escape(m_Manager.Environment));

            if (Channels.Count > 0)
                query.Add("channels=" + MgdsHttp.Escape(string.Join(",", Channels)));

            JObject attributes = Attributes ?? m_Manager.Config.Attributes;
            if (attributes != null && attributes.Count > 0)
                query.Add("attributes=" + MgdsHttp.Escape(MgdsJson.Write(attributes)));

            if (m_LastId > 0)
                query.Add("lastEventId=" + m_LastId);

            if (longPoll)
                query.Add("wait=" + WaitSeconds);

            return m_Manager.Http.Url("/push/stream") + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        }

        private void Handle(SseEvent e, int generation)
        {
            if (generation != m_Generation)
                return;

            JToken data;
            try
            {
                data = MgdsJson.Parse(e.Data);
            }
            catch (Exception)
            {
                return;
            }

            switch (e.Name)
            {
                case "mgds.connected":
                    ConnectionId = (string)data["connection"];
                    Connected?.Invoke();
                    return;

                case "mgds.recalled":
                    Recalled?.Invoke((long?)data["id"] ?? 0, (string)data["trigger"]);
                    return;

                case "mgds.reconnect":
                    return;
            }

            if (e.Name.StartsWith("mgds.", StringComparison.Ordinal))
                return;

            PushMessage message = MgdsJson.To<PushMessage>(data);
            if (message == null)
                return;

            message.Connection = ConnectionId;

            // At least once: a message's id never repeats, so one seen is skipped.
            if (!m_Seen.Add(message.Id))
                return;

            m_SeenOrder.Enqueue(message.Id);
            while (m_SeenOrder.Count > 500)
                m_Seen.Remove(m_SeenOrder.Dequeue());

            if (message.Id > m_LastId)
            {
                m_LastId = message.Id;
                m_Manager.Store.Set(LastIdKey, m_LastId.ToString());
            }

            bool ran = true;

            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                ran = false;
                Debug.LogException(ex);
            }

            if (m_Handlers.TryGetValue(message.Trigger ?? "", out List<Action<PushMessage>> handlers))
            {
                foreach (Action<PushMessage> handler in handlers.ToList())
                {
                    try
                    {
                        handler(message);
                    }
                    catch (Exception ex)
                    {
                        ran = false;
                        Debug.LogException(ex);
                    }
                }
            }
            else if (MessageReceived == null)
            {
                Debug.LogWarning($"[MGDS] Push message {message.Trigger} arrived and nothing handles it: Mgds.Push.On(\"{message.Trigger}\", ...).");
                ran = false;
            }

            if (ran && AutoAcknowledge)
                _ = AcknowledgeQuietlyAsync(message);
        }

        private async Task AcknowledgeQuietlyAsync(PushMessage message)
        {
            try
            {
                await AcknowledgeAsync(message, m_Manager.Stopping);
            }
            catch (Exception)
            {
                // Counting it is the dashboard's business; the game has done its part.
            }
        }

        /// <summary>Hands the text to the reader as it arrives.</summary>
        private sealed class StreamHandler : DownloadHandlerScript
        {
            private readonly SseReader m_Reader;
            private readonly Action<SseEvent> m_OnEvent;
            private readonly Decoder m_Decoder = new UTF8Encoding(false).GetDecoder();
            private readonly char[] m_Chars = new char[8192];

            public StreamHandler(SseReader reader, Action<SseEvent> onEvent)
                : base(new byte[4096])
            {
                m_Reader = reader;
                m_OnEvent = onEvent;
            }

            protected override bool ReceiveData(byte[] data, int length)
            {
                int offset = 0;

                while (offset < length)
                {
                    int take = Math.Min(length - offset, m_Chars.Length / 2);
                    int count = m_Decoder.GetChars(data, offset, take, m_Chars, 0);
                    offset += take;

                    foreach (SseEvent e in m_Reader.Feed(new string(m_Chars, 0, count)))
                        m_OnEvent(e);
                }

                return true;
            }
        }
    }

    internal struct SseEvent
    {
        public string Name;
        public string Data;
        public long? Id;
    }

    /// <summary>Server-Sent Events, read line by line from text as it arrives.</summary>
    internal sealed class SseReader
    {
        private readonly StringBuilder m_Line = new StringBuilder();
        private readonly StringBuilder m_Data = new StringBuilder();
        private readonly StringBuilder m_Everything = new StringBuilder();
        private string m_Name;
        private long? m_Id;

        public int RetryMilliseconds { get; private set; }

        /// <summary>The stream said to reconnect (mgds.reconnect).</summary>
        public bool EndedOnPurpose { get; private set; }

        /// <summary>The start of whatever arrived, for an error message.</summary>
        public string Leftover => m_Everything.ToString();

        public IEnumerable<SseEvent> Feed(string text)
        {
            var events = new List<SseEvent>();

            if (m_Everything.Length < 500)
                m_Everything.Append(text, 0, Math.Min(text.Length, 500 - m_Everything.Length));

            foreach (char c in text)
            {
                if (c != '\n')
                {
                    m_Line.Append(c);
                    continue;
                }

                string line = m_Line.ToString().TrimEnd('\r');
                m_Line.Clear();

                if (line.Length == 0)
                {
                    if (m_Data.Length > 0)
                    {
                        string name = m_Name ?? "message";
                        if (name == "mgds.reconnect")
                            EndedOnPurpose = true;

                        events.Add(new SseEvent { Name = name, Data = m_Data.ToString(), Id = m_Id });
                    }

                    m_Data.Clear();
                    m_Name = null;
                    m_Id = null;
                }
                else if (line.StartsWith(":", StringComparison.Ordinal))
                {
                    // A heartbeat.
                }
                else
                {
                    int colon = line.IndexOf(':');
                    string field = colon < 0 ? line : line.Substring(0, colon);
                    string value = colon < 0 ? "" : line.Substring(colon + 1).TrimStart(' ');

                    switch (field)
                    {
                        case "event": m_Name = value; break;
                        case "data": if (m_Data.Length > 0) m_Data.Append('\n'); m_Data.Append(value); break;
                        case "id": m_Id = long.TryParse(value, out long id) ? id : (long?)null; break;
                        case "retry": if (int.TryParse(value, out int ms)) RetryMilliseconds = ms; break;
                    }
                }
            }

            return events;
        }
    }
}
