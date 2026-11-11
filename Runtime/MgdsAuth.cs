using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DasMaffin.Mgds
{
    /// <summary>The signed-in player, as MGDS describes them.</summary>
    public sealed class MgdsPlayer
    {
        public string PlayerId { get; set; }
        public string DisplayName { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>The ways this player signs in: anonymous, steam, sbox, dasmaffin.</summary>
        public List<string> Logins { get; set; } = new List<string>();

        public DateTime AccessExpiresAt { get; set; }
        public DateTime SessionExpiresAt { get; set; }
    }

    internal sealed class MgdsSession
    {
        public string PlayerId { get; set; }
        public string SessionToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string RefreshToken { get; set; }
        public DateTime RefreshExpiresAt { get; set; }
        public string DisplayName { get; set; }
        public bool Created { get; set; }
        public string DeviceKey { get; set; }
        public string IdToken { get; set; }
    }

    internal sealed class BrowserSignIn
    {
        public string RequestId { get; set; }
        public string PollToken { get; set; }
        public string Url { get; set; }
        public DateTime ExpiresAt { get; set; }
        public float PollEverySeconds { get; set; } = 2;
    }

    /// <summary>
    /// Signing the player in and out. The session is kept on the device and refreshed by itself, so a
    /// player who plays regularly never signs in twice; every other part of the SDK uses it.
    /// </summary>
    public sealed class MgdsAuth
    {
        private const string RefreshKey = "refresh";
        private const string DeviceKey = "device";

        private readonly MgdsManager m_Manager;
        private MgdsSession m_Session;
        private Task m_Refreshing;

        internal MgdsAuth(MgdsManager manager) => m_Manager = manager;

        /// <summary>Raised when a player is signed in: by any way in, or when a session is picked up again.</summary>
        public event Action SignedIn;

        /// <summary>Raised when the player is signed out: by the game, or because the session ended.</summary>
        public event Action SignedOut;

        public bool IsSignedIn => m_Session != null;

        /// <summary>The signed-in player's id, or null.</summary>
        public string PlayerId => m_Session?.PlayerId;

        /// <summary>The signed-in player's name, when they have one.</summary>
        public string DisplayName => m_Session?.DisplayName;

        /// <summary>True when the last sign-in made a new player.</summary>
        public bool IsNewPlayer => m_Session?.Created ?? false;

        /// <summary>
        /// Says who the player is, signed by MGDS, for your own game server to check without asking
        /// MGDS. Renewed with every refresh; lasts an hour.
        /// </summary>
        public string IdToken => m_Session?.IdToken;

        /// <summary>When the session ends unless it is refreshed (the SDK refreshes it by itself while the game runs).</summary>
        public DateTime? SessionExpiresAt => m_Session?.RefreshExpiresAt;

        /// <summary>Whether this device has an anonymous player to come back to.</summary>
        public bool HasDeviceKey => m_Manager.Store.Get(DeviceKey) != null;

        /// <summary>Whether a session from an earlier run is kept on this device.</summary>
        public bool HasStoredSession => m_Manager.Store.Get(RefreshKey) != null;

        internal string SessionToken => m_Session?.SessionToken;

        // --- signing in ---------------------------------------------------------------------------

        /// <summary>
        /// Picks up the session from the last run, if this device kept one. True when it did. Called
        /// by the manager when the game starts, unless that is turned off.
        /// </summary>
        public async Task<bool> RestoreAsync(CancellationToken cancel = default)
        {
            if (IsSignedIn)
                return true;

            string refresh = m_Manager.Store.Get(RefreshKey);
            if (refresh == null)
                return false;

            try
            {
                await RefreshWithAsync(refresh, cancel);
                return true;
            }
            catch (MgdsException e) when (e.IsUnauthorized)
            {
                // That session ended: signed out everywhere, or deleted on the dashboard.
                m_Manager.Store.Set(RefreshKey, null);
                return false;
            }
        }

        /// <summary>
        /// Signs in as this device's anonymous player, made the first time. Signing in anywhere else
        /// with Steam or a dasmaffin.com account after linking it (<see cref="LinkSteamAsync"/>)
        /// leads back to the same player.
        /// </summary>
        /// <exception cref="MgdsException">401 when the device's key is unknown to the project.</exception>
        public async Task SignInAnonymouslyAsync(CancellationToken cancel = default)
        {
            var body = new JObject();
            string key = m_Manager.Store.Get(DeviceKey);
            if (key != null)
                body["deviceKey"] = key;

            MgdsResponse answer = await m_Manager.Http.JsonAsync("POST", "/auth/anonymous", body, Token.None, cancel);
            Accept(answer.As<MgdsSession>());
        }

        /// <summary>
        /// Signs in with a Steam auth ticket for the identity <c>mgds</c>. With Steamworks.NET:
        /// <c>SteamUser.GetAuthTicketForWebApi("mgds")</c>, and the bytes from its
        /// <c>GetTicketForWebApiResponse_t</c> callback. Steam must be added as a provider on the
        /// project's Authenticate page.
        /// </summary>
        public Task SignInWithSteamAsync(byte[] ticket, CancellationToken cancel = default) =>
            SignInWithSteamAsync(Hex(ticket), cancel);

        /// <summary>The same, with the ticket already as hex.</summary>
        public async Task SignInWithSteamAsync(string ticketHex, CancellationToken cancel = default)
        {
            MgdsResponse answer = await m_Manager.Http.JsonAsync("POST", "/auth/steam", new JObject { ["ticket"] = ticketHex }, Token.None, cancel);
            Accept(answer.As<MgdsSession>());
        }

        /// <summary>
        /// Signs in with a dasmaffin.com account: opens the sign-in page in the player's browser and
        /// waits for them to allow it. dasmaffin.com must be added as a provider on the project's
        /// Authenticate page.
        /// </summary>
        /// <param name="openUrl">How to show the page; <see cref="Application.OpenURL"/> when left out.</param>
        public async Task SignInWithDasmaffinAsync(Action<string> openUrl = null, CancellationToken cancel = default)
        {
            JToken session = await BrowserAsync("/auth/dasmaffin", Token.None, openUrl, cancel);
            Accept(MgdsJson.To<MgdsSession>(session));
        }

        // --- the session -------------------------------------------------------------------------

        /// <summary>Gets a new access token now. The SDK does this by itself when one is about to run out.</summary>
        public Task RefreshAsync(CancellationToken cancel = default)
        {
            if (m_Session == null)
                throw new MgdsException(401, "not signed in");

            return RefreshWithAsync(m_Session.RefreshToken, cancel);
        }

        /// <summary>
        /// Signs out: this session, or with <paramref name="everywhere"/> every session the player
        /// has on every device. The device's anonymous player stays, to sign in as again.
        /// </summary>
        public async Task SignOutAsync(bool everywhere = false, CancellationToken cancel = default)
        {
            if (m_Session == null)
                return;

            try
            {
                await m_Manager.Http.JsonAsync("POST", everywhere ? "/auth/logout/all" : "/auth/logout", null, Token.Required, cancel);
            }
            finally
            {
                Forget();
            }
        }

        /// <summary>Who is signed in, and how they sign in, from MGDS.</summary>
        public async Task<MgdsPlayer> GetPlayerAsync(CancellationToken cancel = default) =>
            (await m_Manager.Http.JsonAsync("GET", "/me", null, Token.Required, cancel)).As<MgdsPlayer>();

        // --- more ways in ------------------------------------------------------------------------

        /// <summary>Adds Steam to the signed-in player. Returns their ways in now. 409 if that Steam account is another player's.</summary>
        public async Task<List<string>> LinkSteamAsync(byte[] ticket, CancellationToken cancel = default)
        {
            MgdsResponse answer = await m_Manager.Http.JsonAsync("POST", "/me/logins/steam", new JObject { ["ticket"] = Hex(ticket) }, Token.Required, cancel);
            return Logins(answer.Json);
        }

        /// <summary>Adds a dasmaffin.com account to the signed-in player, through the browser.</summary>
        public async Task<List<string>> LinkDasmaffinAsync(Action<string> openUrl = null, CancellationToken cancel = default) =>
            Logins(await BrowserAsync("/me/logins/dasmaffin", Token.Required, openUrl, cancel));

        /// <summary>Removes a way in: anonymous, steam, sbox or dasmaffin. Never the last one (409).</summary>
        public async Task<List<string>> UnlinkAsync(string provider, CancellationToken cancel = default)
        {
            MgdsResponse answer = await m_Manager.Http.JsonAsync("DELETE", "/me/logins/" + MgdsHttp.Escape(provider), null, Token.Required, cancel);
            return Logins(answer.Json);
        }

        // --- the player's own data ---------------------------------------------------------------

        /// <summary>Everything this project holds about the signed-in player, as one JSON document (GDPR).</summary>
        public async Task<JObject> ExportDataAsync(CancellationToken cancel = default) =>
            (JObject)(await m_Manager.Http.JsonAsync("GET", "/me/export", null, Token.Required, cancel)).Json;

        /// <summary>
        /// Deletes the signed-in player and everything of theirs in this project, for good, and signs
        /// out. Ask the player to confirm first: it cannot be undone.
        /// </summary>
        public async Task DeletePlayerAsync(CancellationToken cancel = default)
        {
            await m_Manager.Http.JsonAsync("DELETE", "/me", null, Token.Required, cancel);
            m_Manager.Store.Set(DeviceKey, null);
            Forget();
        }

        /// <summary>Forgets this device's anonymous player, so the next anonymous sign-in makes a new one. Does not delete anything.</summary>
        public void ForgetDevice() => m_Manager.Store.Set(DeviceKey, null);

        // --- inside the SDK ----------------------------------------------------------------------

        /// <summary>Refreshes first when the access token runs out within a minute.</summary>
        internal Task EnsureFreshAsync(CancellationToken cancel)
        {
            if (m_Session == null || m_Session.ExpiresAt.ToUniversalTime() - DateTime.UtcNow > TimeSpan.FromMinutes(1))
                return Task.CompletedTask;

            return SharedRefreshAsync(cancel);
        }

        /// <summary>After a 401 with <paramref name="rejected"/>: true when there is a working token to try again with.</summary>
        internal async Task<bool> RecoverAsync(string rejected, CancellationToken cancel)
        {
            if (m_Session == null)
                return false;

            // Somebody else refreshed in the meantime.
            if (m_Session.SessionToken != rejected)
                return true;

            try
            {
                await SharedRefreshAsync(cancel);
                return m_Session != null;
            }
            catch (MgdsException e) when (e.IsUnauthorized)
            {
                return false;
            }
        }

        /// <summary>One refresh at a time: a refresh token is good once.</summary>
        private Task SharedRefreshAsync(CancellationToken cancel)
        {
            if (m_Refreshing == null || m_Refreshing.IsCompleted)
                m_Refreshing = RefreshWithAsync(m_Session.RefreshToken, cancel);

            return m_Refreshing;
        }

        private async Task RefreshWithAsync(string refresh, CancellationToken cancel)
        {
            try
            {
                MgdsResponse answer = await m_Manager.Http.JsonAsync("POST", "/auth/refresh", new JObject { ["refreshToken"] = refresh }, Token.None, cancel);
                Accept(answer.As<MgdsSession>());
            }
            catch (MgdsException e) when (e.IsUnauthorized)
            {
                // The session is over: signed out elsewhere, or the refresh token turned up twice.
                Forget();
                throw;
            }
        }

        private void Accept(MgdsSession session)
        {
            bool wasSignedIn = m_Session != null && m_Session.PlayerId == session.PlayerId;
            m_Session = session;

            if (!string.IsNullOrEmpty(session.DeviceKey))
                m_Manager.Store.Set(DeviceKey, session.DeviceKey);

            m_Manager.Store.Set(RefreshKey, session.RefreshToken);

            if (!wasSignedIn)
                SignedIn?.Invoke();
        }

        private void Forget()
        {
            bool was = m_Session != null;
            m_Session = null;
            m_Manager.Store.Set(RefreshKey, null);

            if (was)
                SignedOut?.Invoke();
        }

        /// <summary>The browser flow: start, open the page, poll until they allow it. Returns the final answer.</summary>
        private async Task<JToken> BrowserAsync(string path, Token token, Action<string> openUrl, CancellationToken cancel)
        {
            BrowserSignIn started = (await m_Manager.Http.JsonAsync("POST", path, new JObject(), token, cancel)).As<BrowserSignIn>();
            (openUrl ?? Application.OpenURL)(started.Url);

            var poll = new JObject { ["requestId"] = started.RequestId, ["pollToken"] = started.PollToken };
            float every = Mathf.Max(1, started.PollEverySeconds);

            while (true)
            {
                await m_Manager.DelayAsync(every, cancel);

                MgdsResponse answer = await m_Manager.Http.JsonAsync("POST", path + "/poll", poll, token, cancel, 202);
                if (answer.Status != 202)
                    return answer.Json;
            }
        }

        private static List<string> Logins(JToken answer) =>
            answer?["logins"]?.ToObject<List<string>>() ?? new List<string>();

        private static string Hex(byte[] bytes)
        {
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                hex.Append(b.ToString("x2"));

            return hex.ToString();
        }
    }
}
