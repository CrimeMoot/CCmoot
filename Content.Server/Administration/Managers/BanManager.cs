using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Reflection.PortableExecutable;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Players;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Content.Server.Exodus.Discord.Webhooks;;

namespace Content.Server.Administration.Managers;

public sealed class BanManager : IBanManager, IPostInjectInit
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILocalizationManager _localizationManager = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly DiscordWebhook _discord = default!;
    //start  Exodus - 22.08.2024-banwebhook
    [Dependency] private readonly WebhookBans _webhookBans = default!;
    //end  Exodus - 22.08.2024-banwebhook

    private ISawmill _sawmill = default!;

    public const string SawmillId = "admin.bans";
    public const string JobPrefix = "Job:";

    private readonly Dictionary<NetUserId, HashSet<ServerRoleBanDef>> _cachedRoleBans = new();

    public void Initialize()
    {
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

        _netManager.RegisterNetMessage<MsgRoleBans>();
    }

    private async void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus != SessionStatus.Connected || _cachedRoleBans.ContainsKey(e.Session.UserId))
            return;

        var netChannel = e.Session.Channel;
        ImmutableArray<byte>? hwId = netChannel.UserData.HWId.Length == 0 ? null : netChannel.UserData.HWId;
        await CacheDbRoleBans(e.Session.UserId, netChannel.RemoteEndPoint.Address, hwId);

        SendRoleBans(e.Session);
    }

    private async Task<bool> AddRoleBan(ServerRoleBanDef banDef)
    {
        banDef = await _db.AddServerRoleBanAsync(banDef);

        if (banDef.UserId != null)
        {
            _cachedRoleBans.GetOrNew(banDef.UserId.Value).Add(banDef);
        }

        return true;
    }

    public HashSet<string>? GetRoleBans(NetUserId playerUserId)
    {
        return _cachedRoleBans.TryGetValue(playerUserId, out var roleBans)
            ? roleBans.Select(banDef => banDef.Role).ToHashSet()
            : null;
    }

    private async Task CacheDbRoleBans(NetUserId userId, IPAddress? address = null, ImmutableArray<byte>? hwId = null)
    {
        var roleBans = await _db.GetServerRoleBansAsync(address, userId, hwId, false);

        var userRoleBans = new HashSet<ServerRoleBanDef>();
        foreach (var ban in roleBans)
        {
            userRoleBans.Add(ban);
        }

        _cachedRoleBans[userId] = userRoleBans;
    }

    public void Restart()
    {
        // Clear out players that have disconnected.
        var toRemove = new List<NetUserId>();
        foreach (var player in _cachedRoleBans.Keys)
        {
            if (!_playerManager.TryGetSessionById(player, out _))
                toRemove.Add(player);
        }

        foreach (var player in toRemove)
        {
            _cachedRoleBans.Remove(player);
        }

        // Check for expired bans
        foreach (var roleBans in _cachedRoleBans.Values)
        {
            roleBans.RemoveWhere(ban => DateTimeOffset.Now > ban.ExpirationTime);
        }
    }

    #region Server Bans
    public async void CreateServerBan(NetUserId? target, string? targetUsername, NetUserId? banningAdmin, (IPAddress, int)? addressRange, ImmutableArray<byte>? hwid, uint? minutes, NoteSeverity severity, string reason)
    {
        //start  Exodus - 22.08.2024-banwebhook
        DateTimeOffset timeOfBan = DateTimeOffset.Now;
        //end  Exodus - 22.08.2024-banwebhook
        DateTimeOffset? expires = null;
        if (minutes > 0)
        {
            //start  Exodus - 22.08.2024-banwebhook
            expires = timeOfBan + TimeSpan.FromMinutes(minutes.Value);
            //end  Exodus - 22.08.2024-banwebhook
        }

        _systems.TryGetEntitySystem<GameTicker>(out var ticker);
        int? roundId = ticker == null || ticker.RoundId == 0 ? null : ticker.RoundId;
        var playtime = target == null ? TimeSpan.Zero : (await _db.GetPlayTimes(target.Value)).Find(p => p.Tracker == PlayTimeTrackingShared.TrackerOverall)?.TimeSpent ?? TimeSpan.Zero;

        var banDef = new ServerBanDef(
            null,
            target,
            addressRange,
            hwid,
            //start  Exodus - 22.08.2024-banwebhook
            timeOfBan,
            //end  Exodus - 22.08.2024-banwebhook
            expires,
            roundId,
            playtime,
            reason,
            severity,
            banningAdmin,
            null);

        await _db.AddServerBanAsync(banDef);
        var adminName = banningAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");
        var targetName = target is null ? "null" : $"{targetUsername} ({target})";
        var addressRangeString = addressRange != null
            ? $"{addressRange.Value.Item1}/{addressRange.Value.Item2}"
            : "null";
        var hwidString = hwid != null
            ? string.Concat(hwid.Value.Select(x => x.ToString("x2")))
            : "null";
        //start  Exodus - 22.08.2024-banwebhook
        var expiresString = expires == null ? Loc.GetString("server-ban-string-never") : 
            Loc.GetString("server-ban-string-expires", ("time", expires.Value.DateTime));
        //end  Exodus - 22.08.2024-banwebhook

        var key = _cfg.GetCVar(CCVars.AdminShowPIIOnBan) ? "server-ban-string" : "server-ban-string-no-pii";

        var logMessage = Loc.GetString(
            key,
            ("admin", adminName),
            ("severity", severity),
            ("expires", expiresString),
            ("name", targetName),
            ("ip", addressRangeString),
            ("hwid", hwidString),
            ("reason", reason));

        _sawmill.Info(logMessage);
        _chat.SendAdminAlert(logMessage);

        // Exodus-BanWebhook-Start
        if (!string.IsNullOrWhiteSpace(_cfg.GetCVar(CCVars.DiscordBanWebhook)))
        {
            _discord.TryGetWebhook(_cfg.GetCVar(CCVars.DiscordBanWebhook), async (banWebhook) =>
            {
                var ban = await _db.GetServerBanAsync(null, target, null);
                var targetUser = target == null ? null : await _db.GetPlayerRecordByUserId(target.Value);
                var discordMention = targetUser?.DiscordId != null ? $"<@!{targetUser.DiscordId}>" : "null";

                var description = $"> **ID раунда:** `{roundId}`\n\n"
                + $"> **Нарушитель:** `{targetUsername}` ({discordMention})\n> **Администратор:** `{adminName}`\n\n"
                + $"> **Выдан:** <t:{DateTimeOffset.Now.ToUnixTimeSeconds()}:R> ({DateTimeOffset.Now})\n\n";

                if (expires != null)
                {
                    description += $"**Истекает:** <t:{expires.Value.ToUnixTimeSeconds()}:R> ({expires.Value})\n";
                }
                if (reason != string.Empty)
                {
                    description += "**Причина:**\n > " + string.Join("\n> ", reason.Trim().Split("\n")) + "\n";
                }

                var payload = new WebhookPayload()
                {
                    Embeds = new() {
                    new() {
                        Title = minutes > 0 ? $"Бан #{ban?.Id} на {minutes} минут" : $"Перманентный бан #{ban?.Id}",
                        Description = description
                    }
                    }
                };
                await _discord.CreateMessage(banWebhook.ToIdentifier(), payload);
            });
        }
        // Exodus-BanWebhook-End

        // If we're not banning a player we don't care about disconnecting people
        if (target == null)
            return;

        // Is the player connected?
        if (!_playerManager.TryGetSessionById(target.Value, out var targetPlayer))
            return;
        // If they are, kick them
        var message = banDef.FormatBanMessage(_cfg, _localizationManager);
        targetPlayer.Channel.Disconnect(message);

        //start  Exodus - 22.08.2024-banwebhook
        var banDefDb = await _db.GetServerBanAsync(addressRange?.Item1, target, hwid);

        var banId = banDefDb?.Id.ToString() ?? "NotFound";
        // Sends a message about the ban to the discord server
        await _webhookBans.SendBanMessage(adminName, timeOfBan.DateTime.ToString(), expiresString, targetUsername ?? "null", reason, banId);
        //end  Exodus - 22.08.2024-banwebhook
    }
    #endregion

    #region Job Bans
    // If you are trying to remove timeOfBan, please don't. It's there because the note system groups role bans by time, reason and banning admin.
    // Removing it will clutter the note list. Please also make sure that department bans are applied to roles with the same DateTimeOffset.
    //start  Exodus - 22.08.2024-banwebhook
    public async void CreateRoleBan(NetUserId? target, string? targetUsername, NetUserId? banningAdmin, (IPAddress, int)? addressRange, ImmutableArray<byte>? hwid, string role, uint? minutes, NoteSeverity severity, string reason, DateTimeOffset timeOfBan, bool skipWebhook = false)
    //end  Exodus - 22.08.2024-banwebhook
    {
        if (!_prototypeManager.TryIndex(role, out JobPrototype? _))
        {
            throw new ArgumentException($"Invalid role '{role}'", nameof(role));
        }

        //start  Exodus - 22.08.2024-banwebhook
        var roleJobPrefix = string.Concat(JobPrefix, role);
        //end  Exodus - 22.08.2024-banwebhook
        DateTimeOffset? expires = null;
        if (minutes > 0)
        {
            //start  Exodus - 22.08.2024-banwebhook
            expires = timeOfBan + TimeSpan.FromMinutes(minutes.Value);
            //end  Exodus - 22.08.2024-banwebhook
        }

        _systems.TryGetEntitySystem(out GameTicker? ticker);
        int? roundId = ticker == null || ticker.RoundId == 0 ? null : ticker.RoundId;
        var playtime = target == null ? TimeSpan.Zero : (await _db.GetPlayTimes(target.Value)).Find(p => p.Tracker == PlayTimeTrackingShared.TrackerOverall)?.TimeSpent ?? TimeSpan.Zero;

        var banDef = new ServerRoleBanDef(
            null,
            target,
            addressRange,
            hwid,
            timeOfBan,
            expires,
            roundId,
            playtime,
            reason,
            severity,
            banningAdmin,
            null,
            //start  Exodus - 22.08.2024-banwebhook
            roleJobPrefix);
            //end  Exodus - 22.08.2024-banwebhook

        if (!await AddRoleBan(banDef))
        {
            //start  Exodus - 22.08.2024-banwebhook
            _chat.SendAdminAlert(Loc.GetString("cmd-roleban-existing", ("target", targetUsername ?? "null"), ("role", roleJobPrefix)));
            //end  Exodus - 22.08.2024-banwebhook
            return;
        }

        //start  Exodus - 22.08.2024-banwebhook
        var length = expires == null
            ? Loc.GetString("cmd-roleban-inf")
            : Loc.GetString("cmd-roleban-until", ("expires", expires.Value.DateTime));

        _chat.SendAdminAlert(Loc.GetString("cmd-roleban-success", ("target", targetUsername ?? "null"), ("role", roleJobPrefix), ("reason", reason), ("length", length)));
        //end  Exodus - 22.08.2024-banwebhook

        if (target != null)
        {
            SendRoleBans(target.Value);
        }

        //start  Exodus - 22.08.2024-banwebhook
        if (!skipWebhook)
        {
            var adminName = banningAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(banningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");

            var localizedNameJob = _prototypeManager.Index<JobPrototype>(role).LocalizedName;

            var username = targetUsername ?? "null";

            await _webhookBans.SendBanRoleMessage(adminName, timeOfBan.DateTime.ToString(), length, localizedNameJob, username, reason);
        }
    }

    public async void CreateDepartmentBan(NetUserId? target, string? targetUsername, NetUserId? banningAdmin, (IPAddress, int)? addressRange, ImmutableArray<byte>? hwid, DepartmentPrototype departmentProto, uint? minutes, NoteSeverity severity, string reason, DateTimeOffset timeOfBan)
    {

        foreach (var job in departmentProto.Roles)
        {
            CreateRoleBan(target, targetUsername, banningAdmin, addressRange, hwid, job, minutes, severity, reason, timeOfBan, true);
        }

        var departmentName = Loc.GetString($"department-{departmentProto.ID}");

        var adminName = banningAdmin == null
        ? Loc.GetString("system-user")
        : (await _db.GetPlayerRecordByUserId(banningAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");

        DateTimeOffset? expires = null;
        if (minutes > 0)
        {
            expires = timeOfBan + TimeSpan.FromMinutes(minutes.Value);
        }

        var length = expires == null
            ? Loc.GetString("cmd-roleban-inf")
            : Loc.GetString("cmd-roleban-until", ("expires", expires.Value.DateTime));

        await _webhookBans.SendBanRoleMessage(adminName, timeOfBan.DateTime.ToString(), length, departmentName, targetUsername ?? "null", reason);
        //end  Exodus - 22.08.2024-banwebhook

    }

    public async Task<string> PardonRoleBan(int banId, NetUserId? unbanningAdmin, DateTimeOffset unbanTime)
    {
        var ban = await _db.GetServerRoleBanAsync(banId);

        if (ban == null)
        {
            return $"No ban found with id {banId}";
        }

        if (ban.Unban != null)
        {
            var response = new StringBuilder("This ban has already been pardoned");

            if (ban.Unban.UnbanningAdmin != null)
            {
                response.Append($" by {ban.Unban.UnbanningAdmin.Value}");
            }

            response.Append($" in {ban.Unban.UnbanTime}.");
            return response.ToString();
        }

        await _db.AddServerRoleUnbanAsync(new ServerRoleUnbanDef(banId, unbanningAdmin, DateTimeOffset.Now));

        if (ban.UserId is { } player && _cachedRoleBans.TryGetValue(player, out var roleBans))
        {
            roleBans.RemoveWhere(roleBan => roleBan.Id == ban.Id);
            SendRoleBans(player);
        }

        return $"Pardoned ban with id {banId}";
    }

    public HashSet<ProtoId<JobPrototype>>? GetJobBans(NetUserId playerUserId)
    {
        if (!_cachedRoleBans.TryGetValue(playerUserId, out var roleBans))
            return null;
        return roleBans
            .Where(ban => ban.Role.StartsWith(JobPrefix, StringComparison.Ordinal))
            .Select(ban => new ProtoId<JobPrototype>(ban.Role[JobPrefix.Length..]))
            .ToHashSet();
    }
    #endregion

    public void SendRoleBans(NetUserId userId)
    {
        if (!_playerManager.TryGetSessionById(userId, out var player))
        {
            return;
        }

        SendRoleBans(player);
    }

    public void SendRoleBans(ICommonSession pSession)
    {
        var roleBans = _cachedRoleBans.GetValueOrDefault(pSession.UserId) ?? new HashSet<ServerRoleBanDef>();
        var bans = new MsgRoleBans()
        {
            Bans = roleBans.Select(o => o.Role).ToList()
        };

        _sawmill.Debug($"Sent rolebans to {pSession.Name}");
        _netManager.ServerSendMessage(bans, pSession.Channel);
    }

    public void PostInject()
    {
        _sawmill = _logManager.GetSawmill(SawmillId);
    }
}
