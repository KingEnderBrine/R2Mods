﻿using BepInEx;
using RoR2;


using R2API;
using R2API.Utils;



/// <summary>
/// Things to do: 
///     Make sure your references are located in a "libs" folder that's sitting next to the project folder.
///         This folder structure was chosen as it was noticed to be one of the more common structures.
///     Add a NuGet Reference to Mono.Cecil. The one included in bepinexpack3.0.0 on thunderstore is the wrong version 0.10.4. You want 0.11.1.
///         You can do this by right clicking your project (not your solution) and going to "Manage NuGet Packages".
///    Make sure the AUTHOR field is correct.
///    Make sure the MODNAME field is correct.
///    Delete this comment!
///    Oh and actually write some stuff.
/// </summary>



namespace NoPrismTrial
{

    [BepInDependency("com.bepis.r2api")]
    [R2APISubmoduleDependency(nameof(CommandHelper))]
    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync,VersionStrictness.DifferentModVersionsAreOk)]
    [BepInPlugin(GUID, MODNAME, VERSION)]
    public sealed class NoPrismTrialPlugin : BaseUnityPlugin
    {
        public const string
            MODNAME = "NoPrismTrial",
            AUTHOR = "Harb",
            GUID = "com." + AUTHOR + "." + MODNAME,
            VERSION = "1.0.0";

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Quality", "IDE0051:Remove unused private members", Justification = "Awake is automatically called by Unity")]
        private void Awake() //Called when loaded by BepInEx.
        {
            CommandHelper.AddToConsoleWhenReady();
        }


        [ConCommand(commandName ="grantPrismUnlocks",flags = ConVarFlags.ExecuteOnServer, helpText = "Unlock the Scythe and Slicing Winds.")]
        public static void CCunlockPrismAchievements(ConCommandArgs _)
        {
            if (LocalUserManager.isAnyUserSignedIn)
            {
                var scythe = UnlockableCatalog.GetUnlockableDef("Items.HealOnCrit");
                var merc = UnlockableCatalog.GetUnlockableDef("Skills.Merc.EvisProjectile");
                LocalUserManager.GetFirstLocalUser().currentNetworkUser.CallCmdReportUnlock(scythe.index);
                LocalUserManager.GetFirstLocalUser().currentNetworkUser.CallCmdReportUnlock(merc.index);
            }
        }
    }
}
