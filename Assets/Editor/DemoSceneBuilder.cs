using System.IO;
using RedDot.Demo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RedDot.EditorTools
{
    /// <summary>
    /// Generates <c>Assets/Scenes/RedDotDemo.unity</c>.
    /// </summary>
    /// <remarks>
    /// The demo scene holds three objects and no authored content, so keeping it as a
    /// script rather than a hand-edited YAML asset means it can be regenerated after any
    /// change to the bootstrap, and reviewed as a diff that says what it does.
    /// </remarks>
    public static class DemoSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/RedDotDemo.unity";

        [MenuItem("RedDot/Rebuild demo scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0x12, 0x14, 0x18, 0xFF);
            camera.orthographic = true;
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);

            var demoObject = new GameObject("RedDotDemo");
            demoObject.AddComponent<DemoMain>();
            demoObject.AddComponent<RedDotDriver>();

            var directory = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);

            // The PlayMode smoke test loads this by name, and it is the scene a build
            // should start on.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            AssetDatabase.Refresh();

            Debug.Log("[RedDot] wrote " + ScenePath);
        }
    }
}
