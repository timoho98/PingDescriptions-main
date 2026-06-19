using BepInEx;
using MonoMod.RuntimeDetour;
using R2API;
using RoR2;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace PingDescriptions
{
    [BepInDependency(LanguageAPI.PluginGUID)]
    [BepInDependency("droppod.lookingglass", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class PingDescriptionsPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "timoh.PingDescriptions";
        public const string PluginName = "PingDescriptions";
        public const string PluginVersion = "1.0.0";

        private static MethodInfo _lgGetItemDescription;

        public void Awake()
        {
            Log.Init(Logger);
            Log.Info($"Loading {PluginName} v{PluginVersion}...");

            // Register a passthrough token so SimpleChatMessage can format our raw strings
            LanguageAPI.Add("PINGDESC_FORMAT", "{0}");

            // Soft-dep: if LookingGlass is loaded, grab its GetItemDescription for enhanced stats
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("droppod.lookingglass"))
            {
                try
                {
                    var lgType = Type.GetType("LookingGlass.ItemStatsNameSpace.ItemStats, LookingGlass");
                    _lgGetItemDescription = lgType?.GetMethod("GetItemDescription", BindingFlags.Public | BindingFlags.Static);

                    if (_lgGetItemDescription != null)
                        Log.Info("LookingGlass detected — will use enhanced item descriptions with stat calculations.");
                    else
                        Log.Warning("LookingGlass found but GetItemDescription method could not be located. Falling back to vanilla descriptions.");
                }
                catch (Exception e)
                {
                    Log.Warning($"Failed to access LookingGlass methods: {e.Message}");
                }
            }
            else
            {
                Log.Info("LookingGlass not detected — using vanilla item descriptions.");
            }

            // PingerController.SetCurrentPing is private, so we use MonoMod RuntimeDetour directly
            var pingMethod = typeof(PingerController).GetMethod(
                "SetCurrentPing",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (pingMethod == null)
            {
                Log.Error("Could not find PingerController.SetCurrentPing — ping hook will not be installed.");
                return;
            }

            new Hook(pingMethod,
                new Action<Action<PingerController, PingerController.PingInfo>, PingerController, PingerController.PingInfo>(OnPing));

            Log.Info($"{PluginName} loaded successfully. All players' item pings will be broadcast to chat.");
        }

        private static void OnPing(
            Action<PingerController, PingerController.PingInfo> orig,
            PingerController self,
            PingerController.PingInfo newPingInfo)
        {
            orig(self, newPingInfo);

            // Only the host/server should broadcast — clients would double-send otherwise
            if (!NetworkServer.active)
            {
                Log.Debug("OnPing: not the server, skipping.");
                return;
            }

            if (newPingInfo.targetGameObject == null)
            {
                Log.Debug("OnPing: ping has no target object, skipping.");
                return;
            }

            Log.Debug($"OnPing: ping detected on '{newPingInfo.targetGameObject.name}'");

            // Get the pinging player's master (PingerController lives on the CharacterMaster object)
            CharacterMaster pingerMaster = self.gameObject.GetComponent<CharacterMaster>();
            string pingerName = pingerMaster?.playerCharacterMasterController?.networkUser?.userName
                ?? pingerMaster?.GetBody()?.GetDisplayName()
                ?? "Unknown";

            Log.Debug($"OnPing: pinger is '{pingerName}'");

            PickupIndex pickupIndex = PickupIndex.none;

#pragma warning disable Publicizer001
            if (newPingInfo.targetGameObject.TryGetComponent<GenericPickupController>(out var worldItem))
            {
                pickupIndex = worldItem._pickupState.pickupIndex;
                Log.Debug($"OnPing: world item detected, pickupIndex={pickupIndex}");
            }
            else if (newPingInfo.targetGameObject.TryGetComponent<ShopTerminalBehavior>(out var shop))
            {
                if (!shop.hidden && shop.pickupDisplay != null)
                {
                    pickupIndex = shop.pickup.pickupIndex;
                    Log.Debug($"OnPing: shop terminal detected, pickupIndex={pickupIndex}");
                }
                else
                {
                    Log.Debug("OnPing: shop terminal is hidden or has no display, skipping.");
                }
            }
            else if (newPingInfo.targetGameObject.TryGetComponent<PickupDistributorBehavior>(out var tempShop))
            {
                if (!tempShop.hidden)
                {
                    pickupIndex = tempShop.pickup.pickupIndex;
                    Log.Debug($"OnPing: temp/printer shop detected, pickupIndex={pickupIndex}");
                }
                else
                {
                    Log.Debug("OnPing: temp shop is hidden, skipping.");
                }
            }
            else
            {
                Log.Debug($"OnPing: no recognized pickup component on '{newPingInfo.targetGameObject.name}' — skipping (not an item/shop).");
            }
#pragma warning restore Publicizer001

            if (pickupIndex == PickupIndex.none)
                return;

            PickupDef pickupDef = PickupCatalog.GetPickupDef(pickupIndex);
            if (pickupDef == null)
            {
                Log.Warning($"OnPing: PickupCatalog returned null for pickupIndex={pickupIndex}");
                return;
            }

            string chatMsg = BuildChatMessage(pickupDef, pingerMaster, pingerName);
            if (string.IsNullOrEmpty(chatMsg))
            {
                Log.Debug("OnPing: BuildChatMessage returned empty string, skipping.");
                return;
            }

            Log.Debug($"OnPing: broadcasting chat message:\n{chatMsg}");

            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "PINGDESC_FORMAT",
                paramTokens = [chatMsg]
            });

            Log.Info($"Broadcast ping description: '{pingerName}' pinged {Language.GetString(PickupCatalog.GetPickupDef(pickupIndex)?.nameToken ?? "")}");
        }

        private static string BuildChatMessage(PickupDef pickupDef, CharacterMaster pingerMaster, string pingerName)
        {
            string itemName;
            string description;

            if (pickupDef.itemIndex != ItemIndex.None)
            {
                ItemDef itemDef = ItemCatalog.GetItemDef(pickupDef.itemIndex);
                if (itemDef == null)
                {
                    Log.Warning($"BuildChatMessage: ItemCatalog returned null for itemIndex={pickupDef.itemIndex}");
                    return null;
                }
                itemName = Language.GetString(itemDef.nameToken);
                description = GetItemDescriptionText(itemDef, pingerMaster);
                Log.Debug($"BuildChatMessage: item='{itemName}'");
            }
            else if (pickupDef.equipmentIndex != EquipmentIndex.None)
            {
                EquipmentDef equipDef = EquipmentCatalog.GetEquipmentDef(pickupDef.equipmentIndex);
                if (equipDef == null)
                {
                    Log.Warning($"BuildChatMessage: EquipmentCatalog returned null for equipmentIndex={pickupDef.equipmentIndex}");
                    return null;
                }
                itemName = Language.GetString(equipDef.nameToken);
                description = Language.IsTokenInvalid(equipDef.descriptionToken)
                    ? Language.GetString(equipDef.pickupToken)
                    : Language.GetString(equipDef.descriptionToken);
                Log.Debug($"BuildChatMessage: equipment='{itemName}'");
            }
            else
            {
                // Lunar coins, drone indexes, etc. — not useful to broadcast
                Log.Debug("BuildChatMessage: pickup is not a standard item or equipment, skipping.");
                return null;
            }

            string tierColor = ColorUtility.ToHtmlStringRGB(pickupDef.baseColor);
            return $"<color=#f0e68c>[Ping]</color> {pingerName} → <color=#{tierColor}>{itemName}</color>: {CompactForChat(description)}";
        }

        private static string GetItemDescriptionText(ItemDef itemDef, CharacterMaster master)
        {
            if (_lgGetItemDescription != null)
            {
                try
                {
                    int stacks = master?.inventory?.GetItemCount(itemDef.itemIndex) ?? 1;
                    if (stacks < 1) stacks = 1;

                    string result = (string)_lgGetItemDescription.Invoke(
                        null,
                        new object[] { itemDef, stacks, master, false, false });

                    if (!string.IsNullOrEmpty(result))
                    {
                        Log.Debug($"GetItemDescriptionText: LookingGlass returned description (stacks={stacks})");
                        return result;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"GetItemDescriptionText: LookingGlass call failed — {e.Message}");
                }
            }

            // Vanilla fallback: use full description, or short pickup text if no full desc exists
            string fallback = Language.IsTokenInvalid(itemDef.descriptionToken)
                ? Language.GetString(itemDef.pickupToken)
                : Language.GetString(itemDef.descriptionToken);

            Log.Debug("GetItemDescriptionText: using vanilla description.");
            return fallback;
        }

        private static readonly Regex _tagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        // Strips rich-text tags then collapses multiline LookingGlass output into:
        //   "Vanilla description. [Stat1: val | Stat2: val]"
        private static string CompactForChat(string text)
        {
            string stripped = _tagRegex.Replace(text ?? string.Empty, string.Empty);
            string[] lines = stripped
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToArray();

            if (lines.Length == 0) return string.Empty;
            if (lines.Length == 1) return lines[0];

            string desc = lines[0];
            // string stats = string.Join(" | ", lines.Skip(1));
            return $"{desc}";// [{stats}]";
        }
    }
}
