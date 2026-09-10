using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Splatform;
using Steamworks;
using UnityEngine;

// Authored by Jere Kuusela <https://github.com/JereKuusela>
// https://github.com/JereKuusela/valheim-expand_world_prefabs/blob/main/ExpandWorldPrefabs/service/ServerClient.cs (public domain)

namespace WebMap
{
    public class ServerClient
    {
        public static ZNet.PlayerInfo Client => client ??= CreatePlayerInfo();
        private static ZNet.PlayerInfo? client;

        // Server client is only sent to clients, so this is needed for the server to recognize it.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.TryGetPlayerByPlatformUserID))]
        public class RecognizeServerClient
        {
            static bool Postfix(bool result, PlatformUserID platformUserID, ref ZNet.PlayerInfo playerInfo)
            {
                if (result) return result;
                if (platformUserID != Client.m_userInfo.m_id) return result;

                playerInfo = Client;
                return true;
            }
        }

        // Valheim l-1.0.7 moved the body of SendPlayerList into a new private
        // ZNet.WritePlayerInfo(List<PlayerInfo>), which SendPlayerList now only
        // calls before forwarding the package to peers, so the patch follows it.
        [HarmonyPatch(typeof(ZNet), "WritePlayerInfo")]
        public class AddExtraPlayer
        {
            // Hand-rolled rather than using HarmonyLib.CodeMatcher: CodeMatcher's
            // constructor takes an optional ILGenerator, and resolving that type
            // needs an mscorlib the netstandard2.1 reference set does not provide
            // (CS7069).
            //
            // AddServer rewrites the player count at position 0 and then appends
            // the server entry at the current write position, so it must run at
            // the point where the count is the only thing written so far:
            // immediately after WritePlayerInfo's `zPackage.Write(m_players.Count)`
            // and before the per-player loop, which then keeps writing after the
            // server entry. The old match (the last `ldfld ZNet.m_players`) landed
            // before the ZPackage local even exists in this method.
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var writeInt = AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(int) });

                var index = -1;
                for (var i = 0; i < codes.Count; i++)
                {
                    if ((codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call)
                        && Equals(codes[i].operand, writeInt))
                    {
                        index = i;
                        break;
                    }
                }

                // Leave the method untouched if the expected shape is gone, so a
                // future Valheim build degrades to "no server in the player list"
                // instead of emitting corrupt IL or corrupting the package.
                if (index < 0)
                {
                    ZLog.LogWarning("WebMap: WritePlayerInfo shape not recognised; server client not added.");
                    return codes;
                }

                codes.InsertRange(index + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AddExtraPlayer), nameof(AddServer))),
                });
                return codes;
            }

            static void AddServer(ZNet net, ZPackage pkg)
            {
                // This is needed in case multiple mods are adding extra players.
                var prev = pkg.GetPos();
                pkg.SetPos(0);
                if (IsExtraPlayerAdded(net, pkg.ReadInt()))
                {
                  pkg.SetPos(prev);
                }
                else
                {
                  pkg.SetPos(0);
                  pkg.Write(net.m_players.Count + 1);
                  Write(pkg);
                }
            }

            static bool IsExtraPlayerAdded(ZNet net, int count) => count >= net.m_players.Count + 1;
        }

        private static ZNet.PlayerInfo CreatePlayerInfo() => new()
        {
            m_name = "Server",
            // Receiving chat messages requires a valid character ID.
            m_characterID = new ZDOID(ZDOMan.GetSessionID(), uint.MaxValue),
            // m_serverAssignedDisplayName moved from PlayerInfo into
            // CrossNetworkUserInfo in Valheim l-1.0.7.
            m_userInfo = new() { m_id = new(ZNet.instance.m_steamPlatform, GetId()), m_displayName = "Server", m_serverAssignedDisplayName = "Server", m_playfabId = "" },
            m_publicPosition = false,
            m_position = Vector3.zero,
        };

        private static string GetId()
        {
            try
            {
                return SteamGameServer.GetSteamID().ToString();
            }
            catch (InvalidOperationException)
            {
                return "0";
            }
        }

        public static void Write(ZPackage pkg)
        {
            // Delegate to the game's own PlayerInfo serializer rather than
            // hand-writing the field order. The previous hand-rolled version
            // silently encoded the pre-l-1.0.7 layout, and PlayerInfo has since
            // moved a field into CrossNetworkUserInfo. Using ZNet's own writer
            // keeps this correct across future layout changes by construction.
            // m_publicPosition is false on Client, so no position is emitted.
            Client.Write(pkg.m_writer);
        }
    }
}
