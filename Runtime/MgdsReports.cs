using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace DasMaffin.Mgds
{
    public enum ReportKind
    {
        Bug,
        Idea,
        Other,
    }

    /// <summary>
    /// A report from the player: what happened, in their words, and whatever the game attaches.
    /// Logs, the device and the scene are added by themselves unless turned off.
    /// </summary>
    public sealed class MgdsReport
    {
        internal readonly List<(string Field, byte[] Content, string Name, string Mime)> Files =
            new List<(string, byte[], string, string)>();

        /// <summary>One line, up to 300 characters. Required.</summary>
        public string Summary { get; set; }

        /// <summary>What happened, up to 8,000 characters. Required.</summary>
        public string Description { get; set; }

        public ReportKind Kind { get; set; } = ReportKind.Bug;

        /// <summary>The game's version. <see cref="Application.version"/> when left out.</summary>
        public string Build { get; set; }

        /// <summary>Where the player was. The active scene's name when left out.</summary>
        public string Scene { get; set; }

        /// <summary>A name, for when nobody is signed in. A signed-in player's own name is used instead.</summary>
        public string PlayerName { get; set; }

        /// <summary>Attaches the game's latest log lines (kept by the manager).</summary>
        public bool IncludeLogs { get; set; } = true;

        /// <summary>Attaches the device, its system, memory, graphics and language.</summary>
        public bool IncludeDeviceInfo { get; set; } = true;

        /// <summary>Facts of your own, shown as a table on the dashboard: any object.</summary>
        public object Info { get; set; }

        /// <summary>A PNG, JPEG, GIF or WebP, shown on the dashboard.</summary>
        public MgdsReport AddImage(byte[] content, string name = "screenshot.png") => Add("images", content, name, MimeOf(name, "image/png"));

        /// <summary>An MP4 or WebM, played on the dashboard.</summary>
        public MgdsReport AddVideo(byte[] content, string name = "recording.mp4") => Add("videos", content, name, MimeOf(name, "video/mp4"));

        /// <summary>A log of your own, as plain text.</summary>
        public MgdsReport AddLog(string text, string name = "log.txt") => Add("logs", Encoding.UTF8.GetBytes(text), name, "text/plain");

        /// <summary>Any other file: a save, a config. Offered as a download.</summary>
        public MgdsReport AddFile(byte[] content, string name, string mime = "application/octet-stream") => Add("files", content, name, mime);

        private MgdsReport Add(string field, byte[] content, string name, string mime)
        {
            Files.Add((field, content, name, mime));
            return this;
        }

        private static string MimeOf(string name, string fallback)
        {
            string lower = name.ToLowerInvariant();
            if (lower.EndsWith(".png")) return "image/png";
            if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")) return "image/jpeg";
            if (lower.EndsWith(".gif")) return "image/gif";
            if (lower.EndsWith(".webp")) return "image/webp";
            if (lower.EndsWith(".mp4")) return "video/mp4";
            if (lower.EndsWith(".webm")) return "video/webm";
            return fallback;
        }
    }

    /// <summary>What MGDS answered to a report.</summary>
    public sealed class ReportReceipt
    {
        public int Id { get; set; }

        /// <summary>The player it belongs to, or null when nobody was signed in.</summary>
        public string PlayerId { get; set; }
    }

    public sealed class ReportMessage
    {
        /// <summary>True for your answers, false for the player's own messages.</summary>
        public bool FromDeveloper { get; set; }

        public string Body { get; set; }
        public DateTime WrittenAt { get; set; }
    }

    /// <summary>One of the signed-in player's reports, and the conversation on it.</summary>
    public sealed class PlayerReport
    {
        public int Id { get; set; }
        public string Kind { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool Open { get; set; }

        /// <summary>Your answers they have not read yet.</summary>
        public int Unread { get; set; }

        /// <summary>In the list: how many messages there are. In a single report, see <see cref="Conversation"/>.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public List<ReportMessage> Conversation { get; set; } = new List<ReportMessage>();
    }

    public sealed class PlayerReports
    {
        /// <summary>Unread answers across all of them: what a badge shows.</summary>
        public int Unread { get; set; }

        public List<PlayerReport> Reports { get; set; } = new List<PlayerReport>();
    }

    /// <summary>
    /// Reports: a player tells you something went wrong, or has an idea, from inside the game.
    /// Nobody needs to be signed in; when somebody is, the report is theirs and they can read your
    /// answers here.
    /// </summary>
    public sealed class MgdsReports
    {
        private readonly MgdsManager m_Manager;

        internal MgdsReports(MgdsManager manager) => m_Manager = manager;

        /// <summary>Sends a report. 429 after 12 in an hour.</summary>
        public async Task<ReportReceipt> SendAsync(MgdsReport report, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(report.Summary) || string.IsNullOrWhiteSpace(report.Description))
                throw new ArgumentException("A report needs a summary and a description.", nameof(report));

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("summary", report.Summary),
                new MultipartFormDataSection("description", report.Description),
                new MultipartFormDataSection("kind", report.Kind.ToString()),
                new MultipartFormDataSection("build", report.Build ?? Application.version),
                new MultipartFormDataSection("scene", report.Scene ?? SceneManager.GetActiveScene().name),
            };

            if (!string.IsNullOrEmpty(report.PlayerName))
                form.Add(new MultipartFormDataSection("playerName", report.PlayerName));

            if (report.IncludeLogs && m_Manager.Logs.Count > 0)
                form.Add(new MultipartFormFileSection("logs", Encoding.UTF8.GetBytes(m_Manager.Logs.ToJson()), "unity.json", "application/json"));

            JObject info = report.IncludeDeviceInfo ? DeviceInfo() : new JObject();
            if (report.Info != null && MgdsJson.From(report.Info) is JObject own)
                info.Merge(own);

            if (info.Count > 0)
                form.Add(new MultipartFormFileSection("info", Encoding.UTF8.GetBytes(MgdsJson.Write(info)), "info.json", "application/json"));

            foreach (var (field, content, name, mime) in report.Files)
                form.Add(new MultipartFormFileSection(field, content, name, mime));

            return (await m_Manager.Http.FormAsync("POST", "/reports", form, Token.IfSignedIn, cancel)).As<ReportReceipt>();
        }

        /// <summary>
        /// The screen as a PNG, taken at the end of this frame - before a report window opens over
        /// it, ideally.
        /// </summary>
        public async Task<byte[]> CaptureScreenshotAsync()
        {
            byte[] png = null;
            await m_Manager.RunAsync(Capture());
            return png;

            IEnumerator Capture()
            {
                yield return new WaitForEndOfFrame();
                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                png = shot.EncodeToPNG();
                UnityEngine.Object.Destroy(shot);
            }
        }

        /// <summary>The log lines a report would carry now.</summary>
        public IReadOnlyList<string> RecentLogs => m_Manager.Logs.Lines;

        // --- answers, in the game ----------------------------------------------------------------

        /// <summary>The signed-in player's reports, with how many of your answers they have not read.</summary>
        public async Task<PlayerReports> GetMineAsync(CancellationToken cancel = default) =>
            (await m_Manager.Http.JsonAsync("GET", "/me/reports", null, Token.Required, cancel)).As<PlayerReports>();

        /// <summary>One of their reports with its conversation. Opening it marks your answers read.</summary>
        public async Task<PlayerReport> GetMineAsync(int id, CancellationToken cancel = default)
        {
            JToken answer = (await m_Manager.Http.JsonAsync("GET", "/me/reports/" + id, null, Token.Required, cancel)).Json;
            PlayerReport report = MgdsJson.To<PlayerReport>(answer);
            report.Conversation = answer["messages"]?.ToObject<List<ReportMessage>>(MgdsJson.Serializer) ?? new List<ReportMessage>();
            return report;
        }

        /// <summary>Writes in the conversation on one of their reports. 409 once it is closed.</summary>
        public Task ReplyAsync(int id, string body, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("POST", $"/me/reports/{id}/messages", new JObject { ["body"] = body }, Token.Required, cancel);

        /// <summary>The player says it is sorted.</summary>
        public Task CloseAsync(int id, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("POST", $"/me/reports/{id}/close", null, Token.Required, cancel);

        private static JObject DeviceInfo() => new JObject
        {
            ["device"] = new JObject
            {
                ["model"] = SystemInfo.deviceModel,
                ["os"] = SystemInfo.operatingSystem,
                ["cpu"] = SystemInfo.processorType,
                ["memoryMb"] = SystemInfo.systemMemorySize,
                ["gpu"] = SystemInfo.graphicsDeviceName,
                ["gpuMemoryMb"] = SystemInfo.graphicsMemorySize,
            },
            ["screen"] = $"{Screen.width}x{Screen.height}",
            ["platform"] = Application.platform.ToString(),
            ["unity"] = Application.unityVersion,
            ["language"] = Application.systemLanguage.ToString(),
        };
    }

    /// <summary>The game's latest log lines, for a report to carry.</summary>
    internal sealed class MgdsLogBuffer
    {
        private readonly int m_Most;
        private readonly Queue<JObject> m_Entries = new Queue<JObject>();

        public MgdsLogBuffer(int most) => m_Most = most;

        public int Count => m_Entries.Count;

        public IReadOnlyList<string> Lines => m_Entries.Select(e => $"[{e["level"]}] {e["message"]}").ToList();

        public void Add(string message, string stack, LogType type)
        {
            if (m_Most <= 0)
                return;

            var entry = new JObject
            {
                ["frame"] = Time.frameCount,
                ["at"] = Math.Round(Time.realtimeSinceStartup, 3),
                ["level"] = type.ToString(),
                ["message"] = message,
            };

            if (type != LogType.Log && type != LogType.Warning && !string.IsNullOrEmpty(stack))
                entry["stack"] = stack;

            m_Entries.Enqueue(entry);
            while (m_Entries.Count > m_Most)
                m_Entries.Dequeue();
        }

        public string ToJson() => MgdsJson.Write(new JObject { ["entries"] = new JArray(m_Entries) });
    }
}
