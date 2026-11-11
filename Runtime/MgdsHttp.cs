using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace DasMaffin.Mgds
{
    /// <summary>
    /// MGDS said no, or could not be reached. <see cref="StatusCode"/> is the HTTP status (0 when
    /// there was no answer at all) and <see cref="Error"/> MGDS's own words for it.
    /// </summary>
    public sealed class MgdsException : Exception
    {
        public MgdsException(long statusCode, string error)
            : base(statusCode == 0 ? "MGDS could not be reached: " + error : $"MGDS answered {statusCode}: {error}")
        {
            StatusCode = statusCode;
            Error = error;
        }

        /// <summary>The HTTP status, or 0 when MGDS could not be reached.</summary>
        public long StatusCode { get; }

        /// <summary>What MGDS said was wrong.</summary>
        public string Error { get; }

        /// <summary>No connection: offline, or MGDS could not be reached.</summary>
        public bool IsNetworkError => StatusCode == 0;

        /// <summary>Not signed in, or the session ended.</summary>
        public bool IsUnauthorized => StatusCode == 401;

        public bool IsNotFound => StatusCode == 404;
    }

    internal enum Token
    {
        /// <summary>Never sends the player's token.</summary>
        None,

        /// <summary>Sends it when somebody is signed in.</summary>
        IfSignedIn,

        /// <summary>Refuses to make the call without a signed-in player.</summary>
        Required,
    }

    internal sealed class MgdsResponse
    {
        private JToken m_Json;

        public long Status;
        public string Text;
        public byte[] Bytes;
        public string RetryAfter;

        public JToken Json => m_Json ??= MgdsJson.Parse(Text);

        public T As<T>() => MgdsJson.To<T>(Json);
    }

    internal static class MgdsJson
    {
        /// <summary>Dates stay text inside a game's own JSON: a save holding "2026-01-01" gets back "2026-01-01".</summary>
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Include,
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        });

        public static JToken Parse(string text) =>
            string.IsNullOrWhiteSpace(text) ? JValue.CreateNull() : JsonConvert.DeserializeObject<JToken>(text, Settings);

        public static T To<T>(JToken token) => token == null || token.Type == JTokenType.Null ? default : token.ToObject<T>(Serializer);

        public static JToken From(object value) => value == null ? JValue.CreateNull() : value as JToken ?? JToken.FromObject(value, Serializer);

        public static string Write(JToken token) => token.ToString(Formatting.None);
    }

    /// <summary>Every request to MGDS: the address, the token, one retry after refreshing, and errors as exceptions.</summary>
    internal sealed class MgdsHttp
    {
        private readonly MgdsManager m_Manager;

        public MgdsHttp(MgdsManager manager) => m_Manager = manager;

        /// <summary>https://www.dasmaffin.com/api/mgds/v1/projects/{projectId}</summary>
        public string ProjectRoot => $"{m_Manager.ServerUrl}/api/mgds/v1/projects/{m_Manager.ProjectId}";

        public string Url(string path) => path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ProjectRoot + path;

        public Task<MgdsResponse> JsonAsync(string method, string path, JToken body, Token token, CancellationToken cancel, params long[] alsoFine) =>
            SendAsync(method, path, token, cancel, alsoFine, () =>
            {
                var request = new UnityWebRequest(Url(path), method) { downloadHandler = new DownloadHandlerBuffer() };

                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MgdsJson.Write(body)));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                return request;
            });

        public Task<MgdsResponse> FormAsync(string method, string path, List<IMultipartFormSection> form, Token token, CancellationToken cancel) =>
            SendAsync(method, path, token, cancel, Array.Empty<long>(), () =>
            {
                UnityWebRequest request = UnityWebRequest.Post(Url(path), form);
                request.method = method;
                request.downloadHandler = new DownloadHandlerBuffer();
                return request;
            });

        public Task<MgdsResponse> GetBytesAsync(string url, Token token, CancellationToken cancel) =>
            SendAsync("GET", url, token, cancel, Array.Empty<long>(), () =>
                new UnityWebRequest(Url(url), "GET") { downloadHandler = new DownloadHandlerBuffer() });

        private async Task<MgdsResponse> SendAsync(string method, string path, Token token, CancellationToken cancel, long[] alsoFine,
            Func<UnityWebRequest> make)
        {
            if (!Guid.TryParse(m_Manager.ProjectId, out _))
                throw new InvalidOperationException("The MGDS Manager has no project id.");

            MgdsAuth auth = m_Manager.Auth;

            for (int attempt = 0; ; attempt++)
            {
                string bearer = null;

                if (token != Token.None && auth.IsSignedIn)
                {
                    await auth.EnsureFreshAsync(cancel);
                    bearer = auth.SessionToken;
                }

                if (token == Token.Required && bearer == null)
                    throw new MgdsException(401, "not signed in: sign a player in first");

                using (UnityWebRequest request = make())
                {
                    if (bearer != null)
                        request.SetRequestHeader("Authorization", "Bearer " + bearer);

                    if (m_Manager.LogRequests)
                        Debug.Log($"[MGDS] {method} {request.url}");

                    await request.SendAsync(cancel);

                    long status = request.responseCode;

                    if (status == 0)
                        throw new MgdsException(0, request.error ?? "no answer");

                    // An access token that stopped working: refresh once and ask again.
                    if (status == 401 && bearer != null && attempt == 0 && await auth.RecoverAsync(bearer, cancel))
                        continue;

                    var response = new MgdsResponse
                    {
                        Status = status,
                        Bytes = request.downloadHandler?.data,
                        Text = request.downloadHandler?.text,
                        RetryAfter = request.GetResponseHeader("Retry-After"),
                    };

                    if (m_Manager.LogRequests)
                        Debug.Log($"[MGDS] {status} {method} {request.url}");

                    if ((status >= 200 && status < 300) || alsoFine.Contains(status))
                        return response;

                    throw Failure(response);
                }
            }
        }

        public static MgdsException Failure(MgdsResponse response)
        {
            string error = null;

            try
            {
                error = (string)(response.Json as JObject)?["error"];
            }
            catch (JsonException)
            {
            }

            return new MgdsException(response.Status, error ?? response.Text ?? "no reason given");
        }

        public static string Escape(string segment) => Uri.EscapeDataString(segment ?? "");
    }

    internal static class UnityWebRequestAwaiting
    {
        /// <summary>Sends and completes when the answer is in. Cancelling aborts the request.</summary>
        public static Task SendAsync(this UnityWebRequest request, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<bool>();
            cancel.ThrowIfCancellationRequested();

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            SynchronizationContext main = SynchronizationContext.Current;

            CancellationTokenRegistration registration = cancel.CanBeCanceled
                ? cancel.Register(() =>
                {
                    // Unity's objects belong to the main thread.
                    void Abort(object _)
                    {
                        done.TrySetCanceled();
                        try { request.Abort(); } catch (Exception) { }
                    }

                    if (main != null) main.Post(Abort, null);
                    else Abort(null);
                })
                : default;

            if (operation.isDone)
            {
                registration.Dispose();
                done.TrySetResult(true);
            }
            else
            {
                operation.completed += _ =>
                {
                    registration.Dispose();
                    done.TrySetResult(true);
                };
            }

            return done.Task;
        }
    }

    /// <summary>What the SDK keeps on the device between runs, per project.</summary>
    internal sealed class MgdsStore
    {
        private readonly string m_Prefix;

        public MgdsStore(string project) => m_Prefix = "mgds." + (project ?? "").Trim() + ".";

        public string Get(string key)
        {
            string value = PlayerPrefs.GetString(m_Prefix + key, "");
            return value.Length == 0 ? null : value;
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
                PlayerPrefs.DeleteKey(m_Prefix + key);
            else
                PlayerPrefs.SetString(m_Prefix + key, value);

            PlayerPrefs.Save();
        }

        /// <summary>An id for this install, for remote config rollouts before anybody signs in.</summary>
        public string InstallId
        {
            get
            {
                string id = Get("install");
                if (id == null)
                {
                    id = Guid.NewGuid().ToString();
                    Set("install", id);
                }

                return id;
            }
        }
    }
}
