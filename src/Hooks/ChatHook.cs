using System;
using HarmonyLib;
using ValheimJS.Core;

namespace ValheimJS.Hooks;

/// <summary>
/// Server-side chat capture point. <c>Chat.OnNewChatMessage</c> is the local-client path
/// (it dereferences the local player and an async permission check); a dedicated server
/// receives chat through the <c>ChatMessage</c> routed RPC instead.
/// Patches stay dumb: register + emit, JS decides.
///
/// A <b>prefix</b>, not a postfix: the server handler defers through an async permission
/// check and bails early when there is no local player, so a postfix could fire late or not
/// at all. The event describes the message arriving, which is what plugins care about.
/// </summary>
[ValheimJS.Scripting.Shared]
[HarmonyPatch(typeof(Chat), "RPC_ChatMessage")]
internal static class ChatHook
{
    [HarmonyPrefix]
    private static void Prefix(long sender, int type, string text)
    {
        try
        {
            ValheimBridge.RaiseChat(ChannelName((Talker.Type)type), sender, text);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[ChatHook] " + exception.Message);
        }
    }

    private static string ChannelName(Talker.Type channel) => channel switch
    {
        Talker.Type.Shout => "shout",
        Talker.Type.Whisper => "whisper",
        Talker.Type.Ping => "ping",
        _ => "normal"
    };
}
