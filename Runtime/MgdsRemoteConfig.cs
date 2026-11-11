using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DasMaffin.Mgds
{
    /// <summary>A picture or video setting. Fetch it with <see cref="MgdsRemoteConfig.DownloadAsync(string, CancellationToken)"/>.</summary>
    public sealed class ConfigMedia
    {
        public string Url { get; set; }

        /// <summary>What the file is once unpacked: image/png, video/mp4...</summary>
        public string ContentType { get; set; }

        /// <summary>none, or gzip (the SDK unpacks it).</summary>
        public string Compression { get; set; }

        public long Bytes { get; set; }
        public string FileName { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? Seconds { get; set; }
    }

    public sealed class ConfigRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Remote config: values the game fetches, changed on the dashboard without a new build. Fetch
    /// once (the manager can do it when the game starts), then read with <see cref="Get{T}"/>.
    /// </summary>
    public sealed class MgdsRemoteConfig
    {
        private readonly MgdsManager m_Manager;
        private readonly Dictionary<string, byte[]> m_Media = new Dictionary<string, byte[]>();
        private JObject m_Values = new JObject();
        private Dictionary<string, string> m_Types = new Dictionary<string, string>();

        internal MgdsRemoteConfig(MgdsManager manager) => m_Manager = manager;

        /// <summary>
        /// What the game tells MGDS about itself, for rules to match on: <c>platform</c>,
        /// <c>app.version</c>, <c>user.level</c>... Filled with <c>platform</c> and
        /// <c>app.version</c> to begin with; add your own before fetching.
        /// </summary>
        public JObject Attributes { get; } = new JObject
        {
            ["platform"] = Application.platform.ToString(),
            ["app"] = new JObject { ["version"] = Application.version },
        };

        /// <summary>Raised after every successful fetch.</summary>
        public event Action Fetched;

        public bool IsFetched { get; private set; }

        /// <summary>The environment the values came from.</summary>
        public string Environment { get; private set; }

        /// <summary>The rules that changed something for this player.</summary>
        public List<ConfigRule> AppliedRules { get; private set; } = new List<ConfigRule>();

        /// <summary>Every value, by key.</summary>
        public JObject Values => m_Values;

        /// <summary>
        /// Fetches the values for this player. <paramref name="attributes"/>, when given, are added to
        /// <see cref="Attributes"/>; <paramref name="environment"/> overrides the manager's.
        /// </summary>
        public async Task FetchAsync(object attributes = null, string environment = null, CancellationToken cancel = default)
        {
            if (attributes != null && MgdsJson.From(attributes) is JObject extra)
                Attributes.Merge(extra, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });

            var body = new JObject
            {
                ["userId"] = m_Manager.Auth.PlayerId ?? m_Manager.Store.InstallId,
                ["attributes"] = Attributes,
            };

            string chosen = environment ?? m_Manager.Environment;
            if (!string.IsNullOrEmpty(chosen))
                body["environment"] = chosen;

            JToken answer = (await m_Manager.Http.JsonAsync("POST", "/config", body, Token.IfSignedIn, cancel)).Json;

            m_Values = answer["configs"] as JObject ?? new JObject();
            m_Types = answer["types"]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
            AppliedRules = answer["rules"]?.ToObject<List<ConfigRule>>() ?? new List<ConfigRule>();
            Environment = (string)answer["environment"];
            IsFetched = true;

            Fetched?.Invoke();
        }

        public bool Has(string key) => m_Values.ContainsKey(key);

        /// <summary>string, bool, int, long, float, json, image or video; null when there is no such key.</summary>
        public string TypeOf(string key) => m_Types.TryGetValue(key, out string type) ? type : null;

        /// <summary>A value as <typeparamref name="T"/>, or <paramref name="fallback"/> when it is not set (or not fetched yet).</summary>
        public T Get<T>(string key, T fallback = default)
        {
            if (!m_Values.TryGetValue(key, out JToken value) || value.Type == JTokenType.Null)
                return fallback;

            try
            {
                return MgdsJson.To<T>(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public string GetString(string key, string fallback = "") => Get(key, fallback);

        public bool GetBool(string key, bool fallback = false) => Get(key, fallback);

        public int GetInt(string key, int fallback = 0) => Get(key, fallback);

        public long GetLong(string key, long fallback = 0) => Get(key, fallback);

        public float GetFloat(string key, float fallback = 0) => Get(key, fallback);

        /// <summary>A json setting as it is.</summary>
        public JToken GetJson(string key) => m_Values.TryGetValue(key, out JToken value) ? value : null;

        /// <summary>An image or video setting, or null.</summary>
        public ConfigMedia GetMedia(string key) =>
            TypeOf(key) == "image" || TypeOf(key) == "video" ? Get<ConfigMedia>(key) : null;

        /// <summary>An image or video setting's file, unpacked. Kept in memory: an address never changes what it holds.</summary>
        public Task<byte[]> DownloadAsync(string key, CancellationToken cancel = default)
        {
            ConfigMedia media = GetMedia(key) ?? throw new KeyNotFoundException($"Remote config has no image or video called {key}.");
            return DownloadAsync(media, cancel);
        }

        public async Task<byte[]> DownloadAsync(ConfigMedia media, CancellationToken cancel = default)
        {
            if (m_Media.TryGetValue(media.Url, out byte[] kept))
                return kept;

            byte[] bytes = (await m_Manager.Http.GetBytesAsync(media.Url, Token.None, cancel)).Bytes;

            if (media.Compression == "gzip")
            {
                using (var packed = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
                using (var unpacked = new MemoryStream())
                {
                    packed.CopyTo(unpacked);
                    bytes = unpacked.ToArray();
                }
            }

            m_Media[media.Url] = bytes;
            return bytes;
        }

        /// <summary>An image setting as a texture. PNG and JPEG; WebP needs a decoder of your own.</summary>
        public async Task<Texture2D> LoadTextureAsync(string key, CancellationToken cancel = default)
        {
            byte[] bytes = await DownloadAsync(key, cancel);
            var texture = new Texture2D(2, 2);

            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException($"Remote config's {key} is not a picture Unity can load ({GetMedia(key)?.ContentType}).");
            }

            texture.name = key;
            return texture;
        }
    }
}
