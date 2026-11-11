using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace DasMaffin.Mgds
{
    /// <summary>A save file as it is listed: without its entries.</summary>
    public sealed class SaveFileInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Public { get; set; }
        public string CreatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>An uploaded file inside a save file.</summary>
    public sealed class SaveFileUpload
    {
        public string Url { get; set; }
        public string Name { get; set; }
        public long Bytes { get; set; }
        public string Mime { get; set; }
    }

    public sealed class SaveFileOwner
    {
        public string PlayerId { get; set; }
        public DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// A whole save file. Entries are under their key, so <c>save.Get&lt;int&gt;("level")</c> or
    /// <c>save.Entries["inventory"][3]["count"]</c> read them directly.
    /// </summary>
    public sealed class SaveFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Public { get; set; }
        public string CreatedBy { get; set; }
        public List<string> Owners { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Every entry's value by its key.</summary>
        public JObject Entries { get; set; } = new JObject();

        /// <summary>What each entry is: int, string, json or file.</summary>
        public Dictionary<string, string> Types { get; set; } = new Dictionary<string, string>();

        public bool Has(string key) => Entries.ContainsKey(key);

        /// <summary>int, string, json or file; null when there is no such entry.</summary>
        public string TypeOf(string key) => Types.TryGetValue(key, out string type) ? type : null;

        /// <summary>An entry as <typeparamref name="T"/>, or <paramref name="fallback"/> when it is not there.</summary>
        public T Get<T>(string key, T fallback = default) =>
            Entries.TryGetValue(key, out JToken value) && value.Type != JTokenType.Null ? MgdsJson.To<T>(value) : fallback;

        /// <summary>A file entry's details - its <c>Url</c> downloads it - or null.</summary>
        public SaveFileUpload GetUpload(string key) => TypeOf(key) == "file" ? Get<SaveFileUpload>(key) : null;
    }

    /// <summary>
    /// Several changes to one save file, saved together or not at all: its name, whether it is
    /// public, entries to set and entries to remove.
    /// </summary>
    public sealed class SaveChanges
    {
        private readonly JObject m_Entries = new JObject();
        private readonly List<string> m_Remove = new List<string>();

        /// <summary>A new name, or null to keep it.</summary>
        public string Name { get; set; }

        /// <summary>Public or private, or null to keep it.</summary>
        public bool? Public { get; set; }

        /// <summary>A whole number.</summary>
        public SaveChanges SetInt(string key, long value) => Set(key, "int", value);

        public SaveChanges SetString(string key, string value) => Set(key, "string", value);

        /// <summary>Any value, kept as JSON: a class of yours, a list, a number with a fraction.</summary>
        public SaveChanges SetJson(string key, object value) => Set(key, "json", MgdsJson.From(value));

        public SaveChanges Remove(string key)
        {
            m_Remove.Add(key);
            return this;
        }

        internal bool HasEntries => m_Entries.Count > 0;

        internal JObject EntriesJson => m_Entries;

        internal JObject ToJson()
        {
            var body = new JObject();
            if (Name != null) body["name"] = Name;
            if (Public != null) body["public"] = Public.Value;
            if (m_Entries.Count > 0) body["entries"] = m_Entries;
            if (m_Remove.Count > 0) body["remove"] = new JArray(m_Remove);
            return body;
        }

        private SaveChanges Set(string key, string type, JToken value)
        {
            m_Entries[key] = Entry(type, value);
            return this;
        }

        internal static JObject Entry(string type, JToken value) => new JObject { ["type"] = type, ["value"] = value };
    }

    /// <summary>
    /// Save files: named sets of entries that belong to the signed-in player, on every device, and
    /// that can be shared with other players or made public.
    /// </summary>
    /// <remarks>
    /// The quickest way is <see cref="SaveAsync{T}"/> and <see cref="LoadAsync{T}"/>: one object of
    /// yours per slot. Everything else - several entries, sharing, uploads - is here too.
    /// </remarks>
    public sealed class MgdsSaves
    {
        /// <summary>The entry <see cref="SaveAsync{T}"/> keeps its object under.</summary>
        public const string SlotEntry = "data";

        private readonly MgdsManager m_Manager;
        private readonly Dictionary<string, string> m_Slots = new Dictionary<string, string>();

        internal MgdsSaves(MgdsManager manager)
        {
            m_Manager = manager;
            manager.Auth.SignedOut += m_Slots.Clear;
        }

        // --- the quick way -----------------------------------------------------------------------

        /// <summary>
        /// Saves <paramref name="data"/> in the player's save file called <paramref name="slot"/>,
        /// making the file the first time. Anything Newtonsoft can write as JSON.
        /// </summary>
        public async Task SaveAsync<T>(string slot, T data, CancellationToken cancel = default)
        {
            string id = await SlotIdAsync(slot, cancel);

            if (id == null)
            {
                SaveFile made = await CreateAsync(slot, new SaveChanges().SetJson(SlotEntry, data), cancel: cancel);
                m_Slots[slot] = made.Id;
                return;
            }

            try
            {
                await SetJsonAsync(id, SlotEntry, data, cancel);
            }
            catch (MgdsException e) when (e.IsNotFound)
            {
                // Left on another device: start it again.
                m_Slots.Remove(slot);
                await SaveAsync(slot, data, cancel);
            }
        }

        /// <summary>What <see cref="SaveAsync{T}"/> saved in <paramref name="slot"/>, or <paramref name="fallback"/> when there is nothing yet.</summary>
        public async Task<T> LoadAsync<T>(string slot, T fallback = default, CancellationToken cancel = default)
        {
            string id = await SlotIdAsync(slot, cancel);
            if (id == null)
                return fallback;

            try
            {
                return (await GetAsync(id, cancel)).Get(SlotEntry, fallback);
            }
            catch (MgdsException e) when (e.IsNotFound)
            {
                m_Slots.Remove(slot);
                return fallback;
            }
        }

        /// <summary>Whether the player has a save file called <paramref name="slot"/>.</summary>
        public async Task<bool> HasSlotAsync(string slot, CancellationToken cancel = default) => await SlotIdAsync(slot, cancel) != null;

        /// <summary>Removes the player from the slot's file - deleting it, when nobody else owns it.</summary>
        public async Task DeleteSlotAsync(string slot, CancellationToken cancel = default)
        {
            string id = await SlotIdAsync(slot, cancel);
            if (id != null)
                await LeaveAsync(id, cancel);

            m_Slots.Remove(slot);
        }

        // --- files -------------------------------------------------------------------------------

        /// <summary>Every file the signed-in player owns, or only those called <paramref name="name"/>.</summary>
        public async Task<List<SaveFileInfo>> ListAsync(string name = null, CancellationToken cancel = default)
        {
            string query = name == null ? "" : "?name=" + MgdsHttp.Escape(name);
            MgdsResponse answer = await m_Manager.Http.JsonAsync("GET", "/me/saves" + query, null, Token.Required, cancel);
            return answer.Json["files"]?.ToObject<List<SaveFileInfo>>(MgdsJson.Serializer) ?? new List<SaveFileInfo>();
        }

        /// <summary>Another player's public files (all of them, when it is the signed-in player).</summary>
        public async Task<List<SaveFileInfo>> ListForPlayerAsync(string playerId, CancellationToken cancel = default)
        {
            MgdsResponse answer = await m_Manager.Http.JsonAsync("GET", $"/players/{MgdsHttp.Escape(playerId)}/saves", null, Token.IfSignedIn, cancel);
            return answer.Json["files"]?.ToObject<List<SaveFileInfo>>(MgdsJson.Serializer) ?? new List<SaveFileInfo>();
        }

        /// <summary>
        /// Makes a save file, owned by the signed-in player. Names are letters, digits, <c>.</c>,
        /// <c>-</c> and <c>_</c>, and two files may share one. 409 when the player is at the project's limit.
        /// </summary>
        public async Task<SaveFile> CreateAsync(string name, SaveChanges entries = null, bool isPublic = false, CancellationToken cancel = default)
        {
            var body = new JObject { ["name"] = name, ["public"] = isPublic };
            if (entries != null && entries.HasEntries)
                body["entries"] = entries.EntriesJson;

            return (await m_Manager.Http.JsonAsync("POST", "/me/saves", body, Token.Required, cancel)).As<SaveFile>();
        }

        /// <summary>A whole file. A private one that is not the player's answers 404, like one that does not exist.</summary>
        public async Task<SaveFile> GetAsync(string id, CancellationToken cancel = default) =>
            (await m_Manager.Http.JsonAsync("GET", "/saves/" + MgdsHttp.Escape(id), null, Token.IfSignedIn, cancel)).As<SaveFile>();

        /// <summary>Applies several changes at once - all of them, or none. Returns the file as it now is.</summary>
        public async Task<SaveFile> UpdateAsync(string id, SaveChanges changes, CancellationToken cancel = default) =>
            (await m_Manager.Http.JsonAsync("PUT", "/saves/" + MgdsHttp.Escape(id), changes.ToJson(), Token.Required, cancel)).As<SaveFile>();

        // --- single entries ----------------------------------------------------------------------

        public Task SetIntAsync(string id, string key, long value, CancellationToken cancel = default) =>
            SetAsync(id, key, "int", value, cancel);

        public Task SetStringAsync(string id, string key, string value, CancellationToken cancel = default) =>
            SetAsync(id, key, "string", value, cancel);

        /// <summary>Any value, kept as JSON.</summary>
        public Task SetJsonAsync(string id, string key, object value, CancellationToken cancel = default) =>
            SetAsync(id, key, "json", MgdsJson.From(value), cancel);

        /// <summary>
        /// Changes part of a JSON entry by a JSON Pointer - <c>/inventory/3/count</c> - without
        /// sending the rest. <c>-</c> as the last part appends to an array.
        /// </summary>
        public Task PatchJsonAsync(string id, string key, string path, object value, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("PATCH", Entry(id, key), new JObject { ["path"] = path, ["value"] = MgdsJson.From(value) }, Token.Required, cancel);

        /// <summary>Removes part of a JSON entry by a JSON Pointer.</summary>
        public Task RemoveFromJsonAsync(string id, string key, string path, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("PATCH", Entry(id, key), new JObject { ["path"] = path, ["remove"] = true }, Token.Required, cancel);

        public Task RemoveEntryAsync(string id, string key, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("DELETE", Entry(id, key), null, Token.Required, cancel);

        /// <summary>Uploads a file as an entry, up to 64 MB. Executables are refused (415).</summary>
        public Task UploadAsync(string id, string key, byte[] content, string fileName, string mime = "application/octet-stream",
            CancellationToken cancel = default)
        {
            var form = new List<IMultipartFormSection> { new MultipartFormFileSection("file", content, fileName, mime) };
            return m_Manager.Http.FormAsync("PUT", $"/saves/{MgdsHttp.Escape(id)}/files/{MgdsHttp.Escape(key)}", form, Token.Required, cancel);
        }

        /// <summary>Downloads an uploaded file entry.</summary>
        public async Task<byte[]> DownloadAsync(SaveFileUpload upload, CancellationToken cancel = default) =>
            (await m_Manager.Http.GetBytesAsync(upload.Url, Token.IfSignedIn, cancel)).Bytes;

        // --- owners ------------------------------------------------------------------------------

        public async Task<List<SaveFileOwner>> GetOwnersAsync(string id, CancellationToken cancel = default) =>
            (await m_Manager.Http.JsonAsync("GET", $"/saves/{MgdsHttp.Escape(id)}/owners", null, Token.Required, cancel))
                .Json["owners"]?.ToObject<List<SaveFileOwner>>(MgdsJson.Serializer) ?? new List<SaveFileOwner>();

        /// <summary>Makes another player of this project an owner, with full control.</summary>
        public Task AddOwnerAsync(string id, string playerId, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("PUT", $"/saves/{MgdsHttp.Escape(id)}/owners/{MgdsHttp.Escape(playerId)}", null, Token.Required, cancel);

        public Task RemoveOwnerAsync(string id, string playerId, CancellationToken cancel = default) =>
            m_Manager.Http.JsonAsync("DELETE", $"/saves/{MgdsHttp.Escape(id)}/owners/{MgdsHttp.Escape(playerId)}", null, Token.Required, cancel);

        /// <summary>The signed-in player leaves the file. When the last owner leaves, the file is gone.</summary>
        public Task LeaveAsync(string id, CancellationToken cancel = default)
        {
            foreach (string slot in m_Slots.Where(s => s.Value == id).Select(s => s.Key).ToList())
                m_Slots.Remove(slot);

            return RemoveOwnerAsync(id, m_Manager.Auth.PlayerId ?? "", cancel);
        }

        // -----------------------------------------------------------------------------------------

        private Task SetAsync(string id, string key, string type, JToken value, CancellationToken cancel) =>
            m_Manager.Http.JsonAsync("PUT", Entry(id, key), SaveChanges.Entry(type, value), Token.Required, cancel);

        private static string Entry(string id, string key) => $"/saves/{MgdsHttp.Escape(id)}/entries/{MgdsHttp.Escape(key)}";

        private async Task<string> SlotIdAsync(string slot, CancellationToken cancel)
        {
            if (m_Slots.TryGetValue(slot, out string known))
                return known;

            SaveFileInfo found = (await ListAsync(slot, cancel)).OrderByDescending(f => f.UpdatedAt).FirstOrDefault();
            if (found != null)
                m_Slots[slot] = found.Id;

            return found?.Id;
        }
    }
}
