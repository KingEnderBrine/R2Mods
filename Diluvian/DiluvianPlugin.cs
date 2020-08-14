using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using R2API;
using R2API.Utils;
using RoR2;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using EliteDef = RoR2.CombatDirector.EliteTierDef;

namespace Diluvian
{

    [R2APISubmoduleDependency("DifficultyAPI", "ResourcesAPI", "LanguageAPI")]
    //DifficultyAPI: I am adding a difficulty.
    //ResourcesAPI:  I am loading in a custom icon.
    //Assetplus:     For the massive amounts of text replacement.

    [BepInDependency(R2API.R2API.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    //I need R2API.
    [BepInDependency("com.jarlyk.eso", BepInDependency.DependencyFlags.SoftDependency)]
    //ESO does stuff with elites.
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Diluvian : BaseUnityPlugin
    {
        public const string
            NAME = "Diluvian",
            GUID = "com.harbingerofme." + NAME,
            VERSION = "1.1.0";

        //Defining The difficultyDef of Diluvian.
        private readonly Color DiluvianColor = new Color(0.61f, 0.07f, 0.93f);
        private readonly float DiluvianCoef = 3.5f;
        private readonly RoR2.DifficultyDef DiluvianDef;
        private DifficultyIndex DiluvianIndex;

        //DiluvianModifiers:
        private const float EliteModifier = 0.8f;
        private const float HealthRegenMultiplier = -0.6f;
        private const float MonsterRegen = 0.015f;
        private const float NewOSPTreshold = 0.99f;

        //ResourceAPI stuff.
        private const string assetPrefix = "@HarbDiluvian";
        private const string assetString = assetPrefix + ":Assets/Diluvian/DiluvianIcon.png";
        //The Assetbundle bundled into the dll has the path above to the icon.
        //When replicating make sure the icon is saved as a sprite.

        //Hold for the old language so that we can restore it.
        private readonly Dictionary<string, string> DefaultLanguage;
        //Keeping track of ESO
        private bool ESOenabled = false;
        //Keeping track of internal state.
        private bool HooksApplied = false;
        //Remember vanilla values.
        private float[] vanillaEliteMultipliers;
        //Cache the array for the combatdirector.
        private EliteDef[] CombatDirectorTierDefs;
        //Cache the fieldinfo for bloodshrines.
        private FieldInfo BloodShrineWaitingForRefresh;
        private FieldInfo BloodShrinePurchaseInteraction;


        private Diluvian()
        {
            DiluvianDef = new DifficultyDef(
                            DiluvianCoef,
                            "DIFFICULTY_DILUVIAN_NAME",
                            assetString,
                            "DIFFICULTY_DILUVIAN_DESCRIPTION",
                            DiluvianColor,
                            "DIL",
                            true
                            );
            DefaultLanguage = new Dictionary<string, string>();
        }

        public void Awake()
        {
            //acquire my assetbundle and give it to the resource api.
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Diluvian.diluvian"))
            {
                var bundle = AssetBundle.LoadFromStream(stream);
                var provider = new R2API.AssetBundleResourcesProvider(assetPrefix, bundle);
                R2API.ResourcesAPI.AddProvider(provider);
            }
            //Check ESO existence.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.jarlyk.eso"))
            {
                ESOenabled = true;
                Logger.LogWarning("ESO detected: Delegating Elite modifications to them. Future support planned.");
            }
            //Index tierDef array.
            CombatDirectorTierDefs = (EliteDef[])typeof(RoR2.CombatDirector).GetFieldCached("eliteTiers").GetValue(null);
            //Init array because we now know the size.
            vanillaEliteMultipliers = new float[CombatDirectorTierDefs.Length];
            //Cache the BloodshrineBehaviour field.
            BloodShrineWaitingForRefresh = typeof(RoR2.ShrineBloodBehavior).GetField("waitingForRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
            BloodShrinePurchaseInteraction = typeof(RoR2.ShrineBloodBehavior).GetField("purchaseInteraction", BindingFlags.Instance | BindingFlags.NonPublic);

            //Acquire my index from R2API.
            DiluvianIndex = R2API.DifficultyAPI.AddDifficulty(DiluvianDef);

            //Create my description.
            LanguageAPI.Add("DIFFICULTY_DILUVIAN_NAME", "Diluvian");
            string description = "For those found wanting. <style=cDeath>N'Kuhana</style> watches with interest.<style=cStack>\n";
            description = string.Join("\n",
                description,
                $">Difficulty Scaling: +{DiluvianDef.scalingValue * 50 - 100}%",
                $">Player Health Regeneration: {(int)(HealthRegenMultiplier * 100)}%",
                ">Player luck: Reduced in some places.",
                $">Monster Health Regeneration: +{MonsterRegen * 100}% of MaxHP per second (out of danger)",
                ">Oneshot Protection: Also applies to monsters",
                $">Oneshot Protection: Protects only {100 - 100 * NewOSPTreshold}%",
                $">Elites: {(1 - EliteModifier) * 100}% cheaper.",
                ">Shrine of Blood: Cost hidden and random."

                );
            description += "</style>";
            LanguageAPI.Add("DIFFICULTY_DILUVIAN_DESCRIPTION", description);

            //This is where my hooks live. They themselves are events, not ONhooks
            RoR2.Run.onRunStartGlobal += Run_onRunStartGlobal;
            RoR2.Run.onRunDestroyGlobal += Run_onRunDestroyGlobal;

        }

        //Because I want to restore the orignal strings when replacing them with assetplus, I store them prior to replacing them.
        private void ReplaceString(string token, string newText)
        {
            DefaultLanguage[token] = RoR2.Language.GetString(token);
            LanguageAPI.Add(token, newText);
        }


        private void Run_onRunStartGlobal(RoR2.Run run)
        {
            //Only do stuff when Diluvian is selected.
            if (run.selectedDifficulty == DiluvianIndex && HooksApplied == false)
            {
                ChatMessage.SendColored("A storm is brewing...", DiluvianColor);
                //Make our hooks.
                HooksApplied = true;
                IL.RoR2.HealthComponent.TakeDamage += UnluckyBears;
                IL.RoR2.HealthComponent.TakeDamage += ChangeOSP;
                IL.RoR2.CharacterBody.RecalculateStats += AdjustRegen;
                RoR2.TeleporterInteraction.onTeleporterFinishGlobal += MakeSureBonusDirectorDiesOnStageFinish;
                //
                if (!ESOenabled)
                {
                    for (int i = 0; i < CombatDirectorTierDefs.Length; i++)
                    {
                        EliteDef tierDef = CombatDirectorTierDefs[i];
                        vanillaEliteMultipliers[i] = tierDef.costMultiplier;
                        tierDef.costMultiplier *= EliteModifier;
                    }
                }
                On.RoR2.ShrineBloodBehavior.FixedUpdate += BloodShrinePriceRandom;

                //All of these replace strings.
                ReplaceInteractibles();
                ReplaceItems();
                ReplaceObjectives();
                ReplacePause();
                ReplaceStats();
                //Forcefully update the language
            }
        }

        //Failed code for keeping the teleporter director enabled. The game disagreees with me that this code sets it to disabled.
        /*
        private void ChargingState_OnExit(ILContext il)
        {
            ILCursor c = new ILCursor(il);
            c.GotoNext(MoveType.After,//Position our cursor far forward.
                x => x.MatchCallOrCallvirt<TeleporterInteraction.ChargingState>("get_bonusDirector")//13	002B	call	instance class RoR2.CombatDirector RoR2.TeleporterInteraction/ChargingState::get_bossDirector()
                );
            c.GotoNext(MoveType.Before,
                x => x.MatchCallOrCallvirt<TeleporterInteraction.ChargingState>("get_bonusDirector"),
                x => x.MatchLdcI4(0)
                );
            c.Index += 1;//Move our cursor between the instructions just matched.
            c.RemoveRange(2);//remove the 'false' and the enabled call of the bonusdirector
            c.EmitDelegate<Action<RoR2.CombatDirector>>((director) =>
                {
                    director.expRewardCoefficient = 0f;//make the monsters not worth anything after the teleporter has finished charging.
                }
            );
        }
        */

        private void Run_onRunDestroyGlobal(RoR2.Run obj)
        {
            if (HooksApplied)
            {
                //Remove all of our hooks on run end and restore elite tables to their original value.
                IL.RoR2.HealthComponent.TakeDamage -= UnluckyBears;
                IL.RoR2.HealthComponent.TakeDamage -= ChangeOSP;
                IL.RoR2.CharacterBody.RecalculateStats -= AdjustRegen;
                //RoR2.TeleporterInteraction.onTeleporterChargedGlobal += NoSafetyAfterFinishCharging;
                //IL.RoR2.TeleporterInteraction.StateFixedUpdate -= NoSafetyAfterFinishCharging;
                RoR2.TeleporterInteraction.onTeleporterFinishGlobal -= MakeSureBonusDirectorDiesOnStageFinish;
                if (!ESOenabled)
                {
                    for (int i = 0; i < CombatDirectorTierDefs.Length; i++)
                    {
                        EliteDef tierDef = CombatDirectorTierDefs[i];
                        tierDef.costMultiplier = vanillaEliteMultipliers[i];
                    }
                }
                On.RoR2.ShrineBloodBehavior.FixedUpdate -= BloodShrinePriceRandom;

                //Restore vanilla text.
                DefaultLanguage.ForEachTry((pair) =>
                {
                    LanguageAPI.Add(pair.Key, pair.Value);
                });
                HooksApplied = false;
            }
        }


        private void BloodShrinePriceRandom(On.RoR2.ShrineBloodBehavior.orig_FixedUpdate orig, RoR2.ShrineBloodBehavior self)
        {
            //This 'if' is essentially the same as the first line of the orig method(, save that they don't need to do reflection).
            if ((bool)BloodShrineWaitingForRefresh.GetValue(self))
            {
                orig(self);
                //reflect to get the purchaseinteractioncomponent. I then get a random number to change it's price. The price is hidden by the language modification.
                RoR2.PurchaseInteraction pi = ((RoR2.PurchaseInteraction)BloodShrinePurchaseInteraction.GetValue(self));
                if (pi)
                {
                    pi.Networkcost = RoR2.Run.instance.stageRng.RangeInt(50, 100);
                }
            }
        }


        private void MakeSureBonusDirectorDiesOnStageFinish(RoR2.TeleporterInteraction obj)
        {
            if (obj.bonusDirector && obj.bonusDirector.enabled)
            {
                obj.bonusDirector.enabled = false;
            }

        }

        private void AdjustRegen(ILContext il)
        {
            ILCursor c = new ILCursor(il);
            int monsoonPlayerHelperCountIndex = 25;//This value doesn't need to be set, but I set it to keep track of what it's expected to be.
            c.GotoNext(//Since this is only to move the cursor, no explicit movetyp is given as I don't care.
                x => x.MatchLdcI4((int)ItemIndex.MonsoonPlayerHelper),//Find where the itemcount of the monsoon regen modifier is called
                x => x.MatchCallvirt<RoR2.Inventory>("GetItemCount"),
                x => x.MatchStloc(out monsoonPlayerHelperCountIndex)//Then use the `out` keyword to store the index of the local holding the count.
                );
            int regenMultiIndex = 44;//Again this is the expected index,  but I'll be overwriting it.
            c.GotoNext(MoveType.Before,//Movetype defaults to before, but I like to make it epxlicit.
                x => x.MatchStloc(out regenMultiIndex),//The regen multiplier local field is stored right before...
                x => x.MatchLdloc(monsoonPlayerHelperCountIndex)//the itemcount of the monsoonmultiplier item is checked.
                );
            c.Index += 2;//Since the previous cursor location will get skipped over by if statements, we need to move into a slightly weird place that is always called
            c.Emit(OpCodes.Ldarg_0);//emit the characterbody
            c.Emit(OpCodes.Ldloc, regenMultiIndex);//emit the local regen multiplier
            c.EmitDelegate<Func<RoR2.CharacterBody, float, float>>((self, regenMulti) =>//emit a function taking a charbody and a float that returns a float.
            {
                if (self.isPlayerControlled)
                {
                    regenMulti += HealthRegenMultiplier;//note that a + -b == a - b;
                }
                return regenMulti;
            });
            c.Emit(OpCodes.Stloc, regenMultiIndex);//store the result.
            int regenIndex = 36;//As usual with indexes, I overwrite this later with the 'out' keyword. I just put the expected value here
            c.GotoPrev(MoveType.Before,//We need to go back, but I couldn't find these instructions earlier becase I hadn't found the regenMultiIndex yet.
                x => x.MatchStloc(out regenIndex),//The base+level regen i stored with this instruction.
                x => x.MatchLdcR4(1),//And the regen multiplier is initialised.
                x => x.MatchStloc(regenMultiIndex)
                ); ;
            c.Index += 1;//go to right before the init of the regen multi
            c.Emit(OpCodes.Ldarg_0);//emit the characterbody
            c.Emit(OpCodes.Ldloc, regenIndex);//emit the value of the regen
            c.EmitDelegate<Func<RoR2.CharacterBody, float, float>>((self, regen) =>//emit a function taking a characterbody an a float that returns a float
            {
                //Check if this is a monster and if it's not been hit for 5 seconds.
                if (self.teamComponent.teamIndex == TeamIndex.Monster && self.outOfDanger && self.baseNameToken != "ARTIFACTSHELL_BODY_NAME" && self.baseNameToken != "TITANGOLD_BODY_NAME")
                {
                    regen += self.maxHealth * MonsterRegen;
                }
                return regen;
            });
            c.Emit(OpCodes.Stloc, regenIndex);//store the new regen.
        }

        private void UnluckyBears(ILContext il)
        {
            ILCursor c = new ILCursor(il);
            c.GotoNext(
                x => x.MatchLdcI4((int)ItemIndex.Bear),
                x => x.MatchCallvirt<RoR2.Inventory>("GetItemCount")
                );
            c.GotoNext(MoveType.Before,
                x => x.MatchLdcR4(0),//This is the luck value used in CheckRoll for toughertimes.
                x => x.MatchLdnull(),
                x => x.MatchCall("RoR2.Util", "CheckRoll")
                );
            c.Remove();
            c.Emit(OpCodes.Ldc_R4, -1f);//We replace it with -1. Because no mercy.
        }

        private void ChangeOSP(ILContext il)
        {
            try
            {
                ILCursor c = new ILCursor(il);
                c.GotoNext(MoveType.After,//move to right after the call to check if it's a player.
                    x => x.MatchCallvirt<RoR2.HealthComponent>("get_hasOneshotProtection")
                    );
                c.Emit(OpCodes.Pop);//remove the result from the stack
                c.Emit(OpCodes.Ldc_I4_1);//put "true" on the stack.
                c.GotoNext(MoveType.After,//go to right after the 0.9 from OSP
                        x => x.MatchLdcR4(0.9f)
                        );
                c.Emit(OpCodes.Pop);//Remove the 0.9
                c.Emit(OpCodes.Ldc_R4, NewOSPTreshold);//Put my valye there instead.
            }
            catch (Exception e) //probably pointless.
            {
                Logger.LogWarning("Couldn't modify OneShotProtection. Maybe you have a mod interfering. Game might act weird.");
                Logger.LogError(e);
            }
        }


        //Below here is only string replacement.

        private void ReplaceInteractibles()
        {
            ReplaceString("MSOBELISK_CONTEXT", "Escape the madness");
            ReplaceString("MSOBELISK_CONTEXT_CONFIRMATION", "Take the cowards way out");
            ReplaceString("COST_PERCENTHEALTH_FORMAT", "?");//hide the cost for bloodshrines.
            ReplaceString("SHRINE_BLOOD_USE_MESSAGE_2P", "<style=cDeath>N'Kuhana</style>: This pleases me. <style=cShrine>({1})</color>");
            ReplaceString("SHRINE_BLOOD_USE_MESSAGE", "<style=cDeath>N'Kuhana</style>: {0} has paid their respects. Will you do the same? <style=cShrine>({1})</color>");
            ReplaceString("SHRINE_HEALING_USE_MESSAGE_2P", "<style=cDeath>N'Kuhana</style>: Bask in my embrace.");
            ReplaceString("SHRINE_HEALING_USE_MESSAGE", "<style=cDeath>N'Kuhana</style>: Bask in my embrace.");
            ReplaceString("SHRINE_BOSS_BEGIN_TRIAL", "<style=cShrine>Show me your courage.</style>");
            ReplaceString("SHRINE_BOSS_END_TRIAL", "<style=cShrine>Your effort entertains me.</style>");
            ReplaceString("PORTAL_MYSTERYSPACE_CONTEXT", "Hide in another realm.");
            ReplaceString("SCAVLUNAR_BODY_SUBTITLE", "The weakling");
            ReplaceString("MAP_LIMBO_SUBTITLE", "Hideaway");
        }

        private void ReplaceItems()
        {
            ReplaceString("ITEM_BEAR_PICKUP", "Chance to block incoming damage. They think you are <style=cDeath>unlucky</style>.");
        }

        private void ReplaceObjectives()
        {
            ReplaceString("OBJECTIVE_FIND_TELEPORTER", "Flee");
            ReplaceString("OBJECTIVE_DEFEAT_BOSS", "Defeat the <style=cDeath>Anchor</style>");
        }
        private void ReplacePause()
        {
            ReplaceString("PAUSE_RESUME", "Entertain me");
            ReplaceString("PAUSE_SETTINGS", "Change your perspective");
            ReplaceString("PAUSE_QUIT_TO_MENU", "Give up");
            ReplaceString("PAUSE_QUIT_TO_DESKTOP", "Don't come back");
            ReplaceString("QUIT_RUN_CONFIRM_DIALOG_BODY_SINGLEPLAYER", "You are a disappointment.");
            ReplaceString("QUIT_RUN_CONFIRM_DIALOG_BODY_CLIENT", "Leave these weaklings with me?");
            ReplaceString("QUIT_RUN_CONFIRM_DIALOG_BODY_HOST", "You are my main interest, <style=cDeath>end the others?</style>");
        }
        private void ReplaceStats()
        {
            ReplaceString("STAT_KILLER_NAME_FORMAT", "Released by: <color=#FFFF7F>{0}</color>");
            ReplaceString("STAT_POINTS_FORMAT", "");//Delete points
            ReplaceString("STAT_TOTAL", "");//delete more points
            ReplaceString("STAT_CONTINUE", "Try again");

            ReplaceString("STATNAME_TOTALTIMEALIVE", "Wasted time");
            ReplaceString("STATNAME_TOTALDEATHS", "Times Blessed");
            ReplaceString("STATNAME_HIGHESTLEVEL", "Strength Acquired");
            ReplaceString("STATNAME_TOTALGOLDCOLLECTED", "Greed");
            ReplaceString("STATNAME_TOTALDISTANCETRAVELED", "Ground laid to waste");
            ReplaceString("STATNAME_TOTALMINIONDAMAGEDEALT", "Pacification delegated");
            ReplaceString("STATNAME_TOTALITEMSCOLLECTED", "Trash cleaned up");
            ReplaceString("STATNAME_HIGHESTITEMSCOLLECTED", "Most trash held");
            ReplaceString("STATNAME_TOTALSTAGESCOMPLETED", "Times fled");
            ReplaceString("STATNAME_HIGHESTSTAGESCOMPLETED", "Times fled");
            ReplaceString("STATNAME_TOTALPURCHASES", "Offered");
            ReplaceString("STATNAME_HIGHESTPURCHASES", "Offered");

            ReplaceString("GAME_RESULT_LOST", "PATHETHIC");
            ReplaceString("GAME_RESULT_WON", "IMPRESSIVE");
            ReplaceString("GAME_RESULT_UNKNOWN", "where are you?");
        }
    }
}
