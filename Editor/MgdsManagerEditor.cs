using System;
using UnityEditor;
using UnityEngine;

namespace DasMaffin.Mgds.Editor
{
    [CustomEditor(typeof(MgdsManager))]
    public sealed class MgdsManagerEditor : UnityEditor.Editor
    {
        private const string Docs = "https://www.dasmaffin.com/Mgds/Docs/sdk/unity";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            string project = serializedObject.FindProperty("projectId").stringValue?.Trim() ?? "";
            string server = serializedObject.FindProperty("serverUrl").stringValue?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(server))
                server = MgdsManager.DefaultServer;

            if (project.Length == 0)
                EditorGUILayout.HelpBox("Paste the project id from your project's Configure page on dasmaffin.com/Mgds.", MessageType.Warning);
            else if (!Guid.TryParse(project, out _))
                EditorGUILayout.HelpBox("That is not a project id: it looks like 6f0c1c9e-0b5e-4c8f-9a0e-3f1d2c4b5a69.", MessageType.Error);

            DrawDefaultInspector();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!Guid.TryParse(project, out _)))
                {
                    if (GUILayout.Button("Open the project's dashboard"))
                        Application.OpenURL($"{server}/Mgds/{project}/Configure");
                }

                if (GUILayout.Button("Documentation"))
                    Application.OpenURL(Docs);
            }

            if (Application.isPlaying && target is MgdsManager manager && manager.Auth != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("While playing", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Ready", manager.IsReady ? "yes" : "not yet");
                EditorGUILayout.LabelField("Player", manager.Auth.IsSignedIn ? $"{manager.Auth.DisplayName ?? "(no name)"}  {manager.Auth.PlayerId}" : "nobody signed in");
                EditorGUILayout.LabelField("Remote config", manager.Config.IsFetched ? $"{manager.Config.Values.Count} values ({manager.Config.Environment})" : "not fetched");
                EditorGUILayout.LabelField("Push", manager.Push.ConnectionId != null ? "connected" : manager.Push.IsListening ? "connecting" : "off");
                Repaint();
            }

            serializedObject.ApplyModifiedProperties();
        }

        [MenuItem("GameObject/MGDS/MGDS Manager", false, 10)]
        private static void Create(MenuCommand command)
        {
            MgdsManager existing = UnityEngine.Object.FindObjectOfType<MgdsManager>();
            if (existing != null)
            {
                Selection.activeObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                Debug.Log("[MGDS] This scene already has an MGDS Manager.", existing);
                return;
            }

            var created = new GameObject("MGDS Manager", typeof(MgdsManager));
            Undo.RegisterCreatedObjectUndo(created, "Create MGDS Manager");
            Selection.activeObject = created;
        }
    }
}
