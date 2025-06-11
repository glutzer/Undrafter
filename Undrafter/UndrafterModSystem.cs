using HarmonyLib;
using HtmlAgilityPack;
using Newtonsoft.Json;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;

namespace Undrafter;

public class ExtractedModInfoId
{
    public string modid;
    public string websiteid;
    public string? zipname;

    public ExtractedModInfoId()
    {
        websiteid ??= string.Empty;
        modid ??= string.Empty;
    }
}

public class UndrafterModSystem : ModSystem
{
    public static IServerNetworkChannel? ServerChannel { get; private set; }
    public static HashSet<string> ServerDraftedIds { get; } = new();
    public static Dictionary<string, ExtractedModInfoId> ServerModMappings { get; } = new();

    public IClientNetworkChannel? clientChannel;

    public static Harmony? Harmony { get; private set; }

    public static UndrafterModSystem? ClientInst { get; private set; }
    public ICoreAPI? Api { get; set; }

    public override double ExecuteOrder()
    {
        // This will StartPre before anything else and patch nothing else to run until the correct state is reached.
        return -double.MaxValue;
    }

    /// <summary>
    /// This will be executed twice, once with CancelModPhase = false, then again with CancelModPhase = true.
    /// Harmony should only exist once on the client and server.
    /// </summary>
    public override void StartPre(ICoreAPI api)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("Started");
        Console.ResetColor();

        Api = api;

        // Do not start on singleplayer.
        if (api.World is ServerMain main)
        {
            if (!main.IsDedicatedServer) return;

            bool[] onConnectingAllowed = main.GetField<bool[]>("PacketHandlingOnConnectingAllowed") ?? Array.Empty<bool>();
            // Fill with true, or maybe just custom packets.
            for (int i = 0; i < onConnectingAllowed.Length; i++)
            {
                onConnectingAllowed[i] = true;
            }
        }
        if (api.World is ClientMain mainClient)
        {
            if (mainClient.IsSingleplayer) return;
        }

        ClientInst = this;

        if (Patches.CancelModPhase == false)
        {
            Harmony ??= new Harmony("undrafter");
            Harmony.PatchAll();

            if (api is ICoreClientAPI)
            {
                Patches.CancelModPhase = true;
            }
        }
        else if (Patches.CancelModPhase == true) // This should not be called on the server.
        {
            Patches.CancelModPhase = false; // This is called on the second pass, it can now load all mods.
        }

        if (api.Side == EnumAppSide.Server)
        {
            ServerChannel = api.Network.RegisterChannel("undrafter") as IServerNetworkChannel;
            ServerChannel?.RegisterMessageType<DraftedModInfo>();
            ServerChannel?.RegisterMessageType<ModPacket>();
            ServerChannel?.RegisterMessageType<ModRequest>();
            ServerChannel?.SetMessageHandler<ModRequest>((player, packet) =>
            {
                string[] requests = packet.FileNames;
                string modsPath = GamePaths.DataPathMods;

                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"Sending {requests.Length} mods to {player.PlayerName}");
                Console.ResetColor();

                foreach (string file in requests)
                {
                    string filePath = Path.Combine(modsPath, file);
                    if (!File.Exists(filePath)) continue;

                    byte[] data = File.ReadAllBytes(filePath);
                    ModPacket modPacket = new()
                    {
                        FileName = file,
                        Data = data
                    };
                    ServerChannel?.SendPacket(modPacket, player);
                }

                // Finally, send the final identification packet, after all files have been requested.
                Patches.DoingPhase2Identification = true;
                ServerMain main = (ServerMain)api.World;

                try
                {
                    Packet_Server id = main.CallMethod<Packet_Server>("CreatePacketIdentification", player.HasPrivilege("controlserver"));
                    main.SendPacket(player, id);
                }
                finally
                {
                    Patches.DoingPhase2Identification = false;
                }
            });
        }
        else
        {
            clientChannel = api.Network.GetChannel("undrafter") as IClientNetworkChannel ?? api.Network.RegisterChannel("undrafter") as IClientNetworkChannel;
            clientChannel?.RegisterMessageType<DraftedModInfo>();
            clientChannel?.RegisterMessageType<ModPacket>();
            ServerChannel?.RegisterMessageType<ModRequest>();
        }

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("Finished undrafting pre");
        Console.ResetColor();
    }

    public static void HandleFullModPacket(ModPacket p)
    {
        if (p.Data.Length == 0) return;
        string modsPath = GamePaths.DataPathMods;
        string filePath = Path.Combine(modsPath, p.FileName);
        // Download file to mods folder if it's not there.
        if (!File.Exists(filePath))
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Writing {filePath} to mods");
            Console.ResetColor();
            File.WriteAllBytes(filePath, p.Data);
        }
    }

    public static void HandleDraftedModInfoClient(DraftedModInfo p)
    {
        if (p.draftedMods.Length == 0) return;

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("Downloading drafted mods");
        Console.ResetColor();

        List<string> requiredMods = new();

        foreach (DraftedMod mod in p.draftedMods)
        {
            try
            {
                // Check if mod folder contains this mod.
                string modsPath = GamePaths.DataPathMods;
                string filePath = Path.Combine(modsPath, mod.ZipName);
                if (File.Exists(filePath))
                {
                    continue;
                }

                requiredMods.Add(mod.ZipName);

                //List<WebsiteButtonInfo> info = DownloadZipButtonsScraper.GetZipButtonsAsync($"https://mods.vintagestory.at/show/mod/{mod.WebsiteId}#tab-files").GetAwaiter().GetResult();
                //foreach (WebsiteButtonInfo button in info)
                //{
                //    if (button.ButtonText == mod.ZipName)
                //    {
                //        string modsPath = GamePaths.DataPathMods;
                //        // Download file to mods folder if it's not there.
                //        string filePath = Path.Combine(modsPath, button.ButtonText);
                //        if (!File.Exists(filePath))
                //        {
                //            using HttpClient httpClient = new();

                //            Console.ForegroundColor = ConsoleColor.Blue;
                //            Console.WriteLine($"Attempting to download {button.Href} from db, {mod.WebsiteId}");
                //            Console.ResetColor();

                //            HttpResponseMessage response = httpClient.GetAsync(button.Href).GetAwaiter().GetResult();
                //            if (response.IsSuccessStatusCode)
                //            {
                //                byte[] fileBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                //                File.WriteAllBytes(filePath, fileBytes);
                //            }
                //        }
                //    }
                //}
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine(e);
                Console.ResetColor();
            }
        }

        // Send all required zips in ghetto packet.
        ModRequest request = new()
        {
            FileNames = requiredMods.ToArray()
        };

        byte[] data;
        using (MemoryStream memoryStream = new())
        {
            Serializer.Serialize(memoryStream, request);
            data = memoryStream.ToArray();
        }
        Packet_CustomPacket customPacket = new()
        {
            ChannelId = p.channelId,
            MessageId = 2
        };
        customPacket.SetData(data);

        ClientMain? main = (ClientMain)ClientInst?.Api?.World ?? null;

        main?.SendPacketClient(new Packet_Client
        {
            Id = 23,
            CustomPacket = customPacket
        });
    }

    public override void Start(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server)
        {
            if (api.World is ServerMain main)
            {
                if (!main.IsDedicatedServer) return;
            }

            CollectDraftedMods(api).GetAwaiter().GetResult();
            CollectDraftedModMappings();
        }
    }

    public static void CollectDraftedModMappings()
    {
        // Will first try to get the mapping from the config, then overwrite from modinfo.json files.

        string cachePath = GamePaths.Cache;

        // Get all modinfo.json files in this directory and subdirectories, ignoring case.
        IEnumerable<string> modInfoFiles = Directory.EnumerateFiles(cachePath, "modinfo.json", SearchOption.AllDirectories)
            .Where(file => file.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase));

        foreach (string file in modInfoFiles)
        {
            string json = File.ReadAllText(file);
            ExtractedModInfoId? modInfo = JsonConvert.DeserializeObject<ExtractedModInfoId>(json);
            if (modInfo == null) continue;

            // Add to mapping.
            ServerModMappings[modInfo.modid] = modInfo;

            // Get the parent directory of the file, remove everything past ".zip".
            string parentDirectory = Path.GetDirectoryName(file) ?? string.Empty;
            string zipName = Path.GetFileName(parentDirectory); // This should be the zip name.

            if (zipName.Contains(".zip"))
            {
                zipName = zipName[..(zipName.IndexOf(".zip") + 4)];
            }

            modInfo.zipname = zipName;
        }
    }

    /// <summary>
    /// On the server, collect every mod that's drafted.
    /// </summary>
    private static async Task CollectDraftedMods(ICoreAPI api)
    {
        using HttpClient client = new();
        foreach (Mod mod in api.ModLoader.Mods)
        {
            // This mod is not required for the client to download, skip it.
            if (!mod.Info.RequiredOnClient) continue;
            await CheckIfDrafted(mod.Info.ModID, client);
        }
    }

    private static async Task CheckIfDrafted(string modId, HttpClient client)
    {
        if (modId == "undrafter") return;

        string notFoundResponse = "{\"statuscode\":\"404\"}";
        string uri = $"https://mods.vintagestory.at/api/mod/{modId}";
        HttpResponseMessage res = await client.PostAsync(uri, null);
        string response = await res.Content.ReadAsStringAsync();
        if (response == notFoundResponse)
        {
            ServerDraftedIds.Add(modId);
        }
    }

    public override void Dispose()
    {
        if (Harmony != null)
        {
            Harmony.UnpatchAll(Harmony.Id);
            Harmony = null;
        }

        ClientInst = null;

        Patches.CancelModPhase = false;
    }
}

// Sent from the server to the client with all drafted mods.
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class DraftedModInfo
{
    public DraftedMod[] draftedMods;
    public int channelId;

    public static DraftedModInfo Create(List<Packet_ModId> list)
    {
        List<DraftedMod> draftedList = new();

        foreach (Packet_ModId packet in list)
        {
            if (!UndrafterModSystem.ServerDraftedIds.Contains(packet.Modid)) continue; // Not drafted.
            if (!UndrafterModSystem.ServerModMappings.TryGetValue(packet.Modid, out ExtractedModInfoId? info)) continue;

            DraftedMod draftedMod = new(info.websiteid, info?.zipname ?? "0.zip");
            draftedList.Add(draftedMod);
        }

        return new DraftedModInfo
        {
            draftedMods = draftedList.ToArray()
        };
    }

    public DraftedModInfo()
    {
        draftedMods ??= Array.Empty<DraftedMod>();
    }
}

// Client -> server mod request.
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ModRequest
{
    public string[] FileNames { get; set; }

    public ModRequest()
    {
        FileNames ??= Array.Empty<string>();
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class DraftedMod
{
    public string WebsiteId { get; set; }
    public string ZipName { get; set; }

    public DraftedMod(string websiteId, string zipName)
    {
        WebsiteId = websiteId;
        ZipName = zipName;
    }

    public DraftedMod()
    {
        WebsiteId ??= string.Empty;
        ZipName ??= string.Empty;
    }
}

public class WebsiteButtonInfo
{
    public string? ButtonText { get; set; }
    public string? Href { get; set; }
}

/// <summary>
/// Server -> client, full packet.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ModPacket
{
    public string FileName { get; set; }
    public byte[] Data { get; set; }

    public ModPacket()
    {
        FileName ??= string.Empty;
        Data ??= Array.Empty<byte>();
    }
}

public class DownloadZipButtonsScraper
{
    public static async Task<List<WebsiteButtonInfo>> GetZipButtonsAsync(string url)
    {
        List<WebsiteButtonInfo> result = new();
        using HttpClient httpClient = new();
        string html = await httpClient.GetStringAsync(url);

        HtmlDocument doc = new();
        doc.LoadHtml(html);

        HtmlNodeCollection nodes = doc.DocumentNode.SelectNodes("//a[contains(text(), '.zip')]");
        if (nodes != null)
        {
            foreach (HtmlNode? node in nodes)
            {
                string text = node.InnerText.Trim();
                if (text.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string href = node.GetAttributeValue("href", "");

                    result.Add(new WebsiteButtonInfo
                    {
                        ButtonText = text,
                        Href = "https://mods.vintagestory.at" + href
                    });
                }
            }
        }

        return result;
    }
}