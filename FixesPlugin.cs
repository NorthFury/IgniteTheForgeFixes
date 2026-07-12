using Gameplay.Battle.API;
using Gameplay.Battle.API.Signals;
using Gameplay.Battle.API.Statuses;
using Gameplay.Battle.API.Statuses.BattleEvents;
using Gameplay.Battle.Implementation.BattleEvents.Effects;
using Gameplay.Core.API;
using HarmonyLib;
using MelonLoader;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

[assembly: MelonInfo(typeof(IgniteTheForgeFixes.FixesPlugin), "Fixes", "1.0.0", "North")]
[assembly: MelonGame("Floodgate Crew", "Blacksmith Ignite the Forge")]

namespace IgniteTheForgeFixes {
    public class FixesPlugin : MelonMod {
        internal static MelonLogger.Instance Log => Melon<FixesPlugin>.Logger;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName) {
            MelonLogger.Msg("StatChangeWeaponEnchantment");
            var enchantments = Resources.FindObjectsOfTypeAll<StatChangeWeaponEnchantment>();
            foreach (var enchantment in enchantments) {
                MelonLogger.Msg($"{enchantment.name} {enchantment.Description}");
            }

            //MelonLogger.Msg("OreType");
            //var oreTypes = Resources.FindObjectsOfTypeAll<OreType>();
            //foreach (var oreType in oreTypes) {
            //    MelonLogger.Msg($"{oreType.name} {oreType.OreColor}");
            //    foreach (var enchantment in oreType.Enchantments) {
            //        MelonLogger.Msg($"{enchantment.name} {enchantment.Description}");
            //    }
            //}

            MelonLogger.Msg("GameConfig");
            var gameConfigs = Resources.FindObjectsOfTypeAll<GameConfig>();
            foreach (var gameConfig in gameConfigs) {
                MelonLogger.Msg($"BaseCriticalMultiplier {gameConfig.BattleConfig.BaseCriticalMultiplier}");
            }
        }
    }

    [HarmonyPatch(typeof(KillTargetOnHitEffect), "EffectOnTarget")]
    public static class KillTargetOnHitEffectPatch {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            MethodInfo getStacksMethod = AccessTools.PropertyGetter(typeof(StatusBase), "Stacks");
            MethodInfo getConfigMethod = AccessTools.PropertyGetter(typeof(BattleEvent), "Config");

            if (getStacksMethod == null || getConfigMethod == null) {
                FixesPlugin.Log.Error("Failed to find required reflection properties!");
                return instructions;
            }

            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Ldfld &&
                    codes[i].operand is FieldInfo field &&
                    field.Name == "_bonusHealthThresholdPerStatus") {

                    CodeInstruction ldlocItemInstruction = null;
                    for (int j = i - 1; j >= 1; j--) {
                        if (codes[j].opcode == OpCodes.Callvirt && (MethodInfo)codes[j].operand == getConfigMethod && codes[j - 1].IsLdloc()) {
                            ldlocItemInstruction = codes[j - 1].Clone();
                            break;
                        }
                    }

                    if (ldlocItemInstruction != null) {
                        codes.InsertRange(i + 1, new[] {
                        ldlocItemInstruction,                                   // Load 'item' (StatusBase)
                        new CodeInstruction(OpCodes.Callvirt, getStacksMethod), // item.Stacks (returns int)
                        new CodeInstruction(OpCodes.Conv_R4),                    // Convert int to float
                        new CodeInstruction(OpCodes.Mul)                         // Multiply threshold * stacks
                    });

                        FixesPlugin.Log.Msg("KillTargetOnHitEffect.EffectOnTarget patched.");
                        break;
                    }
                }
            }

            return codes;
        }
    }

    [HarmonyPatch(typeof(BlizzardEffect), "OnUnitTurnStarted")]
    public static class BlizzardEffectPatch {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            FieldInfo chanceField = AccessTools.Field(typeof(BlizzardEffect), "_chanceToActivate");
            FieldInfo ownerField = AccessTools.Field(typeof(BlizzardEffect), "_owner");
            MethodInfo luckGetter = AccessTools.PropertyGetter(typeof(Unit), "LuckModifier");

            if (chanceField == null || ownerField == null || luckGetter == null) {
                FixesPlugin.Log.Error("Failed to find required reflection properties!");
                return instructions;
            }

            List<CodeInstruction> codes = new(instructions);

            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Ldfld && (FieldInfo)codes[i].operand == chanceField) {
                    // change (_chanceToActivate) to (_chanceToActivate * _owner.LuckModifier)
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Ldfld, ownerField));
                    codes.Insert(i + 3, new CodeInstruction(OpCodes.Callvirt, luckGetter));
                    codes.Insert(i + 4, new CodeInstruction(OpCodes.Mul));

                    i += 4;
                    FixesPlugin.Log.Msg("BlizzardEffect.OnUnitTurnStarted patched.");
                }
            }

            return codes;
        }
    }

    [HarmonyPatch(typeof(HealingWaterEventEffect), "EffectOnTarget")]
    public static class HealingWaterEventEffectPatch {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new(instructions);

            FieldInfo ownerField = AccessTools.Field(typeof(BattleEventEffect), "_owner");
            MethodInfo luckGetter = AccessTools.PropertyGetter(typeof(Unit), "LuckModifier");
            FieldInfo randomChanceField = AccessTools.Field(typeof(HealingWaterEventEffectConfig), "RandomChance");

            bool patchedLoop = false;
            bool patchedCondition = false;

            // Patch the loop logic: num -= _effectConfig.BonusChancePerStatusTime * (float)battleEvent.Stacks * _owner.LuckModifier;
            for (int i = 0; i < codes.Count - 1; i++) {
                if (codes[i].opcode == OpCodes.Mul && codes[i + 1].opcode == OpCodes.Add) {
                    codes[i + 1].opcode = OpCodes.Sub;

                    List<CodeInstruction> loopInjection = new() {
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldfld, ownerField),
                        new(OpCodes.Callvirt, luckGetter),
                        new(OpCodes.Mul)
                    };

                    // Insert the elements directly between the original Mul and our freshly changed Sub
                    codes.InsertRange(i + 1, loopInjection);

                    patchedLoop = true;
                    break;
                }
            }

            // Patch the branch condition: _effectConfig.RandomChance * _owner.LuckModifier
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Ldfld && codes[i].operand is FieldInfo fInfo && fInfo == randomChanceField) {
                    List<CodeInstruction> conditionInjection = new() {
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldfld, ownerField),
                        new(OpCodes.Callvirt, luckGetter),
                        new(OpCodes.Mul)
                    };

                    // Insert immediately after RandomChance is pushed onto the evaluation stack
                    codes.InsertRange(i + 1, conditionInjection);

                    patchedCondition = true;
                    break;
                }
            }

            if (patchedLoop && patchedCondition) {
                FixesPlugin.Log.Msg("HealingWaterEventEffect.EffectOnTarget patched.");
            } else {
                FixesPlugin.Log.Warning($"HealingWaterEventEffect.EffectOnTarget patch failure. Loop Patched: {patchedLoop}, Condition Patched: {patchedCondition}");
            }

            return codes;
        }
    }

    [HarmonyPatch(typeof(AttackAllOnCritEffect), "EffectOnTarget")]
    public static class AttackAllOnCritEffect_Patch {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var codes = new List<CodeInstruction>(instructions);

            for (int i = 1; i < codes.Count - 1; i++) {
                if (codes[i - 1].opcode == OpCodes.Add && codes[i].opcode == OpCodes.Neg && codes[i + 1].opcode == OpCodes.Conv_I4) {
                    codes[i].opcode = OpCodes.Nop;
                    FixesPlugin.Log.Msg("AttackAllOnCritEffect.EffectOnTarget patched.");
                    break;
                }
            }

            return codes;
        }
    }

    [HarmonyPatch(typeof(MultiplyGoldGainedBattleEventEffect), MethodType.Constructor, new[] { typeof(MultiplyGoldGainedBattleEventEffectConfig) })]
    public static class MultiplyGoldGainedConstructorPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var codes = new List<CodeInstruction>(instructions);

            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Ldc_I4_0) {
                    // set _isSubscribed to true instead of false
                    codes[i].opcode = OpCodes.Ldc_I4_1;
                    FixesPlugin.Log.Msg("MultiplyGoldGainedBattleEventEffect.Constructor patched.");
                    break;
                }
            }
            return codes;
        }
    }

    [HarmonyPatch(typeof(MultiplyGoldGainedBattleEventEffect), "OnSignal")]
    public static class MultiplyGoldGainedPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            var codes = new List<CodeInstruction>(instructions);

            var ownerField = AccessTools.Field(typeof(MultiplyGoldGainedBattleEventEffect), "_owner");
            var targetField = AccessTools.Field(typeof(GoldChangedByUnitSignal), "Target");

            Label continueOriginalMethod = il.DefineLabel();
            codes[0].labels.Add(continueOriginalMethod);

            // generate code for "if (_owner != signal.Target) return;"
            var injectedCodes = new List<CodeInstruction> {
                new(OpCodes.Ldarg_0),                                 // Load 'this'
                new(OpCodes.Ldfld, ownerField),                       // Load 'this._owner'
                new(OpCodes.Ldarg_1),                                 // Load 'signal'
                new(OpCodes.Ldfld, targetField),                      // Load 'signal.Target'
                new(OpCodes.Beq_S, continueOriginalMethod),           // If equal, jump to original start
                new(OpCodes.Ret)                                      // Else, return early
            };

            codes.InsertRange(0, injectedCodes);

            FixesPlugin.Log.Msg("MultiplyGoldGainedPatch.OnSignal patched.");

            return codes;
        }
    }

    //[HarmonyPatch(typeof(ChangeCritChanceForEnemiesAboveThresholdEffect), "OnUnitTurnStarted")]
    //public class ChangeCritChanceForEnemiesAboveThresholdEffect_Patch {
    //    [HarmonyPrefix]
    //    public static bool Prefix(ChangeCritChanceForEnemiesAboveThresholdEffect __instance, UnitTurnStartedSignal signal) {
    //        Traverse traverse = Traverse.Create(__instance);
    //        var _hpPercentageThreshold = traverse.Field("_hpPercentageThreshold").GetValue<float>();
    //        var battleMinigameController = traverse.Property("BattleMinigameController").GetValue<Gameplay.Battle.Implementation.BattleMinigameController>();
    //        foreach (Unit unit in battleMinigameController.GetEnemyTeam().Units) {
    //            FixesPlugin.Log.Msg($"unit.CurrentHealth {unit.CurrentHealth}");
    //        }
    //        FixesPlugin.Log.Msg($"_hpPercentageThreshold {_hpPercentageThreshold}");

    //        return true; // execute original code
    //    }
    //}

    //[HarmonyPatch(typeof(ApplyStatusToTargetOnSuccessfulAttackEffect), "EffectOnTarget")]
    //public class ApplyStatusToTargetOnSuccessfulAttackEffect_Patch {
    //    [HarmonyPostfix]
    //    public static void Postfix(ApplyStatusToTargetOnSuccessfulAttackEffect __instance, Unit target) {
    //        Traverse traverse = Traverse.Create(__instance);
    //        Unit _owner = traverse.Field("_owner").GetValue<Unit>();
    //        FixesPlugin.Log.Msg($"{_owner.Name} crit {_owner.CritDamageModifier}");
    //    }
    //}

    /*
    [HarmonyPatch(typeof(KillTargetOnHitEffect), "EffectOnTarget")]
    public class KillTargetOnHitEffect_Patch {
        [HarmonyPrefix]
        public static bool Prefix(KillTargetOnHitEffect __instance, Unit target) {
            try {
                Traverse traverse = Traverse.Create(__instance);
                float healthThreshold = traverse.Field("_healthThreshold").GetValue<float>();
                float bonusHealthThresholdPerStatus = traverse.Field("_bonusHealthThresholdPerStatus").GetValue<float>();
                BattleEventConfig bonusStatus = traverse.Field("_bonusStatus").GetValue<BattleEventConfig>();

                float computedThreashold = healthThreshold;
                computedThreashold += target.Statuses
                    .OfType<BattleEvent>()
                    .Where(it => it.Config == bonusStatus)
                    .Sum(it => bonusHealthThresholdPerStatus * it.Stacks);

                if (target.CurrentHealthPercent <= computedThreashold) {
                    target.Kill();
                }

                return false; // skip original code
            } catch (Exception ex) {
                FixesPlugin.Log.Error($"Failed fixing KillTargetOnHitEffect.EffectOnTarget: {ex}");
                return true; // execute original code
            }
        }
    }
    */

    //[HarmonyPatch(typeof(Gameplay.AlloyMelting.Impl.CrucibleBehaviour), "CheckInsertedOre")]
    //public class CrucibleBehaviour_Patch {
    //    [HarmonyPostfix]
    //    public static void Postfix(Gameplay.AlloyMelting.Impl.CrucibleBehaviour __instance, Gameplay.AlloyMelting.Impl.CrucibleOreReceiver updatedReceiver) {
    //        if (__instance == null) return;
    //        try {
    //            FixesPlugin.Log.Msg(">>>>>>>>>>>>>>>>>>>>>>>>");
    //            var gameConfig = Traverse.Create(__instance).Property("GameConfig").GetValue<Gameplay.Core.API.GameConfig>();

    //            foreach (var followerPrototype in gameConfig.FollowersConfig.FollowerPrototypes) {
    //                FixesPlugin.Log.Msg($"## {followerPrototype.name}");
    //                FixesPlugin.Log.Msg($"Health: {followerPrototype.MaxHealthRange}");
    //                FixesPlugin.Log.Msg($"Damage: {followerPrototype.DamageModRange}");
    //                FixesPlugin.Log.Msg($"Speed: {followerPrototype.SpeedModRange}");
    //                FixesPlugin.Log.Msg($"CritChance: {followerPrototype.CritChangeRange}");
    //                FixesPlugin.Log.Msg($"StartingArmor: {followerPrototype.StartingArmorRange.x}");
    //                FixesPlugin.Log.Msg($"ChargesToUse: {followerPrototype.TalentTreeConfig.ActiveAbilityConfig.ChargesToUse}");
    //                FixesPlugin.Log.Msg($"ChargesGainedOnBeingAttacked: {followerPrototype.TalentTreeConfig.ActiveAbilityConfig.ChargesGainedOnBeingAttacked}");
    //                FixesPlugin.Log.Msg($"ChargesGainedOnPerformingAttack: {followerPrototype.TalentTreeConfig.ActiveAbilityConfig.ChargesGainedOnPerformingAttack}");
    //                FixesPlugin.Log.Msg($"ChargesGainedOnPerformingActiveAbility: {followerPrototype.TalentTreeConfig.ActiveAbilityConfig.ChargesGainedOnPerformingActiveAbility}");
    //                FixesPlugin.Log.Msg($"ActiveAbility: {followerPrototype.TalentTreeConfig.ActiveAbilityConfig.Description} {string.Join(", ", followerPrototype.TalentTreeConfig.ActiveAbilityConfig.EffectsTriggeredOnAbilityActivation.Select(e => e.EffectType.ToString() + " " + string.Join(", ", e.Triggers)))}");
    //                var abilityNodes = followerPrototype.TalentTreeConfig.TalentTreeGraph.Nodes().OfType<Gameplay.Followers.API.AbilityNode>();
    //                bool first = true;
    //                foreach (var abilityNode in abilityNodes) {
    //                    if (abilityNode.BattleEventConfig.Description != null && abilityNode.BattleEventConfig.Description.Length > 0) {
    //                        var prefix = "*";
    //                        if (first) {
    //                            first = false;
    //                            if (abilityNodes.Count(it => it.BattleEventConfig.Description != null && it.BattleEventConfig.Description.Length > 0) == 4) {
    //                                prefix = "Passive:";
    //                            }
    //                        }
    //                        FixesPlugin.Log.Msg($"{prefix} {abilityNode.BattleEventConfig.Description} {string.Join(", ", abilityNode.BattleEventConfig.Effects.Select(e => e.EffectType + " " + string.Join(", ", e.Triggers)))}");
    //                    }
    //                }
    //                FixesPlugin.Log.Msg("");
    //            }

    //        } catch (Exception ex) {
    //            FixesPlugin.Log.Error($"Failed fixing CrucibleBehaviour.CheckInsertedOre: {ex}");
    //        }
    //    }
    //}

    //[HarmonyPatch(typeof(Gameplay.RunesEmbedding.Impl.RuneTableRuneReceiver), "ReceiveItem")]
    //public class RuneTableRuneReceiver_Patch {
    //    [HarmonyPostfix]
    //    public static void Postfix(Gameplay.RunesEmbedding.Impl.RuneTableRuneReceiver __instance, Gameplay.Core.API.DraggableItem<Gameplay.Core.API.RuneItem> draggableItem) {
    //        if (__instance == null) return;
    //        try {
    //            FixesPlugin.Log.Msg($"{draggableItem.Item.RuneType.Name.GetLocalizedString()}");
    //            foreach (var enchantment in draggableItem.Item.RuneType.WeaponEnchantment) {
    //                try {
    //                    FixesPlugin.Log.Msg($">>> {enchantment.EnchantmentName.GetLocalizedString()}");
    //                    foreach (var effect in enchantment.Effects) {
    //                        FixesPlugin.Log.Msg($">>>>>> {effect.EffectType} {effect.Target} {string.Join(", ", effect.Triggers)}");
    //                        //if (effect is HealingWaterEventEffectConfig healingWaterEffectConfig) {
    //                        //    FixesPlugin.Log.Msg($">>>>>> HealAmount: {healingWaterEffectConfig.HealAmount}");
    //                        //    FixesPlugin.Log.Msg($">>>>>> RandomChance: {healingWaterEffectConfig.RandomChance}");
    //                        //    FixesPlugin.Log.Msg($">>>>>> BonusChancePerStatusTime: {healingWaterEffectConfig.BonusChancePerStatusTime}");
    //                        //    FixesPlugin.Log.Msg($">>>>>> TargetStatus: {healingWaterEffectConfig.TargetStatus}");
    //                        //}
    //                    }
    //                } catch (Exception) {
    //                }
    //            }

    //        } catch (Exception ex) {
    //            FixesPlugin.Log.Error($"Failed fixing RuneTableRuneReceiver.CheckInsertedOre: {ex}");
    //        }
    //    }
    //}


    //[HarmonyPatch(typeof(AttackAllOnCritEffect), "EffectOnTarget")]
    //public static class AttackAllOnCritEffectPatch {
    //    [HarmonyTranspiler]
    //    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
    //        List<CodeInstruction> codes = new(instructions);

    //        for (int i = 0; i < codes.Count - 1; i++) {
    //            FixesPlugin.Log.Msg(codes[i]);
    //        }

    //        return codes;
    //    }
    //}
}
