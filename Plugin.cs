using BepInEx;

namespace ChqserMedia
{
    [System.ComponentModel.Description("Chqser Media, AssetBundle Template")]
    [BepInPlugin("com.Chqser.Media.lyfe", "Chqser Media", "1.0.0")]
    public class HarmonyPatches : BaseUnityPlugin
    {
        private void Awake()
        {
            GorillaTagger.OnPlayerSpawned(OnPlayerSpawned);
            gameObject.AddComponent<Menu>();
            gameObject.AddComponent<AudioManagement>();
            gameObject.AddComponent<ChqserNetwork>();
        }

        public void OnPlayerSpawned() =>
            Patches.PatchHandler.PatchAll();
    }
}
