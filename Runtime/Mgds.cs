using System.Threading.Tasks;

namespace DasMaffin.Mgds
{
    /// <summary>
    /// A shorter way to the <see cref="MgdsManager"/>: <c>Mgds.Saves</c> is
    /// <c>MgdsManager.Instance.Saves</c>. Throws a clear error when there is no manager in the scene.
    /// </summary>
    public static class Mgds
    {
        public static MgdsManager Manager => MgdsManager.Require();

        public static MgdsAuth Auth => Manager.Auth;

        public static MgdsSaves Saves => Manager.Saves;

        public static MgdsLeaderboards Leaderboards => Manager.Leaderboards;

        public static MgdsRemoteConfig Config => Manager.Config;

        public static MgdsReports Reports => Manager.Reports;

        public static MgdsPush Push => Manager.Push;

        /// <summary>Completes once the manager's start-up work - signing in, config, push - is done.</summary>
        public static Task WhenReady => Manager.WhenReady;
    }
}
