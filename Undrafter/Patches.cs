using HarmonyLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Undrafter;

public class Patches
{
    public static bool CancelModPhase { get; set; } = false;
    public static bool DoingPhase2Identification { get; set; } = false;

    [HarmonyPatch(typeof(ServerMain), "CreatePacketIdentification")]
    public class ServerMainPatch1
    {
        [HarmonyPostfix]
        public static void Postfix(ServerMain __instance, ref Packet_Server __result)
        {
            if (DoingPhase2Identification) return;

            IEnumerable<Mod> mods = __instance.Api.ModLoader.Mods;
            List<Packet_ModId> list = (from mod in mods
                                       where mod.Info.Side.IsUniversal()
                                       select new Packet_ModId
                                       {
                                           Modid = mod.Info.ModID,
                                           Name = mod.Info.Name,
                                           Networkversion = mod.Info.NetworkVersion,
                                           Version = mod.Info.Version,
                                           RequiredOnClient = mod.Info.RequiredOnClient
                                       }).ToList();

            // Do not report drafted mods.
            list.RemoveAll(m =>
            {
                return UndrafterModSystem.ServerDraftedIds.Contains(m.Modid);
            });

            __result.Identification.SetMods(list.ToArray());
        }
    }

    [HarmonyPatch(typeof(Vintagestory.Client.NoObf.NetworkAPI), "HandleCustomPacket")]
    public class NetworkAPIPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Packet_Server packet)
        {
            if (CancelModPhase)
            {
                // 1 packet will be received here.
                Packet_CustomPacket? custom = packet.CustomPacket;
                if (custom != null && custom.Data != null)
                {
                    if (custom.MessageId == 0)
                    {
                        using MemoryStream source = new(custom.Data);
                        DraftedModInfo draft = Serializer.Deserialize<DraftedModInfo>(source);

                        // Do something with drafted mod info.
                        UndrafterModSystem.HandleDraftedModInfoClient(draft);
                    }

                    if (custom.MessageId == 1)
                    {
                        using MemoryStream source = new(custom.Data);
                        ModPacket draft = Serializer.Deserialize<ModPacket>(source);

                        // Handle full packet from server.
                        UndrafterModSystem.HandleFullModPacket(draft);
                    }
                }

                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ServerMain), "SendServerIdentification")]
    public class ServerMainPatch2
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerMain __instance, ServerPlayer player)
        {
            Packet_Server withoutDrafted = __instance.CallMethod<Packet_Server>("CreatePacketIdentification", player.HasPrivilege("controlserver"));
            __instance.SendPacket(player, withoutDrafted);

            // After the packet without the drafted mods is sent, send the DRAFTED mod packet, which will download all the mods, then a new one will be sent.
            IEnumerable<Mod> mods = __instance.Api.ModLoader.Mods;
            List<Packet_ModId> list = (from mod in mods
                                       where mod.Info.Side.IsUniversal()
                                       select new Packet_ModId
                                       {
                                           Modid = mod.Info.ModID,
                                           Name = mod.Info.Name,
                                           Networkversion = mod.Info.NetworkVersion,
                                           Version = mod.Info.Version,
                                           RequiredOnClient = mod.Info.RequiredOnClient
                                       }).ToList();
            List<Packet_ModId> draftedMods = list.Where(m =>
            {
                return UndrafterModSystem.ServerDraftedIds.Contains(m.Modid);
            }).ToList();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"{draftedMods.Count} drafted mods sent to the client.");
            Console.ResetColor();

            DraftedModInfo info = DraftedModInfo.Create(draftedMods);
            info.channelId = UndrafterModSystem.ServerChannel?.GetField<int>("channelId") ?? 0;

            ConnectedClient client = player.GetField<ConnectedClient>("client");

            Vintagestory.API.Server.EnumClientState currentState = client.State;
            client.State = Vintagestory.API.Server.EnumClientState.Connected;
            UndrafterModSystem.ServerChannel?.SendPacket(info, player);
            client.State = currentState;

            // Do this after client request first.
            //doingPhase2Identification = true;
            //try
            //{
            //    Packet_Server id = __instance.CallMethod<Packet_Server>("CreatePacketIdentification", player.HasPrivilege("controlserver"));
            //    __instance.SendPacket(player, id);
            //}
            //finally
            //{
            //    doingPhase2Identification = false;
            //}

            return false;
        }
    }

    [HarmonyPatch(typeof(ModLoader), "TryRunModPhase")]
    public class TryRunModPhasePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result, Mod mod, ModSystem system, ICoreAPI api, ModRunPhase phase)
        {
            __result = true;
            return !CancelModPhase || system is UndrafterModSystem;
        }
    }

    [HarmonyPatch(typeof(SystemModHandler), "StartMods")]
    public class StartModsPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CancelModPhase;
        }
    }

    [HarmonyPatch(typeof(SystemModHandler), "ReloadExternalAssets")]
    public class AssetsPatch1
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CancelModPhase;
        }
    }

    [HarmonyPatch(typeof(ClientSystemStartup), "AfterAssetsLoaded")]
    public class AssetsPatch2
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CancelModPhase;
        }
    }

    [HarmonyPatch(typeof(GuiManager), "OnServerIdentificationReceived")]
    public class GuiPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CancelModPhase;
        }
    }
}