using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DasMaffin.Mgds
{
    /// <summary>What the manager does about signing in when the game starts.</summary>
    public enum MgdsAutoSignIn
    {
        /// <summary>Nothing: the game signs the player in itself.</summary>
        None,

        /// <summary>Picks up the session from the last time the game ran, if there is one.</summary>
        RestoreOnly,

        /// <summary>Picks up the last session, or signs in anonymously with this device. The usual choice.</summary>
        RestoreOrAnonymous,
    }

    /// <summary>
    /// The one MGDS object in a game. Put it in your first scene, fill in the project id, and use it
    /// from anywhere as <c>MgdsManager.Instance</c> (or the shorter <see cref="Mgds"/>): its
    /// <see cref="Auth"/>, <see cref="Saves"/>, <see cref="Leaderboards"/>, <see cref="Config"/>,
    /// <see cref="Reports"/> and <see cref="Push"/> do the rest.
    /// </summary>
    /// <remarks>
    /// It survives scene loads. A second manager - one in a scene loaded later, say - removes itself
    /// and the first one carries on, so every scene can have one for testing on its own.
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    [AddComponentMenu("MGDS/MGDS Manager")]
    public sealed class MgdsManager : MonoBehaviour
    {
        public const string DefaultServer = "https://www.dasmaffin.com";

        [Header("Project")]
        [Tooltip("The project's id, from its Configure page on dasmaffin.com/Mgds.")]
        [SerializeField] private string projectId = "";

        [Tooltip("The remote config environment for config and push messages. Empty for the project's default one.")]
        [SerializeField] private string environment = "";

        [Header("When the game starts")]
        [Tooltip("Whether the manager signs the player in by itself.")]
        [SerializeField] private MgdsAutoSignIn autoSignIn = MgdsAutoSignIn.RestoreOrAnonymous;

        [Tooltip("Fetches remote config once signing in is done. Needs Remote config activated on the project.")]
        [SerializeField] private bool fetchConfigOnStart = false;

        [Tooltip("Connects to push messages once signing in is done. Needs Push messages activated on the project.")]
        [SerializeField] private bool connectPushOnStart = false;

        [Tooltip("Channels to listen on when push connects by itself: a region, a guild, a match.")]
        [SerializeField] private string[] pushChannels = Array.Empty<string>();

        [Header("Reports")]
        [Tooltip("Keeps the game's latest log lines, so a report can carry them.")]
        [SerializeField] private bool collectLogs = true;

        [Tooltip("How many log lines are kept for a report.")]
        [SerializeField] private int keepLogLines = 500;

        [Header("Advanced")]
        [Tooltip("Where MGDS is. Leave it unless you were told otherwise.")]
        [SerializeField] private string serverUrl = DefaultServer;

        [Tooltip("Writes every request to the console.")]
        [SerializeField] private bool logRequests;

        private readonly TaskCompletionSource<bool> m_Ready = new TaskCompletionSource<bool>();
        private readonly CancellationTokenSource m_Stopping = new CancellationTokenSource();

        /// <summary>The manager, once its scene has loaded. Null before that, or when there is none.</summary>
        public static MgdsManager Instance { get; private set; }

        /// <summary>Whether there is a manager to use.</summary>
        public static bool Exists => Instance != null;

        public string ProjectId => projectId?.Trim() ?? "";

        /// <summary>The remote config environment, or empty for the default one.</summary>
        public string Environment => environment?.Trim() ?? "";

        public string ServerUrl => string.IsNullOrWhiteSpace(serverUrl) ? DefaultServer : serverUrl.Trim().TrimEnd('/');

        public bool LogRequests { get => logRequests; set => logRequests = value; }

        /// <summary>Signing in, out, linking accounts, and the player's own data.</summary>
        public MgdsAuth Auth { get; private set; }

        /// <summary>The signed-in player's save files.</summary>
        public MgdsSaves Saves { get; private set; }

        public MgdsLeaderboards Leaderboards { get; private set; }

        /// <summary>Remote config: values the game fetches, changed on the dashboard.</summary>
        public MgdsRemoteConfig Config { get; private set; }

        /// <summary>Bug reports and ideas from inside the game, and the answers to them.</summary>
        public MgdsReports Reports { get; private set; }

        /// <summary>Push messages: methods the dashboard makes every running game call.</summary>
        public MgdsPush Push { get; private set; }

        /// <summary>True once the start-up work - signing in, config, push - is done, whether or not it worked.</summary>
        public bool IsReady => m_Ready.Task.IsCompleted;

        /// <summary>Completes when the start-up work is done. <c>await Mgds.WhenReady;</c> before the first call that needs a player.</summary>
        public Task WhenReady => m_Ready.Task;

        /// <summary>Raised once the start-up work is done.</summary>
        public event Action Ready;

        internal MgdsHttp Http { get; private set; }

        internal MgdsStore Store { get; private set; }

        internal MgdsLogBuffer Logs { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // The first one carries on: it has the session.
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // DontDestroyOnLoad only works on a root object.
            if (transform.parent != null)
                transform.SetParent(null, false);

            DontDestroyOnLoad(gameObject);

            Store = new MgdsStore(ProjectId);
            Http = new MgdsHttp(this);
            Logs = new MgdsLogBuffer(collectLogs ? Mathf.Max(10, keepLogLines) : 0);

            Auth = new MgdsAuth(this);
            Saves = new MgdsSaves(this);
            Leaderboards = new MgdsLeaderboards(this);
            Config = new MgdsRemoteConfig(this);
            Reports = new MgdsReports(this);
            Push = new MgdsPush(this);

            if (collectLogs)
                Application.logMessageReceived += Logs.Add;
        }

        private async void Start()
        {
            if (Instance != this)
                return;

            try
            {
                if (!Guid.TryParse(ProjectId, out _))
                {
                    Debug.LogError("[MGDS] The MGDS Manager has no project id. Copy it from your project's Configure page on dasmaffin.com/Mgds.", this);
                    return;
                }

                CancellationToken stop = Stopping;

                if (autoSignIn != MgdsAutoSignIn.None)
                {
                    try
                    {
                        bool restored = await Auth.RestoreAsync(stop);
                        if (!restored && autoSignIn == MgdsAutoSignIn.RestoreOrAnonymous)
                            await Auth.SignInAnonymouslyAsync(stop);
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        Debug.LogWarning("[MGDS] Signing in when the game started did not work: " + e.Message, this);
                    }
                }

                if (fetchConfigOnStart)
                {
                    try
                    {
                        await Config.FetchAsync(cancel: stop);
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        Debug.LogWarning("[MGDS] Fetching remote config did not work: " + e.Message, this);
                    }
                }

                if (connectPushOnStart)
                    Push.Connect(pushChannels);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                m_Ready.TrySetResult(true);
                Ready?.Invoke();
            }
        }

        private void OnDestroy()
        {
            if (Instance != this)
                return;

            Application.logMessageReceived -= Logs.Add;
            Push?.Disconnect();
            m_Stopping.Cancel();
            Instance = null;
        }

        /// <summary>Waits, on the game's own clock, without threads: works on WebGL too.</summary>
        internal Task DelayAsync(float seconds, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<bool>();

            if (cancel.IsCancellationRequested)
            {
                done.TrySetCanceled();
                return done.Task;
            }

            StartCoroutine(Wait());
            return done.Task;

            IEnumerator Wait()
            {
                float until = Time.realtimeSinceStartup + seconds;

                while (Time.realtimeSinceStartup < until)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        done.TrySetCanceled();
                        yield break;
                    }

                    yield return null;
                }

                done.TrySetResult(true);
            }
        }

        /// <summary>Runs a coroutine and completes when it does.</summary>
        internal Task RunAsync(IEnumerator routine)
        {
            var done = new TaskCompletionSource<bool>();
            StartCoroutine(Run());
            return done.Task;

            IEnumerator Run()
            {
                yield return routine;
                done.TrySetResult(true);
            }
        }

        internal static MgdsManager Require()
        {
            if (Instance == null)
                throw new InvalidOperationException("There is no MGDS Manager. Add one to your first scene (GameObject > MGDS > MGDS Manager).");

            return Instance;
        }

        internal IReadOnlyList<string> PushChannels => pushChannels;

        /// <summary>Cancelled when the manager goes away: with the game, normally.</summary>
        internal CancellationToken Stopping => m_Stopping.Token;
    }
}
