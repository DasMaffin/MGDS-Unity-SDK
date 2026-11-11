# MGDS for Unity

The Unity SDK for **MGDS - Maffin's Gamedev Services**: sign players in, save files, leaderboards,
remote config, reports and push messages, from one manager in your first scene.

```csharp
await Mgds.WhenReady;                                   // signed in by itself
await Mgds.Saves.SaveAsync("slot1", progress);
await Mgds.Leaderboards.SubmitScoreAsync("high", 1234);
float speed = Mgds.Config.GetFloat("speed", 1f);
Mgds.Push.On("MaintenanceWarning", m => banner.Show(m.Get<string>("message")));
```

**Documentation: [dasmaffin.com/Mgds/Docs/unity](https://www.dasmaffin.com/Mgds/Docs/unity)**

## Install

Window > Package Manager > **+** > Add package from git URL:

```
https://github.com/DasMaffin/MGDS-Unity-SDK.git
```

Then **GameObject > MGDS > MGDS Manager** in your first scene, and paste your project's id from
[dasmaffin.com/Mgds](https://www.dasmaffin.com/Mgds).

Unity 2021.3 or newer, every platform including WebGL. Depends on `com.unity.nuget.newtonsoft-json`.
