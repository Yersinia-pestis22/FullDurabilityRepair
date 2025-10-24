using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("FullDurabilityRepair", "Yersinia Pestis", "1.15.4")]
    [Description("Repara objetos al 100% de durabilidad sin costo")]
    public class FullDurabilityRepair : RustPlugin
    {
        private const string permUse = "fulldurabilityrepair.use";
        private const string permAdmin = "fulldurabilityrepair.admin";
        private const string permVip = "fulldurabilityrepair.vip";
        private const string permNoRestriction = "fulldurabilityrepair.norestriction";

        private const string DataFileName = "FullDurabilityRepair_Data";

        private PluginConfig config;
        private Dictionary<string, PlayerRepairData> playerData = new Dictionary<string, PlayerRepairData>();
        private Dictionary<string, PlayerRepairData> legacyHashedData = new Dictionary<string, PlayerRepairData>();

        private object resetTimer;
        private object saveTimer;
        private object periodicSaveTimer;
        private bool dataDirty = false;

        #region Config
        private class PluginConfig
        {
            public int CooldownSeconds { get; set; } = 300;
            public int DailyLimit { get; set; } = 5;
            public int VipCooldownSeconds { get; set; } = 120;
            public int VipDailyLimit { get; set; } = 10;

            public string MessageCooldown { get; set; } = "<color=#ffff33>Debes esperar {time} antes de reparar nuevamente.</color>";
            public string MessageSuccess { get; set; } = "<color=#33ff33>Tu objeto ha sido reparado a su durabilidad máxima.</color>";
            public string MessageInvalid { get; set; } = "<color=#ffff33>Este objeto no se puede reparar.</color>";
            public string MessageRemaining { get; set; } = "<color=#33ccff>Te quedan {remaining} reparaciones para hoy.</color>";
            public string MessageNoPermission { get; set; } = "<color=#ff3333>No tienes permiso para usar esta función.</color>";
            public string MessageLimitReached { get; set; } = "<color=#ffff33>Has alcanzado tu límite diario de reparaciones.</color>";

            public bool EnableDailyReset { get; set; } = true;
            public int ResetIntervalSeconds { get; set; } = 86400;

            public bool AnonymizeIds { get; set; } = false;

            public int SaveIntervalSeconds { get; set; } = 300;
        }

        private class PlayerRepairData
        {
            public long LastResetTicks { get; set; } = DateTime.UtcNow.Ticks;
            public int DailyCount { get; set; } = 0;
            public long LastRepairTicks { get; set; } = 0;
        }

        private class StoredData
        {
            public bool Anonymized { get; set; }
            public Dictionary<string, PlayerRepairData> Records { get; set; } = new Dictionary<string, PlayerRepairData>();
            public Dictionary<string, string> HashLookup { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, PlayerRepairData> LegacyHashed { get; set; } = new Dictionary<string, PlayerRepairData>();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>();
                if (config == null) throw new Exception("Configuración nula; creando una nueva...");
            }
            catch
            {
                PrintError("Error al leer el archivo de configuración, creando uno nuevo...");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion

        #region Hooks
        void Init()
        {
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permAdmin, this);
            permission.RegisterPermission(permVip, this);
            permission.RegisterPermission(permNoRestriction, this);

            LoadDefaultMessages();

            LoadPlayerData();

            ScheduleDailyReset();

            SchedulePeriodicSave();
        }

        void Unload()
        {
            try
            {
                DestroyTimer(resetTimer);
                resetTimer = null;

                DestroyTimer(saveTimer);
                saveTimer = null;

                DestroyTimer(periodicSaveTimer);
                periodicSaveTimer = null;
            }
            catch (Exception ex)
            {
                PrintError($"Error destruyendo timers en Unload: {ex}");
            }

            SavePlayerData(true);
        }

        void OnNewSave(string filename)
        {
            PrintWarning(lang.GetMessage("WipeDetected", this));
            playerData.Clear();
            legacyHashedData.Clear();
            MarkDirtyAndScheduleSave();
        }

        object OnItemRepair(BasePlayer player, Item item) => HandleRepair(player, item);
        object OnItemRepair(Item item, BasePlayer player) => HandleRepair(player, item);
        object OnItemRefill(Item item, BasePlayer player) => HandleRepair(player, item);
        object OnItemRefill(BasePlayer player, Item item) => HandleRepair(player, item);
        #endregion

        #region Core
        private object HandleRepair(BasePlayer player, Item item)
        {
            try
            {
                if (player == null || item == null) return null;

                if (!permission.UserHasPermission(player.UserIDString, permUse) &&
                    !permission.UserHasPermission(player.UserIDString, permVip) &&
                    !permission.UserHasPermission(player.UserIDString, permNoRestriction))
                    return null;

                if (!item.hasCondition)
                {
                    player.ChatMessage(lang.GetMessage("InvalidMsg", this, player.UserIDString));
                    return false;
                }

                if (permission.UserHasPermission(player.UserIDString, permNoRestriction))
                {
                    RepairItem(item);
                    player.ChatMessage(lang.GetMessage("SuccessMsg", this, player.UserIDString));
                    return null;
                }

                bool isVip = permission.UserHasPermission(player.UserIDString, permVip);

                int cooldown = isVip ? config.VipCooldownSeconds : config.CooldownSeconds;
                int dailyLimit = isVip ? config.VipDailyLimit : config.DailyLimit;

                bool created;
                bool migrated;
                var data = GetOrCreatePlayerData(player.UserIDString, out created, out migrated);
                bool migrateOnly = created || migrated;

                if (config.EnableDailyReset && config.ResetIntervalSeconds > 0)
                {
                    var lastReset = new DateTime(data.LastResetTicks, DateTimeKind.Utc);
                    if ((DateTime.UtcNow - lastReset).TotalSeconds >= config.ResetIntervalSeconds)
                    {
                        data.DailyCount = 0;
                        data.LastResetTicks = DateTime.UtcNow.Ticks;
                    }
                }

                if (data.LastRepairTicks > 0)
                {
                    var lastRepair = new DateTime(data.LastRepairTicks, DateTimeKind.Utc);
                    var timePassed = (DateTime.UtcNow - lastRepair).TotalSeconds;
                    if (timePassed < cooldown)
                    {
                        double remaining = cooldown - timePassed;
                        string timeFormatted = FormatSecondsReadable((int)Math.Ceiling(remaining));
                        player.ChatMessage(lang.GetMessage("CooldownMsg", this, player.UserIDString).Replace("{time}", timeFormatted));
                        if (migrateOnly)
                            MarkDirtyAndScheduleSave();
                        return false;
                    }
                }

                if (dailyLimit > 0 && data.DailyCount >= dailyLimit)
                {
                    player.ChatMessage(lang.GetMessage("LimitReachedMsg", this, player.UserIDString));
                    if (migrateOnly)
                        MarkDirtyAndScheduleSave();
                    return false;
                }

                data.DailyCount++;

                RepairItem(item);
                player.ChatMessage(lang.GetMessage("SuccessMsg", this, player.UserIDString));

                if (dailyLimit > 0)
                {
                    int remainingUses = Math.Max(0, dailyLimit - data.DailyCount);
                    player.ChatMessage(lang.GetMessage("RemainingMsg", this, player.UserIDString).Replace("{remaining}", $"{remainingUses}"));
                }

                data.LastRepairTicks = DateTime.UtcNow.Ticks;

                MarkDirtyAndScheduleSave();

                return null;
            }
            catch (Exception ex)
            {
                PrintError($"HandleRepair error: {ex}");
                return null;
            }
        }

        private void RepairItem(Item item)
        {
            try
            {
                if (!item.hasCondition) return;

                float maxCond = 0f;

                var itemType = item.GetType();
                var maxCondProp = itemType.GetProperty("maxCondition");
                if (maxCondProp != null)
                {
                    var val = maxCondProp.GetValue(item);
                    if (val is float f) maxCond = f;
                    else if (val is double d) maxCond = (float)d;
                    else if (val is int i) maxCond = i;
                }

                if (maxCond <= 0f && item.info != null)
                {
                    try
                    {
                        var infoObj = item.info;
                        var infoType = infoObj.GetType();
                        var condProp = infoType.GetProperty("condition");
                        if (condProp != null)
                        {
                            var condObj = condProp.GetValue(infoObj);
                            if (condObj != null)
                            {
                                var condType = condObj.GetType();
                                var maxProp = condType.GetProperty("max");
                                if (maxProp != null)
                                {
                                    var maxVal = maxProp.GetValue(condObj);
                                    if (maxVal != null)
                                    {
                                        try
                                        {
                                            maxCond = Convert.ToSingle(maxVal);
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                if (maxCond <= 0f) maxCond = item.condition;

                item.condition = maxCond;
                item.MarkDirty();
            }
            catch (Exception ex)
            {
                PrintError($"RepairItem error: {ex.Message}");
            }
        }
        #endregion

        #region Daily Reset & Save Scheduling
        private void ScheduleDailyReset()
        {
            try
            {
                if (!config.EnableDailyReset || config.ResetIntervalSeconds <= 0)
                {
                    Puts(lang.GetMessage("AutoResetDisabled", this));
                    return;
                }

                float interval = (float)config.ResetIntervalSeconds;

                resetTimer = timer.Every(interval, ResetAllDailyCounts);
                Puts(lang.GetMessage("AutoResetEnabled", this).Replace("{seconds}", config.ResetIntervalSeconds.ToString()));
            }
            catch (Exception ex)
            {
                PrintError($"Error al configurar el reinicio automático: {ex.Message}");
            }
        }

        private void ResetAllDailyCounts()
        {
            foreach (var kv in playerData)
            {
                kv.Value.DailyCount = 0;
                kv.Value.LastResetTicks = DateTime.UtcNow.Ticks;
            }
            MarkDirtyAndScheduleSave();
            Puts(lang.GetMessage("AutoResetPerformed", this));
        }

        private void SchedulePeriodicSave()
        {
            try
            {
                DestroyTimer(periodicSaveTimer);
                periodicSaveTimer = null;

                if (config.SaveIntervalSeconds > 0)
                {
                    periodicSaveTimer = timer.Every((float)config.SaveIntervalSeconds, () =>
                    {
                        SavePlayerData();
                    });
                    Puts(lang.GetMessage("PeriodicSaveEnabled", this).Replace("{seconds}", config.SaveIntervalSeconds.ToString()));
                }
                else
                {
                    Puts(lang.GetMessage("PeriodicSaveDisabled", this));
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error al configurar guardado periódico: {ex.Message}");
            }
        }
        #endregion

        #region Commands
        [ChatCommand("fdrinfo")]
        private void CmdInfo(BasePlayer player, string command, string[] args)
        {
            bool migrated;
            var data = FindOrMigratePlayerData(player.UserIDString, out migrated);
            if (migrated)
                MarkDirtyAndScheduleSave();

            if (data == null)
            {
                player.ChatMessage(lang.GetMessage("NoRepairsToday", this, player.UserIDString));
                return;
            }

            bool isVip = permission.UserHasPermission(player.UserIDString, permVip);
            int dailyLimit = isVip ? config.VipDailyLimit : config.DailyLimit;

            if (dailyLimit == 0)
                player.ChatMessage(lang.GetMessage("UnlimitedRepairs", this, player.UserIDString));
            else
            {
                int remaining = Math.Max(0, dailyLimit - data.DailyCount);
                player.ChatMessage(lang.GetMessage("RemainingMsg", this, player.UserIDString).Replace("{remaining}", remaining.ToString()));
            }
        }

        [ChatCommand("fdradminreset")]
        private void CmdAdminReset(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            foreach (var key in playerData.Keys.ToList())
            {
                var entry = playerData[key];
                entry.DailyCount = 0;
                entry.LastResetTicks = DateTime.UtcNow.Ticks;
            }

            foreach (var key in legacyHashedData.Keys.ToList())
            {
                var entry = legacyHashedData[key];
                entry.DailyCount = 0;
                entry.LastResetTicks = DateTime.UtcNow.Ticks;
            }

            MarkDirtyAndScheduleSave();
            player.ChatMessage(lang.GetMessage("AdminResetSuccess", this, player.UserIDString));
        }

        [ChatCommand("fdrvipreset")]
        private void CmdVipReset(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            int resetCount = 0;
            foreach (var kv in playerData.ToList())
            {
                string userIdStr = kv.Key;
                var pData = kv.Value;
                if (permission.UserHasPermission(userIdStr, permVip))
                {
                    pData.DailyCount = 0;
                    pData.LastResetTicks = DateTime.UtcNow.Ticks;
                    resetCount++;
                }
            }

            foreach (var kv in legacyHashedData)
            {
                // Legacy anonymized entries without ID mapping are reset blindly until the player reconnects.
                kv.Value.DailyCount = 0;
                kv.Value.LastResetTicks = DateTime.UtcNow.Ticks;
            }

            MarkDirtyAndScheduleSave();
            player.ChatMessage(lang.GetMessage("VipResetSuccess", this, player.UserIDString).Replace("{count}", resetCount.ToString()));
        }

        [ChatCommand("fdradmininfo")]
        private void CmdAdminInfo(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            player.ChatMessage(lang.GetMessage("AdminCommands", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdAdminResetDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdVipResetDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdSetLimitDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdSetCooldownDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdSetVipLimitDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdSetVipCooldownDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("CmdReloadDesc", this, player.UserIDString));
            player.ChatMessage(lang.GetMessage("VipLimitInfo", this, player.UserIDString).Replace("{limit}", config.VipDailyLimit.ToString()));
            player.ChatMessage(lang.GetMessage("VipCooldownInfo", this, player.UserIDString).Replace("{seconds}", config.VipCooldownSeconds.ToString()));
            player.ChatMessage(lang.GetMessage("AnonymizeInfo", this, player.UserIDString).Replace("{value}", config.AnonymizeIds.ToString()));
            player.ChatMessage(lang.GetMessage("PeriodicSaveInfo", this, player.UserIDString).Replace("{value}", config.SaveIntervalSeconds > 0 ? config.SaveIntervalSeconds + "s" : "disabled"));
        }

        [ChatCommand("fdrsetlimit")]
        private void CmdSetDailyLimit(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            if (args.Length != 1 || !int.TryParse(args[0], out int limit))
            {
                player.ChatMessage(lang.GetMessage("UsageSetLimit", this, player.UserIDString));
                return;
            }

            config.DailyLimit = Math.Max(0, limit);
            SaveConfig();
            player.ChatMessage(lang.GetMessage("SetLimitSuccess", this, player.UserIDString).Replace("{limit}", config.DailyLimit.ToString()));
        }

        [ChatCommand("fdrcooldown")]
        private void CmdSetCooldown(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            if (args.Length != 1 || !int.TryParse(args[0], out int cd))
            {
                player.ChatMessage(lang.GetMessage("UsageSetCooldown", this, player.UserIDString));
                return;
            }

            config.CooldownSeconds = Math.Max(0, cd);
            SaveConfig();
            player.ChatMessage(lang.GetMessage("SetCooldownSuccess", this, player.UserIDString).Replace("{seconds}", config.CooldownSeconds.ToString()));
        }

        [ChatCommand("fdrviplimit")]
        private void CmdSetVipLimit(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            if (args.Length != 1 || !int.TryParse(args[0], out int limit))
            {
                player.ChatMessage(lang.GetMessage("UsageSetVipLimit", this, player.UserIDString));
                return;
            }

            config.VipDailyLimit = Math.Max(0, limit);
            SaveConfig();
            player.ChatMessage(lang.GetMessage("SetVipLimitSuccess", this, player.UserIDString).Replace("{limit}", config.VipDailyLimit.ToString()));
        }

        [ChatCommand("fdrvipcooldown")]
        private void CmdSetVipCooldown(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            if (args.Length != 1 || !int.TryParse(args[0], out int cd))
            {
                player.ChatMessage(lang.GetMessage("UsageSetVipCooldown", this, player.UserIDString));
                return;
            }

            config.VipCooldownSeconds = Math.Max(0, cd);
            SaveConfig();
            player.ChatMessage(lang.GetMessage("SetVipCooldownSuccess", this, player.UserIDString).Replace("{seconds}", config.VipCooldownSeconds.ToString()));
        }

        [ChatCommand("fdrreload")]
        private void CmdReloadConfig(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermMsg", this, player.UserIDString));
                return;
            }

            LoadConfig();
            ScheduleDailyReset();
            SchedulePeriodicSave();

            player.ChatMessage(lang.GetMessage("ConfigReloaded", this, player.UserIDString));
        }
        #endregion

        #region Persistencia
        private void LoadPlayerData()
        {
            playerData = new Dictionary<string, PlayerRepairData>();
            legacyHashedData = new Dictionary<string, PlayerRepairData>();
            bool needsResave = false;

            try
            {
                StoredData container = null;

                try
                {
                    container = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName);
                }
                catch
                {
                    container = null;
                }

                if (container != null && (container.Records?.Count > 0 || container.LegacyHashed?.Count > 0 || container.HashLookup?.Count > 0))
                {
                    if (container.Anonymized)
                    {
                        if (container.HashLookup != null && container.HashLookup.Count > 0)
                        {
                            foreach (var kv in container.Records)
                            {
                                if (container.HashLookup.TryGetValue(kv.Key, out var userId) && !string.IsNullOrEmpty(userId))
                                {
                                    playerData[userId] = kv.Value;
                                }
                                else
                                {
                                    legacyHashedData[kv.Key] = kv.Value;
                                }
                            }
                        }
                        else if (container.Records != null)
                        {
                            foreach (var kv in container.Records)
                            {
                                legacyHashedData[kv.Key] = kv.Value;
                            }
                            needsResave = true;
                        }
                    }
                    else if (container.Records != null)
                    {
                        foreach (var kv in container.Records)
                        {
                            playerData[kv.Key] = kv.Value;
                        }
                    }

                    if (container.LegacyHashed != null && container.LegacyHashed.Count > 0)
                    {
                        foreach (var kv in container.LegacyHashed)
                        {
                            legacyHashedData[kv.Key] = kv.Value;
                        }
                    }

                    if (container.Anonymized && (container.HashLookup == null || container.HashLookup.Count == 0) && legacyHashedData.Count > 0)
                    {
                        PrintWarning(lang.GetMessage("AnonymizedDataWarning", this));
                    }

                    if (needsResave)
                        MarkDirtyAndScheduleSave();

                    return;
                }

                var legacy = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PlayerRepairData>>(DataFileName);
                if (legacy == null || legacy.Count == 0)
                {
                    return;
                }

                bool keysLookHashed = legacy.Keys.All(IsLikelyHash);
                if (keysLookHashed)
                {
                    legacyHashedData = new Dictionary<string, PlayerRepairData>(legacy);
                    PrintWarning(lang.GetMessage("AnonymizedDataWarning", this));
                    needsResave = true;
                }
                else
                {
                    playerData = new Dictionary<string, PlayerRepairData>(legacy);
                    needsResave = true;
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error leyendo FullDurabilityRepair_Data: {ex.Message}. Se inicializarán datos vacíos en memoria.");
                playerData = new Dictionary<string, PlayerRepairData>();
                legacyHashedData = new Dictionary<string, PlayerRepairData>();
                return;
            }

            if (needsResave)
                MarkDirtyAndScheduleSave();
        }

        private void SavePlayerData(bool force = false)
        {
            try
            {
                if (!force && !dataDirty)
                    return;

                var stored = new StoredData();

                if (config != null && config.AnonymizeIds)
                {
                    stored.Anonymized = true;
                    foreach (var kv in playerData)
                    {
                        string hashed = HashString(kv.Key);
                        if (string.IsNullOrEmpty(hashed))
                            continue;

                        stored.Records[hashed] = kv.Value;
                        stored.HashLookup[hashed] = kv.Key;
                    }

                    if (legacyHashedData.Count > 0)
                    {
                        foreach (var kv in legacyHashedData)
                        {
                            stored.LegacyHashed[kv.Key] = kv.Value;
                        }
                    }
                }
                else
                {
                    stored.Anonymized = false;
                    foreach (var kv in playerData)
                    {
                        stored.Records[kv.Key] = kv.Value;
                    }

                    if (legacyHashedData.Count > 0)
                    {
                        foreach (var kv in legacyHashedData)
                        {
                            stored.LegacyHashed[kv.Key] = kv.Value;
                        }
                    }
                }

                Interface.Oxide.DataFileSystem.WriteObject(DataFileName, stored);

                dataDirty = false;
            }
            catch (Exception ex)
            {
                PrintError($"Error saving player data: {ex}");
            }
        }

        private void MarkDirtyAndScheduleSave()
        {
            dataDirty = true;
            if (saveTimer == null)
            {
                saveTimer = timer.Once(5f, () =>
                {
                    try
                    {
                        SavePlayerData();
                    }
                    finally
                    {
                        saveTimer = null;
                    }
                });
            }
        }

        private string FormatSecondsReadable(int seconds)
        {
            if (seconds < 60)
                return $"{seconds}s";

            var time = TimeSpan.FromSeconds(seconds);

            if (time.TotalHours < 1)
            {
                var partsUnderHour = new List<string> { $"{time.Minutes}m" };
                if (time.Seconds > 0)
                    partsUnderHour.Add($"{time.Seconds}s");
                return string.Join(" ", partsUnderHour);
            }

            if (time.TotalDays < 1)
            {
                var parts = new List<string> { $"{(int)time.TotalHours}h" };
                if (time.Minutes > 0)
                    parts.Add($"{time.Minutes}m");
                if (time.Seconds > 0)
                    parts.Add($"{time.Seconds}s");
                return string.Join(" ", parts);
            }

            var segments = new List<string> { $"{(int)time.TotalDays}d" };
            if (time.Hours > 0)
                segments.Add($"{time.Hours}h");
            if (time.Minutes > 0)
                segments.Add($"{time.Minutes}m");

            return string.Join(" ", segments);
        }

        private string HashString(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            try
            {
                using (var sha = SHA256.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    var hash = sha.ComputeHash(bytes);
                    var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    return hex;
                }
            }
            catch (Exception ex)
            {
                PrintError($"HashString error: {ex.Message}");
                return input;
            }
        }

        private PlayerRepairData GetOrCreatePlayerData(string userId, out bool created, out bool migrated)
        {
            created = false;
            var data = FindOrMigratePlayerData(userId, out migrated);
            if (data != null)
                return data;

            data = new PlayerRepairData
            {
                LastResetTicks = DateTime.UtcNow.Ticks,
                DailyCount = 0,
                LastRepairTicks = 0
            };
            playerData[userId] = data;
            created = true;
            return data;
        }

        private PlayerRepairData FindOrMigratePlayerData(string userId, out bool migrated)
        {
            migrated = false;
            if (string.IsNullOrEmpty(userId))
                return null;

            if (playerData.TryGetValue(userId, out var data))
                return data;

            var hashed = HashString(userId);
            if (!string.IsNullOrEmpty(hashed) && legacyHashedData.TryGetValue(hashed, out data))
            {
                legacyHashedData.Remove(hashed);
                playerData[userId] = data;
                migrated = true;
                return data;
            }

            return null;
        }

        private bool IsLikelyHash(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
        }

        private void DestroyTimer(object t)
        {
            if (t == null) return;
            try
            {
                var type = t.GetType();
                var method = type.GetMethod("Destroy") ?? type.GetMethod("Stop");
                if (method != null)
                    method.Invoke(t, null);
            }
            catch (Exception ex)
            {
                PrintError($"Error destruyendo timer: {ex}");
            }
        }
        #endregion

        private void LoadDefaultMessages()
        {
            var en = new Dictionary<string, string>
            {
                {"CooldownMsg", "You must wait {time} before repairing again."},
                {"SuccessMsg", "Your item has been repaired to full durability."},
                {"InvalidMsg", "This item cannot be repaired."},
                {"RemainingMsg", "You have {remaining} repairs remaining today."},
                {"NoPermMsg", "You do not have permission to use this command."},
                {"LimitReachedMsg", "You have reached your daily repair limit."},
                {"AdminResetSuccess", "Daily repair limits have been reset for all players."},
                {"VipResetSuccess", "Daily repair limits have been reset for {count} VIP players."},
                {"ConfigReloaded", "Configuration reloaded successfully."},
                {"NoRepairsToday", "You have no repairs recorded today."},
                {"UnlimitedRepairs", "You have unlimited repairs today."},
                {"AdminCommands", "Administrator commands:"},
                {"CmdAdminResetDesc", "/fdradminreset - Reset all daily limits."},
                {"CmdVipResetDesc", "/fdrvipreset - Reset VIP daily limits."},
                {"CmdSetLimitDesc", "/fdrsetlimit <number> - Change daily limit."},
                {"CmdSetCooldownDesc", "/fdrcooldown <seconds> - Change cooldown time."},
                {"CmdSetVipLimitDesc", "/fdrviplimit <number> - Change VIP daily limit."},
                {"CmdSetVipCooldownDesc", "/fdrvipcooldown <seconds> - Change VIP cooldown."},
                {"CmdReloadDesc", "/fdrreload - Reload configuration."},
                {"VipLimitInfo", "Current VIP daily limit: {limit}"},
                {"VipCooldownInfo", "Current VIP cooldown: {seconds} seconds"},
                {"AnonymizeInfo", "Anonymize IDs on persist: {value}"},
                {"PeriodicSaveInfo", "Periodic save: {value}"},
                {"UsageSetLimit", "Usage: /fdrsetlimit <number>"},
                {"UsageSetCooldown", "Usage: /fdrcooldown <seconds>"},
                {"UsageSetVipLimit", "Usage: /fdrviplimit <number>"},
                {"UsageSetVipCooldown", "Usage: /fdrvipcooldown <seconds>"},
                {"SetLimitSuccess", "Daily limit updated to {limit}"},
                {"SetCooldownSuccess", "Cooldown updated to {seconds} seconds"},
                {"SetVipLimitSuccess", "VIP daily limit updated to {limit}"},
                {"SetVipCooldownSuccess", "VIP cooldown updated to {seconds} seconds"},
                {"AutoResetDisabled", "FullDurabilityRepair: auto-reset disabled."},
                {"AutoResetEnabled", "FullDurabilityRepair: auto-reset enabled every {seconds} seconds."},
                {"PeriodicSaveEnabled", "FullDurabilityRepair: periodic save enabled every {seconds} seconds."},
                {"PeriodicSaveDisabled", "FullDurabilityRepair: periodic save disabled (SaveIntervalSeconds = 0)."},
                {"WipeDetected", "Wipe detected. Resetting all repair limits..."},
                {"AutoResetPerformed", "FullDurabilityRepair: daily counters have been reset automatically."},
                {"AnonymizedDataWarning", "FullDurabilityRepair: data file contains legacy anonymized IDs. Entries will migrate automatically as players reconnect."},
                {"AnonymizedDataMismatch", "FullDurabilityRepair: data file contains anonymized IDs but AnonymizeIds is disabled in config. Data will not be loaded."}
            };

            var es = new Dictionary<string, string>
            {
                {"CooldownMsg", "Debes esperar {time} antes de reparar nuevamente."},
                {"SuccessMsg", "Tu objeto ha sido reparado a su durabilidad máxima."},
                {"InvalidMsg", "Este objeto no se puede reparar."},
                {"RemainingMsg", "Te quedan {remaining} reparaciones para hoy."},
                {"NoPermMsg", "No tienes permiso para usar este comando."},
                {"LimitReachedMsg", "Has alcanzado tu límite diario de reparaciones."},
                {"AdminResetSuccess", "Se han restablecido los límites diarios para todos los jugadores."},
                {"VipResetSuccess", "Se han restablecido los límites diarios para {count} jugadores VIP."},
                {"ConfigReloaded", "Configuración recargada correctamente."},
                {"NoRepairsToday", "No tienes reparaciones registradas hoy."},
                {"UnlimitedRepairs", "Tienes reparaciones ilimitadas hoy."},
                {"AdminCommands", "Comandos de administrador:"},
                {"CmdAdminResetDesc", "/fdradminreset - Restablece todos los límites diarios."},
                {"CmdVipResetDesc", "/fdrvipreset - Restablece los límites diarios de los VIP."},
                {"CmdSetLimitDesc", "/fdrsetlimit <número> - Cambia el límite diario."},
                {"CmdSetCooldownDesc", "/fdrcooldown <segundos> - Cambia el tiempo de reutilización."},
                {"CmdSetVipLimitDesc", "/fdrviplimit <número> - Cambia el límite diario de VIP."},
                {"CmdSetVipCooldownDesc", "/fdrvipcooldown <segundos> - Cambia el tiempo de reutilización de VIP."},
                {"CmdReloadDesc", "/fdrreload - Recarga la configuración."},
                {"VipLimitInfo", "Límite diario VIP actual: {limit}"},
                {"VipCooldownInfo", "Tiempo de reutilización VIP actual: {seconds} segundos"},
                {"AnonymizeInfo", "Anonimizar IDs al persistir: {value}"},
                {"PeriodicSaveInfo", "Guardado periódico: {value}"},
                {"UsageSetLimit", "Uso: /fdrsetlimit <número>"},
                {"UsageSetCooldown", "Uso: /fdrcooldown <segundos>"},
                {"UsageSetVipLimit", "Uso: /fdrviplimit <número>"},
                {"UsageSetVipCooldown", "Uso: /fdrvipcooldown <segundos>"},
                {"SetLimitSuccess", "Límite diario actualizado a {limit}"},
                {"SetCooldownSuccess", "Tiempo de reutilización actualizado a {seconds} segundos"},
                {"SetVipLimitSuccess", "Límite diario VIP actualizado a {limit}"},
                {"SetVipCooldownSuccess", "Tiempo de reutilización VIP actualizado a {seconds} segundos"},
                {"AutoResetDisabled", "FullDurabilityRepair: reinicio automático desactivado."},
                {"AutoResetEnabled", "FullDurabilityRepair: reinicio automático activado cada {seconds} segundos."},
                {"PeriodicSaveEnabled", "FullDurabilityRepair: guardado periódico activado cada {seconds} segundos."},
                {"PeriodicSaveDisabled", "FullDurabilityRepair: guardado periódico desactivado (SaveIntervalSeconds = 0)."},
                {"WipeDetected", "Se detectó un wipe. Restableciendo todos los límites de reparación..."},
                {"AutoResetPerformed", "FullDurabilityRepair: los contadores diarios se reiniciaron automáticamente."},
                {"AnonymizedDataWarning", "FullDurabilityRepair: archivo de datos contiene IDs anonimizados heredados. Se migrarán automáticamente cuando los jugadores vuelvan a usar el plugin."},
                {"AnonymizedDataMismatch", "FullDurabilityRepair: archivo de datos contiene IDs anonimizados (hash) pero la configuración actual no habilita AnonymizeIds. No se cargarán los datos para evitar inconsistencias."}
            };

            var pt = new Dictionary<string, string>
            {
                {"CooldownMsg", "Você deve esperar {time} antes de reparar novamente."},
                {"SuccessMsg", "Seu item foi reparado para durabilidade máxima."},
                {"InvalidMsg", "Este item não pode ser reparado."},
                {"RemainingMsg", "Você tem {remaining} reparos restantes hoje."},
                {"NoPermMsg", "Você não tem permissão para usar este comando."},
                {"LimitReachedMsg", "Você atingiu seu limite diário de reparos."},
                {"AdminResetSuccess", "Os limites diários de reparo foram reiniciados para todos os jogadores."},
                {"VipResetSuccess", "Os limites diários de reparo foram reiniciados para {count} jogadores VIP."},
                {"ConfigReloaded", "Configuração recarregada com sucesso."},
                {"NoRepairsToday", "Você não tem reparos registrados hoje."},
                {"UnlimitedRepairs", "Você tem reparos ilimitados hoje."},
                {"AdminCommands", "Comandos de administrador:"},
                {"CmdAdminResetDesc", "/fdradminreset - Reinicia todos os limites diários."},
                {"CmdVipResetDesc", "/fdrvipreset - Reinicia os limites diários dos VIPs."},
                {"CmdSetLimitDesc", "/fdrsetlimit <número> - Altera o limite diário."},
                {"CmdSetCooldownDesc", "/fdrcooldown <segundos> - Altera o tempo de reutilização."},
                {"CmdSetVipLimitDesc", "/fdrviplimit <número> - Altera o limite diário dos VIPs."},
                {"CmdSetVipCooldownDesc", "/fdrvipcooldown <segundos> - Altera o tempo de reutilização dos VIPs."},
                {"CmdReloadDesc", "/fdrreload - Recarrega a configuração."},
                {"VipLimitInfo", "Limite diário VIP atual: {limit}"},
                {"VipCooldownInfo", "Tempo de reutilização VIP atual: {seconds} segundos"},
                {"AnonymizeInfo", "Anonimizar IDs ao persistir: {value}"},
                {"PeriodicSaveInfo", "Salvamento periódico: {value}"},
                {"UsageSetLimit", "Uso: /fdrsetlimit <número>"},
                {"UsageSetCooldown", "Uso: /fdrcooldown <segundos>"},
                {"UsageSetVipLimit", "Uso: /fdrviplimit <número>"},
                {"UsageSetVipCooldown", "Uso: /fdrvipcooldown <segundos>"},
                {"SetLimitSuccess", "Limite diário atualizado para {limit}"},
                {"SetCooldownSuccess", "Tempo de reutilização atualizado para {seconds} segundos"},
                {"SetVipLimitSuccess", "Limite diário VIP atualizado para {limit}"},
                {"SetVipCooldownSuccess", "Tempo de reutilização VIP atualizado para {seconds} segundos"},
                {"AutoResetDisabled", "FullDurabilityRepair: reinício automático desativado."},
                {"AutoResetEnabled", "FullDurabilityRepair: reinício automático ativado a cada {seconds} segundos."},
                {"PeriodicSaveEnabled", "FullDurabilityRepair: salvamento periódico ativado a cada {seconds} segundos."},
                {"PeriodicSaveDisabled", "FullDurabilityRepair: salvamento periódico desativado (SaveIntervalSeconds = 0)."},
                {"WipeDetected", "Wipe detectado. Reiniciando todos os limites de reparo..."},
                {"AutoResetPerformed", "FullDurabilityRepair: contadores diários foram reiniciados automaticamente."},
                {"AnonymizedDataWarning", "FullDurabilityRepair: arquivo de dados contém IDs anonimizados legados. As entradas serão migradas automaticamente quando os jogadores retornarem."},
                {"AnonymizedDataMismatch", "FullDurabilityRepair: arquivo de dados contém IDs anonimizados, mas AnonymizeIds está desativado na configuração. Os dados não serão carregados."}
            };

            lang.RegisterMessages(en, this, "en");
            lang.RegisterMessages(es, this, "es");
            lang.RegisterMessages(pt, this, "pt-BR");
            lang.RegisterMessages(en, this);
        }
    }
}