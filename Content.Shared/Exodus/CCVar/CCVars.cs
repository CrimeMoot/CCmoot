using Robust.Shared.Configuration;
using Robust.Shared;

namespace Content.Shared.Exodus.CCVars;

[CVarDefs]
public sealed class CCVarsExodus : CVars
{

    /// <summary>
    /// The URL of the webhook used to send ban messages to the Discord server.
    /// </summary>
    public static readonly CVarDef<string> DiscordServerBansWebhook =
        CVarDef.Create("discord.server_bans_webhook", string.Empty, CVar.SERVERONLY);
}