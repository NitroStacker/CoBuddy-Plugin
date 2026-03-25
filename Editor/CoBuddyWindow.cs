using UnityEditor;
using UnityEngine;

namespace CoBuddy.Editor
{
    /// <summary>
    /// Minimal CoBuddy status window. Full UI lives in the CoBuddy desktop app.
    /// Ableton-style accent: #ffc857.
    /// </summary>
    public class CoBuddyWindow : EditorWindow
    {
        private static readonly Color AccentColor = new Color(1f, 0.784f, 0.341f); // #ffc857

        [MenuItem("Window/CoBuddy")]
        public static void ShowWindow()
        {
            var window = GetWindow<CoBuddyWindow>("CoBuddy");
            window.minSize = new Vector2(280, 120);
        }

        private void OnGUI()
        {
            // Accent left border (Ableton-style)
            EditorGUI.DrawRect(new Rect(0, 0, 3, position.height), AccentColor);

            GUILayout.BeginHorizontal();
            GUILayout.Space(12); // Offset from accent border
            GUILayout.BeginVertical();
            GUILayout.Space(8);
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = AccentColor } };
            EditorGUILayout.LabelField("CoBuddy by OpenGate", headerStyle);
            EditorGUILayout.LabelField("Unity AI Assistant", EditorStyles.miniLabel);
            GUILayout.Space(6);

            // Status strip
            var projectName = System.IO.Path.GetFileName(System.IO.Path.GetFullPath(".")) ?? "—";
            EditorGUILayout.LabelField("CoBuddy · Port 38472 · " + projectName, EditorStyles.miniLabel);
            GUILayout.Space(6);

            EditorGUILayout.LabelField("Status", "Bridge running on port 38472");
            EditorGUILayout.LabelField("Project", projectName);
            GUILayout.Space(8);

            // Device-panel style help box (accent border)
            var helpRect = GUILayoutUtility.GetRect(10, 44, GUILayout.ExpandWidth(true));
            var borderRect = new Rect(helpRect.x, helpRect.y, 3, helpRect.height);
            EditorGUI.DrawRect(borderRect, AccentColor);
            var bgRect = new Rect(helpRect.x + 3, helpRect.y, Mathf.Max(0, helpRect.width - 3), helpRect.height);
            EditorGUI.DrawRect(bgRect, new Color(0.15f, 0.15f, 0.15f, 0.95f));
            var textRect = new Rect(helpRect.x + 12, helpRect.y + 6, Mathf.Max(0, helpRect.width - 18), helpRect.height - 12);
            GUI.Label(textRect, "Connect the CoBuddy desktop app to this project. Select your Unity project in the app and ensure both are running.", EditorStyles.wordWrappedLabel);

            GUILayout.Space(8);
            using (new EditorGUI.DisabledScope(false))
            {
                if (GUILayout.Button("Open CoBuddy Documentation"))
                {
                    Application.OpenURL("https://cobuddydev.com/docs/welcome");
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
    }
}
