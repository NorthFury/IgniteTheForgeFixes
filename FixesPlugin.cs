using Gameplay.Battle.API;
using Gameplay.Battle.API.Signals;
using Gameplay.Battle.API.Statuses;
using Gameplay.Battle.API.Statuses.BattleEvents;
using Gameplay.Battle.Implementation;
using Gameplay.Battle.Implementation.BattleEvents.Effects;
using HarmonyLib;
using MelonLoader;
using System.Reflection;
using System.Reflection.Emit;

[assembly: MelonInfo(typeof(IgniteTheForgeFixes.FixesPlugin), "Fixes", "1.0.3", "North")]
[assembly: MelonGame("Floodgate Crew", "Blacksmith Ignite the Forge")]

namespace IgniteTheForgeFixes {
    public class FixesPlugin : MelonMod {
        internal static MelonLogger.Instance Log => Melon<FixesPlugin>.Logger;

        public override void OnInitializeMelon() {
            var transpilerMethod = new HarmonyMethod(typeof(LuckModifierPatch).GetMethod(nameof(LuckModifierPatch.Transpiler)));

            List<MethodInfo> targetsToPatch = new() {
                AccessTools.Method(typeof(RandomEffectCalculator), "EffectHappens"),
                AccessTools.Method(typeof(ApplyStatusToAttackerOnBeingHitEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(ApplyStatusToTargetHitEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(ApplyStatusToTargetOnSuccessfulAOEAttackEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(ApplyStatusToTargetOnSuccessfulAttackEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(ApplyStatusToTargetOnSuccessfulCriticalAttackHitEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(ApplyStatusWhenApplyingOtherStatusEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(DealDamageToRandomEnemyAndApplyStatusEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(DealDamageToRandomEnemyAndNeighboursEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(DealDamageToTargetAndNeighboursEffect), "EffectOnTarget"),
                AccessTools.Method(typeof(ApplyStatusToAlliesOnGainingItEffect), "OnSignal"),
                AccessTools.Method(typeof(ApplyStatusToSummonedUnitEffect), "OnSignal"),
            };

            foreach (MethodInfo target in targetsToPatch) {
                if (target != null) {
                    HarmonyInstance.Patch(target, transpiler: transpilerMethod);
                }
            }
        }
    }

    public static class LuckModifierPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new(instructions);

            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Mul) {
                    codes[i].opcode = OpCodes.Add;

                    // luck modifier is 1.1 for 10% extra luck; remove 1 to only add 0.1
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldc_R4, 1.0f));
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Sub));

                    FixesPlugin.Log.Msg("LuckModifier patched.");
                    break;
                }
            }

            return codes;
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
                    // change (_chanceToActivate) to (_chanceToActivate + _owner.LuckModifier - 1.0f)
                    codes.InsertRange(i + 1, new[] {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, ownerField),
                        new CodeInstruction(OpCodes.Callvirt, luckGetter),
                        new CodeInstruction(OpCodes.Add),
                        new CodeInstruction(OpCodes.Ldc_R4, 1.0f),
                        new CodeInstruction(OpCodes.Sub)
                    });

                    FixesPlugin.Log.Msg("BlizzardEffect.OnUnitTurnStarted patched.");
                    break;
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

            // change the addition to subtraction
            for (int i = 0; i < codes.Count - 1; i++) {
                if (codes[i].opcode == OpCodes.Mul && codes[i + 1].opcode == OpCodes.Add) {
                    codes[i + 1].opcode = OpCodes.Sub;

                    patchedLoop = true;
                    break;
                }
            }

            // include the luck modifer in the check: _effectConfig.RandomChance + _owner.LuckModifier
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Ldfld && codes[i].operand is FieldInfo fInfo && fInfo == randomChanceField) {

                    codes.InsertRange(i + 1, new[] {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, ownerField),
                        new CodeInstruction(OpCodes.Callvirt, luckGetter),
                        new CodeInstruction(OpCodes.Add),
                        new CodeInstruction(OpCodes.Ldc_R4, 1.0f),
                        new CodeInstruction(OpCodes.Sub)
                    });

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
            codes.InsertRange(0, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),                                 // Load 'this'
                new CodeInstruction(OpCodes.Ldfld, ownerField),                       // Load 'this._owner'
                new CodeInstruction(OpCodes.Ldarg_1),                                 // Load 'signal'
                new CodeInstruction(OpCodes.Ldfld, targetField),                      // Load 'signal.Target'
                new CodeInstruction(OpCodes.Beq_S, continueOriginalMethod),           // If equal, jump to original start
                new CodeInstruction(OpCodes.Ret)                                      // Else, return early
            });

            FixesPlugin.Log.Msg("MultiplyGoldGainedPatch.OnSignal patched.");

            return codes;
        }
    }

    [HarmonyPatch(typeof(ChangeCritDamageModifierBattleEventEffect), "EffectOnTarget")]
    public static class ChangeCritDamageModifierBattleEventEffectPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new(instructions);

            for (int i = 0; i < codes.Count - 3; i++) {
                // find the instructions for _critDamagePercentageChange * _previousCritDamageModifier
                if (codes[i].opcode == OpCodes.Ldfld && codes[i].operand?.ToString().Contains("_critDamagePercentageChange") == true) {
                    // remove the multiplication since the value is already a percentage multiplier
                    if (i + 3 < codes.Count && codes[i + 3].opcode == OpCodes.Mul) {
                        // ldarg.0
                        // ldfld _previousCritDamageModifier
                        // mul
                        codes.RemoveRange(i + 1, 3);

                        FixesPlugin.Log.Msg("ChangeCritDamageModifierBattleEventEffect.EffectOnTarget patched.");
                        break;
                    }
                }
            }

            return codes;
        }
    }

    [HarmonyPatch(typeof(Unit), "Attack")]
    public static class UnitAttackPatch {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            MethodInfo critChanceGetter = AccessTools.PropertyGetter(typeof(Unit), "CritChance");
            MethodInfo luckModifierGetter = AccessTools.PropertyGetter(typeof(Unit), "LuckModifier");

            if (critChanceGetter == null || luckModifierGetter == null) {
                MelonLogger.Error("Failed to resolve property getters for Unit patch.");
                return instructions;
            }

            List<CodeInstruction> codes = new(instructions);

            for (int i = 0; i < codes.Count - 2; i++) {
                if (codes[i].opcode == OpCodes.Call && codes[i].operand as MethodInfo == critChanceGetter
                    && codes[i + 1].opcode == OpCodes.Ldc_R4
                ) {
                    codes[i + 1].opcode = OpCodes.Ldarg_0;
                    codes[i + 1].operand = null;

                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, luckModifierGetter));

                    FixesPlugin.Log.Msg("Unit.Attack patched.");
                    break;
                }
            }

            return codes;
        }
    }

}
