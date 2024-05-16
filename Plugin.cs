using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using BepInEx.Configuration;

namespace ArtificeBlizzard
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.artificeblizzard", PLUGIN_NAME = "Artifice Blizzard", PLUGIN_VERSION = "0.0.0";
        internal static new ManualLogSource Logger;
        internal static ConfigEntry<bool> configDaytimeSpawns;
        internal static ConfigEntry<int> configBaboonWeight;
        internal static ConfigEntry<float> configFogDistance;

        void Awake()
        {
            Logger = base.Logger;

            configDaytimeSpawns = Config.Bind(
                "Spawning",
                "DaytimeSpawns",
                false,
                "(Only affects your hosted games) Should daytime enemies spawn? (Manticoils, circuit bees, tulip snakes)");

            configBaboonWeight = Config.Bind(
                "Spawning",
                "BaboonWeight",
                0,
                new ConfigDescription("(Only affects your hosted games) Spawn weight for baboon hawks. Vanilla is 7.\nFor comparison, Old Birds have 45, forest keepers have 23, eyeless dogs have 19, and earth leviathans have 6.",
                    new AcceptableValueRange<int>(0, 100)
                ));

            configFogDistance = Config.Bind(
                "Visuals",
                "FogDistance",
                3.3f,
                new ConfigDescription("Controls how \"thick\" the snowstorm is. (Lower value means denser fog)\nFor comparison, Rend uses 3.7, Titan uses 5, and Dine uses 8. Artifice uses 25 in vanilla.",
                    new AcceptableValueRange<float>(2.4f, 25f)
                ));

            new Harmony(PLUGIN_GUID).PatchAll();

            SceneManager.sceneLoaded += ArtificeSceneTransformer.OnSceneLoaded;

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class ArtificeBlizzardPatches
    {
        [HarmonyPatch(typeof(StartOfRound), "Awake")]
        [HarmonyPostfix]
        static void StartOfRoundPostAwake(StartOfRound __instance)
        {
            SelectableLevel artifice = __instance.levels.FirstOrDefault(level => level.name == "ArtificeLevel");
            artifice.levelIncludesSnowFootprints = true;
            Plugin.Logger.LogInfo("Enabled snow footprints on Artifice");
            if (Plugin.configDaytimeSpawns.Value)
            {
                artifice.maxDaytimeEnemyPowerCount = 20;
                Plugin.Logger.LogInfo("Daytime spawns are active on Artifice");
            }
            else
            {
                artifice.maxDaytimeEnemyPowerCount = 0;
                Plugin.Logger.LogInfo("Disable daytime spawns for Artifice");
            }
            artifice.OutsideEnemies.FirstOrDefault(spawnableEnemyWithRarity => spawnableEnemyWithRarity.enemyType.name == "BaboonHawk").rarity = Plugin.configBaboonWeight.Value;
            Plugin.Logger.LogInfo("Overridden baboon spawn weight for Artifice");
        }
    }

    class ArtificeSceneTransformer
    {
        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Level9Artifice")
                return;

            AssetBundle artificeBlizzardAssets;
            try
            {
                artificeBlizzardAssets = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "artificeblizzard"));
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError("Failed to load replacement assets - have you installed all the files properly?");
                Plugin.Logger.LogError(e.Message);
                return;
            }

            Transform environment = GameObject.Find("/Environment").transform;
            Transform tree003 = environment.Find("tree.003_LOD0");
            Transform brightDay = environment.Find("Lighting/BrightDay");
            Transform blizzardSunAnimContainer = brightDay.Find("Sun/BlizzardSunAnimContainer");

            Plugin.Logger.LogInfo("Setup \"snowstorm\" fog");
            LocalVolumetricFog localVolumetricFog = brightDay.Find("Local Volumetric Fog (1)").GetComponent<LocalVolumetricFog>();
            localVolumetricFog.parameters.meanFreePath = Plugin.configFogDistance.Value;
            localVolumetricFog.parameters.albedo = new Color(0.8254717f, 0.9147653f, 1f);

            Plugin.Logger.LogInfo("Override global volume");
            Volume skyAndFogGlobalVolume = blizzardSunAnimContainer.Find("Sky and Fog Global Volume").GetComponent<Volume>();
            skyAndFogGlobalVolume.profile = artificeBlizzardAssets.LoadAsset<VolumeProfile>("SnowyFog");

            Plugin.Logger.LogInfo("Override time-of-day animations");
            Animator blizzardSunAnim = blizzardSunAnimContainer.GetComponent<Animator>();
            AnimatorOverrideController animatorOverrideController = new(blizzardSunAnim.runtimeAnimatorController);
            List<KeyValuePair<AnimationClip, AnimationClip>> overrides = new();
            foreach (AnimationClip clip in blizzardSunAnim.runtimeAnimatorController.animationClips)
                overrides.Add(new KeyValuePair<AnimationClip, AnimationClip>(clip, artificeBlizzardAssets.LoadAsset<AnimationClip>(clip.name.Replace("Sun", "SunTypeC"))));
            animatorOverrideController.ApplyOverrides(overrides);
            blizzardSunAnim.runtimeAnimatorController = animatorOverrideController;

            Plugin.Logger.LogInfo("Repaint terrain");
            Transform artificeTerrainCutDown = environment.Find("ArtificeTerrainCutDown");
            artificeTerrainCutDown.GetComponent<Renderer>().material = artificeBlizzardAssets.LoadAsset<Material>("ArtificeTerrainSplatmapLit");
            artificeTerrainCutDown.tag = "Snow";

            Plugin.Logger.LogInfo("Change OOB material");
            GameObject outOfBoundsTerrain = GameObject.Find("/OutOfBoundsTerrain");
            Material snowMatTiled = artificeBlizzardAssets.LoadAsset<Material>("SnowMatTiled");
            foreach (Renderer rend in outOfBoundsTerrain.GetComponentsInChildren<Renderer>())
            {
                rend.material = snowMatTiled;
                rend.tag = "Snow";
            }

            Plugin.Logger.LogInfo("Change tree/foliage material");
            Transform trees = environment.Find("Map/Trees");
            List<Renderer> rends = new(trees.GetComponentsInChildren<Renderer>())
            {
                tree003.GetComponent<Renderer>(),
                tree003.Find("tree.003_LOD1").GetComponent<Renderer>()
            };
            Material forestTextureSnowy = artificeBlizzardAssets.LoadAsset<Material>("ForestTextureSnowy");
            foreach (Renderer rend in rends)
            {
                if (rend.sharedMaterial.name.StartsWith("ForestTexture"))
                    rend.material = forestTextureSnowy;
            }
            tree003.Find("tree.003_LOD2").GetComponent<Renderer>().material = artificeBlizzardAssets.LoadAsset<Material>("TreeFlatLeafless1");

            Plugin.Logger.LogInfo("Hide Mimcket");
            environment.Find("Mimcket").gameObject.SetActive(false);
            tree003.Find("CricketAudio").gameObject.SetActive(false);

            Plugin.Logger.LogInfo("Enable blizzard audio");
            Transform audio = GameObject.Find("/Systems/Audio").transform;
            audio.Find("BlizzardAmbience").gameObject.SetActive(true);
            foreach (Transform blizzardAudio in audio.Find("3DBlizzardAudios"))
                blizzardAudio.gameObject.SetActive(true);

            artificeBlizzardAssets.Unload(false);
        }
    }
}