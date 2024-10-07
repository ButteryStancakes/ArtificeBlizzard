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
using BepInEx.Configuration;

namespace ArtificeBlizzard
{
    enum SnowMode
    {
        Always,
        RandomWhenClear,
        RandomAnyWeather
    }

    enum FogColor
    {
        Neutral,
        Bluish,
        BrightBlue
    }

    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.artificeblizzard", PLUGIN_NAME = "Artifice Blizzard", PLUGIN_VERSION = "1.0.3";
        internal static new ManualLogSource Logger;
        internal static ConfigEntry<bool> configDaytimeSpawns, configAlwaysOverrideSpawns;
        internal static ConfigEntry<int> configBaboonWeight;
        internal static ConfigEntry<float> configFogDistance, configSnowyChance;
        internal static ConfigEntry<SnowMode> configSnowMode;
        internal static ConfigEntry<FogColor> configFogColor;

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
                1,
                new ConfigDescription("(Only affects your hosted games) Spawn weight for baboon hawks. Vanilla is 7.\nFor comparison, Old Birds have 45, forest keepers have 23, eyeless dogs have 19, and earth leviathans have 6.",
                    new AcceptableValueRange<int>(0, 100)
                ));

            configAlwaysOverrideSpawns = Config.Bind(
                "Spawning",
                "AlwaysOverrideSpawns",
                false,
                "(Only affects your hosted games) Determines when \"DaytimeSpawns\" and \"BaboonWeight\" are applied.\nThe default setting (false) will only override vanilla spawns when the blizzard is active.");

            configFogColor = Config.Bind(
                "Visuals",
                "FogColor",
                FogColor.Neutral,
                "Changes the color of the snowstorm.\n\"Neutral\" matches Rend and Dine, \"Bluish\" adds a slight blue tint, and \"BrightBlue\" is the original color used in earlier versions of this mod.");

            configFogDistance = Config.Bind(
                "Visuals",
                "FogDistance",
                8f,
                new ConfigDescription("Controls level of visibility in the snowstorm. (Lower value means denser fog)\nFor comparison, Rend uses 3.7, Titan uses 5.0, and Dine uses 8.0. Artifice uses 25.0 in vanilla.",
                    new AcceptableValueRange<float>(2.4f, 25f)
                ));

            configSnowMode = Config.Bind(
                "Random",
                "SnowMode",
                SnowMode.Always,
                "When should Artifice be snowy?\n\"Always\" makes Artifice permanently snowy. \"RandomWhenClear\" rolls a random chance to replace mild weather with the blizzard. \"RandomAnyWeather\" will still randomize but allow the blizzard to stack with other weather types.");

            configSnowyChance = Config.Bind(
                "Random",
                "SnowyChance",
                0.25f,
                new ConfigDescription("The specific chance that \"SnowMode\" will activate the blizzard.\n(0 = never, 1 = guaranteed, 0.5 = 50% chance, or anything in between)",
                    new AcceptableValueRange<float>(0f, 1f)
                ));

            Config.Bind("Random", "AlwaysOverrideSpawns", false, "Legacy setting, moved to \"Spawning\" section");
            Config.Remove(Config["Random", "AlwaysOverrideSpawns"].Definition);

            new Harmony(PLUGIN_GUID).PatchAll();

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
            Plugin.Logger.LogInfo("Enabled snow footprint caching on Artifice");
        }

        [HarmonyPatch(typeof(RoundManager), "SetToCurrentLevelWeather")]
        [HarmonyPostfix]
        static void RoundManagerPostSetToCurrentLevelWeather(RoundManager __instance)
        {
            if (__instance.currentLevel.name == "ArtificeLevel")
                ArtificeSceneTransformer.RandomizeSnowyWeather();
        }
    }

    class ArtificeSceneTransformer
    {
        public static bool snowy;

        internal static void RandomizeSnowyWeather()
        {
            snowy = Plugin.configSnowMode.Value == SnowMode.Always || ((
                (Plugin.configSnowMode.Value == SnowMode.RandomWhenClear && TimeOfDay.Instance.currentLevelWeather < LevelWeatherType.Rainy)
                || Plugin.configSnowMode.Value == SnowMode.RandomAnyWeather)
                && new System.Random(StartOfRound.Instance.randomMapSeed).NextDouble() <= (double)Plugin.configSnowyChance.Value);

            if (RoundManager.Instance.IsServer)
                AdjustArtificeSpawns();

            if (snowy)
                TransformArtificeScene();
        }

        static void AdjustArtificeSpawns()
        {
            bool apply = snowy || Plugin.configAlwaysOverrideSpawns.Value;
            if (!Plugin.configDaytimeSpawns.Value && apply)
            {
                StartOfRound.Instance.currentLevel.maxDaytimeEnemyPowerCount = 0;
                Plugin.Logger.LogInfo("Disable daytime spawns for Artifice");
            }
            else
            {
                StartOfRound.Instance.currentLevel.maxDaytimeEnemyPowerCount = 20;
                Plugin.Logger.LogInfo("Daytime spawns are active on Artifice");
            }
            SpawnableEnemyWithRarity baboons = StartOfRound.Instance.currentLevel.OutsideEnemies.FirstOrDefault(spawnableEnemyWithRarity => spawnableEnemyWithRarity.enemyType.name == "BaboonHawk");
            if (baboons != null)
                baboons.rarity = apply ? Plugin.configBaboonWeight.Value : 7;
            Plugin.Logger.LogInfo("Overridden baboon spawn weight for Artifice");
        }

        static void TransformArtificeScene()
        {
            Transform environment = GameObject.Find("/Environment").transform;
            Transform tree003 = environment.Find("tree.003_LOD0");

            // Imperium can cause this function to occur during orbit phase, when the Artifice scene is unloaded...
            if (tree003 == null)
            {
                Plugin.Logger.LogWarning("TransformArtificeScene() called, but \"Level9Artifice\" doesn't seem to be loaded");
                return;
            }

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

            Transform brightDay = environment.Find("Lighting/BrightDay");
            Transform blizzardSunAnimContainer = brightDay.Find("Sun/BlizzardSunAnimContainer");

            Plugin.Logger.LogInfo("Setup \"snowstorm\" fog");
            LocalVolumetricFog localVolumetricFog = brightDay.Find("Local Volumetric Fog (1)").GetComponent<LocalVolumetricFog>();
            localVolumetricFog.parameters.meanFreePath = Plugin.configFogDistance.Value;
            if (Plugin.configFogColor.Value == FogColor.BrightBlue)
                localVolumetricFog.parameters.albedo = new Color(0.8254717f, 0.9147653f, 1f);
            else if (Plugin.configFogColor.Value == FogColor.Bluish)
                localVolumetricFog.parameters.albedo = new Color(0.55291027249f, 0.61272013478f, 0.6698113f);

            Plugin.Logger.LogInfo("Override global volume");
            Volume skyAndFogGlobalVolume = blizzardSunAnimContainer.Find("Sky and Fog Global Volume").GetComponent<Volume>();
            skyAndFogGlobalVolume.profile = artificeBlizzardAssets.LoadAsset<VolumeProfile>("SnowyFog");

            Plugin.Logger.LogInfo("Override time-of-day animations");
            Animator blizzardSunAnim = blizzardSunAnimContainer.GetComponent<Animator>();
            AnimatorOverrideController animatorOverrideController = new(blizzardSunAnim.runtimeAnimatorController);
            List<KeyValuePair<AnimationClip, AnimationClip>> overrides = [];
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