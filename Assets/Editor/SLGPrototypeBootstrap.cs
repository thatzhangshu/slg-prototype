#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace VibeSLG.Editor
{
    [InitializeOnLoad]
    public static class SLGPrototypeBootstrap
    {
        static SLGPrototypeBootstrap()
        {
            EditorApplication.delayCall += EnsurePrototypeScene;
        }

        private static void EnsurePrototypeScene()
        {
            EnsureUrpConfigured();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/SLGPrototype.unity") != null) return;
            CreatePrototypeScene();
        }

        private static void EnsureUrpConfigured()
        {
            if (GraphicsSettings.defaultRenderPipeline != null) return;
            const string folder = "Assets/Settings";
            const string path = folder + "/SLGUniversalRenderPipelineAsset.asset";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "Settings");

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create();
                var serialized = new SerializedObject(pipeline);
                var renderer = serialized.FindProperty("m_RendererDataList").GetArrayElementAtIndex(0).objectReferenceValue;
                AssetDatabase.CreateAsset(pipeline, path);
                AssetDatabase.AddObjectToAsset(renderer, pipeline);
                EditorUtility.SetDirty(pipeline);
                AssetDatabase.SaveAssets();
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            Debug.Log("SLG prototype configured Universal Render Pipeline.");
        }

        [MenuItem("SLG Prototype/Create Prototype Scene")]
        public static void CreatePrototypeScene()
        {
            EnsureUrpConfigured();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var map = new GameObject("SLG Map Prototype");
            var prototype = map.AddComponent<SLGMapPrototype>();

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            var controls = cameraObject.AddComponent<SLGCameraController>();
            cameraObject.transform.position = new Vector3(96f, 45f, 96f);
            cameraObject.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            camera.orthographic = true;
            camera.orthographicSize = 11f;
            camera.backgroundColor = new Color(0.56f, 0.72f, 0.82f);
            camera.farClipPlane = 500f;
            controls.mapBounds = new Vector2(256f, 256f);
            prototype.mapCamera = camera;

            var lightObject = new GameObject("Sun");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.75f);
            RenderSettings.ambientEquatorColor = new Color(0.36f, 0.40f, 0.34f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.18f, 0.15f);

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/SLGPrototype.unity");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/SLGPrototype.unity", true) };
            Selection.activeGameObject = map;
            Debug.Log("SLG prototype scene created at Assets/Scenes/SLGPrototype.unity");
        }

        public static void BuildFromCommandLine()
        {
            CreatePrototypeScene();
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
