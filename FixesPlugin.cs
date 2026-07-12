using Gameplay.Battle.API;
using Gameplay.Battle.API.Signals;
using Gameplay.Battle.API.Statuses;
using Gameplay.Battle.API.Statuses.BattleEvents;
using Gameplay.Battle.Implementation.BattleEvents.Effects;
using HarmonyLib;
using MelonLoader;
using System.Reflection;
using System.Reflection.Emit;

[assembly: MelonInfo(typeof(IgniteTheForgeFixes.FixesPlugin), "Fixes", "1.0.0", "North")]
[assembly: MelonGame("Floodgate Crew", "Blacksmith Ignite the Forge")]

namespace IgniteTheForgeFixes {
    public class FixesPlugin : MelonMod {
        internal static MelonLogger.Instance Log => Melon<FixesPlugin>.Logger;
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

}
