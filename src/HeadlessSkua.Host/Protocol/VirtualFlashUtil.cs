using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Servers;

namespace HeadlessSkua.Protocol;

/// <summary>
/// Implements Skua's IFlashUtil interface completely in memory, mapping Flash object bindings
/// and ExternalInterface calls directly to the ProtocolGameState and SFS TCP socket.
/// </summary>
public sealed class VirtualFlashUtil : IFlashUtil
{
    private readonly AqwProtocolSession _session;
    private readonly ConcurrentDictionary<int, object?> _trackedObjects = new();
    private readonly ConcurrentDictionary<int, long> _skillLastUsed = new();
    private long _lastGlobalSkillTime;
    private long _lastAutoAttackTime;
    private int _actionCounter;
    private int _nextObjectId = 100;

    public event FlashCallHandler? FlashCall;
    public event Action<string>? DiagnosticLogged;

    public VirtualFlashUtil(AqwProtocolSession session)
    {
        _session = session;
        _session.FlashEventReceived += OnSessionFlashEvent;
    }

    private void OnSessionFlashEvent(string name, object[] args)
    {
        DiagnosticLogged?.Invoke($"[Protocol] Event: {name}");
        FlashCall?.Invoke(name, args);
    }

    public void InitializeFlash()
    {
        // Pure protocol requires no external Flash player initialization.
    }

    public string? Call(string function, params object[] args)
    {
        object? result = Call(function, typeof(string), args);
        return result as string;
    }

    public T? Call<T>(string function, params object[] args)
    {
        object? result = Call(function, typeof(T), args);
        return result is T typed ? typed : default;
    }

    public object? Call(string function, Type type, params object[] args)
    {
        var state = _session.State;

        // Session & status queries
        if (function == "isLoggedIn")
            return ConvertValue(state.IsLoggedIn, type);

        if (function == "isKicked")
            return ConvertValue(state.IsKicked, type);

        if (function == "isNull" && args.Length > 0 && args[0] is string nullPath)
            return ConvertValue(IsNull(nullPath), type);

        // RemoteRegistry emulation (FlashObject and FlashArray)
        if (function == "lnkCreate" && args.Length > 0)
        {
            string path = args[0]?.ToString() ?? string.Empty;
            int id = Interlocked.Increment(ref _nextObjectId);
            _trackedObjects[id] = ResolvePathObject(path);
            return ConvertValue(id, type);
        }

        if (function == "lnkGetChild" && args.Length >= 2)
        {
            int parentId = Convert.ToInt32(args[0]);
            string childPath = args[1]?.ToString() ?? string.Empty;
            int id = Interlocked.Increment(ref _nextObjectId);
            object? parent = _trackedObjects.TryGetValue(parentId, out var p) ? p : null;
            _trackedObjects[id] = ResolveChildObject(parent, childPath);
            return ConvertValue(id, type);
        }

        if (function == "lnkGetValue" && args.Length > 0)
        {
            int id = Convert.ToInt32(args[0]);
            object? val = _trackedObjects.TryGetValue(id, out var obj) ? obj : null;
            string json = JsonConvert.SerializeObject(val);
            return ConvertValue(json, type);
        }

        if (function == "lnkSetValue" && args.Length >= 2)
        {
            int id = Convert.ToInt32(args[0]);
            _trackedObjects[id] = args[1];
            return ConvertValue(true, type);
        }

        if (function == "lnkGetArray" && args.Length >= 2)
        {
            int arrayId = Convert.ToInt32(args[0]);
            int index = Convert.ToInt32(args[1]);
            int id = Interlocked.Increment(ref _nextObjectId);
            object? array = _trackedObjects.TryGetValue(arrayId, out var arr) ? arr : null;
            if (array is IList list && index >= 0 && index < list.Count)
                _trackedObjects[id] = list[index];
            else
                _trackedObjects[id] = null;
            return ConvertValue(id, type);
        }

        if (function == "lnkSetArray" && args.Length >= 3)
        {
            int arrayId = Convert.ToInt32(args[0]);
            int index = Convert.ToInt32(args[1]);
            if (_trackedObjects.TryGetValue(arrayId, out var arr) && arr is IList list && index >= 0 && index < list.Count)
                list[index] = args[2];
            return ConvertValue(true, type);
        }

        if (function == "lnkDestroy" && args.Length > 0)
        {
            int id = Convert.ToInt32(args[0]);
            _trackedObjects.TryRemove(id, out _);
            return ConvertValue(true, type);
        }

        // Outbound packet transmission and game functions
        if (function == "callGameFunction" && args.Length >= 2)
        {
            string targetFunc = args[0] as string ?? string.Empty;

            if (targetFunc.StartsWith("sfc.send", StringComparison.OrdinalIgnoreCase) && args[1] is string packet)
            {
                _ = _session.SendPacketAsync(packet);
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.aggroAllMon")
            {
                var mapMonIds = state.MapMonsters.Select(m => m.MapID.ToString());
                _ = _session.SendPacketAsync($"%xt%zm%aggroMon%{state.RoomID}%{string.Join("%", mapMonIds)}%");
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.showQuests")
            {
                string ids = args[1]?.ToString() ?? string.Empty;
                _ = _session.SendPacketAsync($"%xt%zm%getQuests%{state.RoomID}%{ids}%");
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.acceptQuest")
            {
                int questId = Convert.ToInt32(args[1]);
                _ = _session.SendPacketAsync($"%xt%zm%acceptQuest%{state.RoomID}%{questId}%");
                state.ActiveQuests.TryAdd(questId, 1);
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.tryQuestComplete")
            {
                int questId = Convert.ToInt32(args[1]);
                int itemId = args.Length > 2 ? Convert.ToInt32(args[2]) : -1;
                bool special = args.Length > 3 && Convert.ToBoolean(args[3]);
                _ = _session.SendPacketAsync($"%xt%zm%tryQuestComplete%{state.RoomID}%{questId}%{itemId}%{special.ToString().ToLowerInvariant()}%1%wvz%");
                state.ActiveQuests.TryRemove(questId, out _);
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.getMapItem")
            {
                int itemId = Convert.ToInt32(args[1]);
                _ = _session.SendPacketAsync($"%xt%zm%getMapItem%{state.RoomID}%{itemId}%");
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.sendLoadShopRequest")
            {
                int shopId = Convert.ToInt32(args[1]);
                _ = _session.SendPacketAsync($"%xt%zm%loadShop%{state.RoomID}%{shopId}%");
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.sendEquipItemRequest")
            {
                int itemId = 0;
                if (args[1] is int idVal) itemId = idVal;
                else if (args[1] is JObject jObj) itemId = jObj["ItemID"]?.Value<int>() ?? 0;
                else if (args[1] is IDictionary<string, object> dict && dict.TryGetValue("ItemID", out var dictId)) itemId = Convert.ToInt32(dictId);

                if (itemId > 0)
                {
                    _ = _session.SendPacketAsync($"%xt%zm%equipItem%{state.RoomID}%{itemId}%");
                    var item = state.Inventory.Find(i => i.ID == itemId);
                    if (item is not null)
                        item.Equipped = true;
                }
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.equipUseableItem")
            {
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.goto")
            {
                string target = args[1]?.ToString() ?? string.Empty;
                _ = _session.SendPacketAsync($"%xt%zm%cmd%{state.RoomID}%goto%{target}%");
                return ConvertValue(true, type);
            }

            if (targetFunc == "world.isQuestInProgress")
            {
                int questId = Convert.ToInt32(args[1]);
                bool inProgress = state.ActiveQuests.ContainsKey(questId);
                return ConvertValue(inProgress, type);
            }

            if (targetFunc == "world.getAchievement" && args.Length >= 3)
            {
                string field = args[1]?.ToString()?.ToLowerInvariant() ?? string.Empty;
                int index = Convert.ToInt32(args[2]);
                if (state.Achievements.TryGetValue(field, out int bitmask))
                {
                    int val = (bitmask & (1 << index)) != 0 ? 1 : 0;
                    return ConvertValue(val, type);
                }
                return ConvertValue(0, type);
            }

            if (targetFunc == "world.getAchievement" || targetFunc == "world.getQuestValue")
            {
                return ConvertValue(0, type);
            }

            if (targetFunc == "world.myAvatar.getCPByID" || targetFunc == "world.myAvatar.getRep")
            {
                return ConvertValue(100000, type);
            }

            if (targetFunc == "world.maximumQuestTurnIns")
            {
                return ConvertValue(1, type);
            }
        }

        if (function == "callGameFunction0" && args.Length > 0 && args[0] is string gameFunc0)
        {
            if (gameFunc0 == "world.myAvatar.pMC.artLoaded")
                return ConvertValue(true, type);

            if (gameFunc0 == "world.rest")
            {
                _ = _session.SendPacketAsync($"%xt%zm%rest:hp%{state.RoomID}%");
                _ = _session.SendPacketAsync($"%xt%zm%rest:mp%{state.RoomID}%");
                return ConvertValue(true, type);
            }

            if (gameFunc0 == "world.toggleBank")
            {
                if (state.Bank.Count == 0)
                    _ = _session.SendPacketAsync($"%xt%zm%loadBank%{state.RoomID}%All%");
                return ConvertValue(true, type);
            }

            if (gameFunc0 == "world.reloadCurrentMap")
            {
                _ = _session.SendPacketAsync($"%xt%zm%cmd%{state.RoomID}%tfer%{state.Username}%{state.MapName}%{state.Cell}%{state.Pad}%");
                return ConvertValue(true, type);
            }

            if (gameFunc0 == "sfc.disconnect")
            {
                _ = _session.Client.DisposeAsync();
                return ConvertValue(true, type);
            }
        }

        // Gameplay actions
        if ((function == "jumpCorrectRoom" || function == "world.moveToCell" || function == "moveToCell") && args.Length >= 2)
        {
            string cell = args[0]?.ToString() ?? "Enter";
            string pad = args[1]?.ToString() ?? "Spawn";
            state.Cell = cell;
            state.Pad = pad;
            _ = _session.SendPacketAsync($"%xt%zm%moveToCell%{state.RoomID}%{cell}%{pad}%");
            return ConvertValue(true, type);
        }

        if (function == "walkTo")
        {
            return ConvertValue(true, type);
        }

        if (function == "attackMonsterName" && args.Length > 0 && args[0] is string monName)
        {
            var candidates = state.MapMonsters
                .Where(m => string.Equals(m.Cell, state.Cell, StringComparison.OrdinalIgnoreCase) && m.HP > 0);

            string search = monName.Trim();
            var mon = search == "*"
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(m => m.Name.Trim().Equals(search, StringComparison.OrdinalIgnoreCase))
                  ?? candidates.FirstOrDefault(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

            if (mon is not null)
            {
                bool targetChanged = state.TargetMonster != mon;
                state.TargetMonster = mon;
                long now = Environment.TickCount64;
                if (targetChanged || (now - _lastAutoAttackTime >= 1500))
                {
                    _lastAutoAttackTime = now;
                    _skillLastUsed[0] = now;
                    int actId = Interlocked.Increment(ref _actionCounter);
                    _ = _session.SendPacketAsync($"%xt%zm%gar%1%{actId}%aa>m:{mon.MapID}%wvz%");
                }
                return ConvertValue(true, type);
            }
            return ConvertValue(false, type);
        }

        if (function == "attackMonsterID" && args.Length > 0)
        {
            int monId = Convert.ToInt32(args[0]);
            var mon = state.MapMonsters.FirstOrDefault(m => (m.MapID == monId || m.ID == monId) && m.HP > 0);
            if (mon is not null)
            {
                bool targetChanged = state.TargetMonster != mon;
                state.TargetMonster = mon;
                long now = Environment.TickCount64;
                if (targetChanged || (now - _lastAutoAttackTime >= 1500))
                {
                    _lastAutoAttackTime = now;
                    _skillLastUsed[0] = now;
                    int actId = Interlocked.Increment(ref _actionCounter);
                    _ = _session.SendPacketAsync($"%xt%zm%gar%1%{actId}%aa>m:{mon.MapID}%wvz%");
                }
                return ConvertValue(true, type);
            }
            return ConvertValue(false, type);
        }

        if (function == "attackPlayer")
        {
            return ConvertValue(true, type);
        }

        if (function == "getTargetMonster")
        {
            if (state.TargetMonster is not null && state.TargetMonster.HP <= 0)
                state.TargetMonster = null;

            string json = state.TargetMonster is not null ? JsonConvert.SerializeObject(state.TargetMonster) : "{}";
            return ConvertValue(json, type);
        }

        if (function == "getMonsters")
        {
            string json = JsonConvert.SerializeObject(state.MapMonsters);
            return ConvertValue(json, type);
        }

        if (function == "availableMonsters")
        {
            var cellMons = state.MapMonsters.Where(m => string.Equals(m.Cell, state.Cell, StringComparison.OrdinalIgnoreCase) && m.HP > 0).ToList();
            string json = JsonConvert.SerializeObject(cellMons);
            return ConvertValue(json, type);
        }

        if (function == "useSkill" && args.Length >= 1)
        {
            int skillIndex = Convert.ToInt32(args[0]);
            int targetId = args.Length >= 2 ? Convert.ToInt32(args[1]) : (state.TargetMonster?.MapID ?? 0);
            if (targetId == 0)
            {
                var candidate = state.MapMonsters.FirstOrDefault(m => string.Equals(m.Cell, state.Cell, StringComparison.OrdinalIgnoreCase) && m.HP > 0);
                if (candidate is not null)
                {
                    state.TargetMonster = candidate;
                    targetId = candidate.MapID;
                }
            }

            long now = Environment.TickCount64;
            _skillLastUsed[skillIndex] = now;
            if (skillIndex >= 1 && skillIndex <= 4)
            {
                _lastGlobalSkillTime = now;
            }

            var skill = state.Skills.FirstOrDefault(s => s.ActID == skillIndex)
                        ?? (skillIndex < state.Skills.Count ? state.Skills[skillIndex] : null);
            if (skill != null)
            {
                skill.LastUse = now;
                skill.IsOk = false;
                if (skill.MP > 0)
                    state.MP = Math.Max(0, state.MP - skill.MP);
            }
            if (skillIndex == 0)
            {
                // Regain mana on auto-attack like all AQW classes
                state.MP = Math.Min(state.MaxMP, state.MP + 10);
            }

            if (targetId > 0)
            {
                int actId = Interlocked.Increment(ref _actionCounter);
                string skillRef = skillIndex switch
                {
                    0 => "aa",
                    1 => "a1",
                    2 => "a2",
                    3 => "a3",
                    4 => "a4",
                    5 => "i1",
                    _ => $"a{skillIndex}"
                };
                _ = _session.SendPacketAsync($"%xt%zm%gar%1%{actId}%{skillRef}>m:{targetId}%wvz%");
            }
            return ConvertValue(true, type);
        }

        if (function == "canUseSkill")
        {
            int skillIndex = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
            if (state.HP <= 0)
                return ConvertValue(false, type);

            long now = Environment.TickCount64;

            // Auto-attack (skill 0)
            if (skillIndex == 0)
            {
                if (_skillLastUsed.TryGetValue(0, out long lastAa) && (now - lastAa < 1500))
                    return ConvertValue(false, type);
                return ConvertValue(true, type);
            }

            // Global Cooldown (GCD) for skills 1..4 (1250ms)
            if (skillIndex >= 1 && skillIndex <= 4)
            {
                if (now - _lastGlobalSkillTime < 1250)
                    return ConvertValue(false, type);
            }

            // Skill specific check
            var skill = state.Skills.FirstOrDefault(s => s.ActID == skillIndex)
                        ?? (skillIndex < state.Skills.Count ? state.Skills[skillIndex] : null);

            if (skill != null)
            {
                if (skill.Locked)
                    return ConvertValue(false, type);

                if (skill.MP > 0 && state.MP < skill.MP)
                    return ConvertValue(false, type);

                int cd = skill.Cooldown > 0 ? skill.Cooldown : 2000;
                if (_skillLastUsed.TryGetValue(skillIndex, out long lastUsed) && (now - lastUsed < cd))
                    return ConvertValue(false, type);
            }
            else
            {
                if (_skillLastUsed.TryGetValue(skillIndex, out long lastUsed) && (now - lastUsed < 2000))
                    return ConvertValue(false, type);
            }

            return ConvertValue(true, type);
        }

        if (function == "untargetSelf" || function == "world.cancelTarget" || function == "world.cancelAutoAttack")
        {
            state.TargetMonster = null;
            return ConvertValue(true, type);
        }

        if (function == "buyItemByName" && args.Length > 0)
        {
            string itemName = args[0]?.ToString() ?? string.Empty;
            var item = state.ShopItems.FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                int qty = args.Length > 1 ? Convert.ToInt32(args[1]) : 1;
                _ = _session.SendPacketAsync($"%xt%zm%buyItem%{state.RoomID}%{item.ID}%{state.ShopID}%{item.ShopItemID}%{qty}%");
            }
            return ConvertValue(true, type);
        }

        if (function == "buyItemByID" && args.Length >= 2)
        {
            int itemId = Convert.ToInt32(args[0]);
            int shopItemId = Convert.ToInt32(args[1]);
            int qty = args.Length > 2 ? Convert.ToInt32(args[2]) : 1;
            _ = _session.SendPacketAsync($"%xt%zm%buyItem%{state.RoomID}%{itemId}%{state.ShopID}%{shopItemId}%{qty}%");
            return ConvertValue(true, type);
        }

        if (function is "killLag" or "disableFX" or "hidePlayers" or "skipCutscenes" or "modEnable" or "modDisable"
            or "infiniteRange" or "magnetize" or "disableDeathAd" or "resetConnectionState")
        {
            return ConvertValue(true, type);
        }

        if (function == "inventoryHasSpaceFor")
        {
            return ConvertValue(true, type);
        }

        if (function == "hasInventoryCategories")
        {
            return ConvertValue(true, type);
        }

        if (function == "inventoryBagUsedSlots")
        {
            return ConvertValue(state.Inventory.Count, type);
        }

        if (function == "inventoryMiscSlots")
        {
            return ConvertValue(state.BagSlots, type);
        }

        if (function == "inventoryMiscUsedSlots")
        {
            return ConvertValue(state.Inventory.Count, type);
        }

        if (function == "inventoryPool")
        {
            return ConvertValue("bag", type);
        }

        if (function == "isFavoriteItem")
        {
            return ConvertValue(false, type);
        }

        if (function == "GetAuraSnapshots")
        {
            return ConvertValue("[]", type);
        }

        if (function == "HasAnyActiveAura")
        {
            return ConvertValue(false, type);
        }

        // Object queries via Call
        if (function == "getGameObject" && args.Length > 0 && args[0] is string getPath)
        {
            string? raw = GetGameObject(getPath);
            return ConvertValue(raw, type);
        }

        if (function == "getGameObjectS" && args.Length > 0 && args[0] is string getPathS)
        {
            string? raw = GetGameObjectStatic(getPathS);
            return ConvertValue(raw, type);
        }

        if (function == "getGameObjectKey" && args.Length >= 2 && args[0] is string getPathKey && args[1] is string key)
        {
            string? raw = GetGameObjectKey(getPathKey, key);
            return ConvertValue(raw, type);
        }

        if (function == "selectArrayObjects" && args.Length >= 2 && args[0] is string arrPath && args[1] is string selector)
        {
            string raw = SelectArrayObjectsRaw(arrPath, selector);
            return ConvertValue(raw, type);
        }

        if (function == "getArrayObject" && args.Length >= 2 && args[0] is string arrIdxPath && args[1] is int idx)
        {
            string? raw = GetArrayObjectRaw(arrIdxPath, idx);
            return ConvertValue(raw, type);
        }

        if (function == "getActiveModalMessage")
        {
            return ConvertValue(null, type);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    public bool IsNull(string path)
    {
        var state = _session.State;
        if (path.StartsWith("world.questTree[", StringComparison.OrdinalIgnoreCase))
        {
            string key = path.Split('[', ']')[1].Trim('"', '\'');
            return !state.RawQuests.ContainsKey(key);
        }

        return path switch
        {
            "world" => !state.WorldLoaded,
            "world.myAvatar" => !state.WorldLoaded,
            "world.myAvatar.pMC" => !state.WorldLoaded,
            "world.avatars" => !state.WorldLoaded,
            "world.actions.active" => false,
            "world.questTree" => false,
            "ui.ModalStack" => true,
            "mcLogin.warning" => true,
            "mcConnDetail.stage" => true,
            "mcLogin.sl.iList" => false,
            "sfc" => !_session.Client.IsConnected,
            _ => false
        };
    }

    public string? GetGameObject(string path)
    {
        var state = _session.State;

        if (path.StartsWith("world.actions.active[", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".isOK", StringComparison.OrdinalIgnoreCase))
                return "true";
            return "true";
        }

        if (path == "world.actions.active")
            return JsonConvert.SerializeObject(state.Skills);

        if (path == "world.myAvatar.items")
            return state.Inventory.Count > 0
                ? JsonConvert.SerializeObject(state.Inventory)
                : (state.WorldLoaded || state.IsLoggedIn)
                    ? "[{\"ItemID\":1,\"sName\":\"Default Item\",\"iQty\":1,\"bEquip\":1,\"sType\":\"Class\"}]"
                    : "[]";

        if (path == "world.myAvatar.items.length")
            return (state.Inventory.Count > 0 ? state.Inventory.Count : (state.WorldLoaded || state.IsLoggedIn ? 1 : 0)).ToString();

        if (path == "world.myAvatar.tempitems")
            return JsonConvert.SerializeObject(state.TempInventory);

        if (path == "world.myAvatar.tempitems.length")
            return state.TempInventory.Count.ToString();

        if (path == "world.myAvatar.houseitems")
            return JsonConvert.SerializeObject(state.HouseItems);

        if (path == "world.myAvatar.houseitems.length" || path == "world.myAvatar.objData.iHouseSlots")
            return "0";

        if (path == "world.bankinfo.items" || path == "world.myAvatar.bankitems")
            return JsonConvert.SerializeObject(state.Bank);

        if (path == "world.myAvatar.iBankCount")
            return state.Bank.Count.ToString();

        if (path == "world.myAvatar.objData.iBankSlots")
            return state.BankSlots.ToString();

        if (path == "world.myAvatar.objData.iBagSlots")
            return state.BagSlots.ToString();

        if (path == "world.myAvatar.dataLeaf.intState")
            return state.State.ToString();

        if (path == "world.myAvatar.dataLeaf.afk")
            return state.IsAfk ? "true" : "false";

        if (path == "world.myAvatar.objData.iCurCP")
            return state.CurrentClassPoints.ToString();

        if (path == "world.myAvatar.objData.iCPToRank")
            return state.ClassPointsToRank.ToString();

        if (path == "world.myAvatar.objData.iRank")
            return state.CurrentClassRank.ToString();

        if (path == "world.myAvatar.objData.strUsername" || path == "world.rootClass.ui.mcPortrait.strName.text" || path == "world.myAvatar.pMC.pname.ti.text")
            return $"\"{state.Username}\"";

        if (path == "world.myAvatar.uid")
            return state.UserId.ToString();

        if (path == "world.myAvatar.objData.intLevel" || path == "world.myAvatar.dataLeaf.intLevel")
            return state.Level.ToString();

        if (path == "world.myAvatar.objData.intGold")
            return state.Gold.ToString();

        if (path == "world.myAvatar.objData.intCoins")
            return state.Coins.ToString();

        if (path == "world.myAvatar.objData.intExp")
            return state.XP.ToString();

        if (path == "world.myAvatar.objData.intExpToLevel")
            return state.RequiredXP.ToString();

        if (path == "world.myAvatar.objData.intHP" || path == "world.myAvatar.dataLeaf.intHP")
            return state.HP.ToString();

        if (path == "world.myAvatar.objData.intHPMax" || path == "world.myAvatar.dataLeaf.intHPMax")
            return state.MaxHP.ToString();

        if (path == "world.myAvatar.objData.intMP")
            return state.MP.ToString();

        if (path == "world.myAvatar.dataLeaf.intMPMax")
            return state.MaxMP.ToString();

        if (path == "world.strMapName")
            return $"\"{state.MapName}\"";

        if (path == "world.strAreaName")
            return $"\"{state.AreaName}\"";

        if (path == "world.curRoom")
            return state.RoomID.ToString();

        if (path == "world.strFrame")
            return $"\"{state.Cell}\"";

        if (path == "world.strPad")
            return $"\"{state.Pad}\"";

        if (path == "world.mapLoadInProgress")
            return state.MapLoading ? "true" : "false";

        if (path == "world.monsters")
            return JsonConvert.SerializeObject(state.MapMonsters);

        if (path == "world.uoTree")
            return JsonConvert.SerializeObject(state.PlayersInCell);

        if (path == "world.areaUsers")
            return JsonConvert.SerializeObject(state.PlayersInCell.Keys.ToList());

        if (path == "world.areaUsers.length")
            return state.PlayersInCell.Count.ToString();

        if (path == "world.map.currentScene.labels")
        {
            var labels = state.MapMonsters
                .Select(m => m.Cell)
                .Append(state.Cell)
                .Append("Enter")
                .Append("Spawn")
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .Select(c => new { name = c });
            return JsonConvert.SerializeObject(labels);
        }

        if (path == "world.questTree")
            return JsonConvert.SerializeObject(state.RawQuests);

        if (path.StartsWith("world.questTree[", StringComparison.OrdinalIgnoreCase))
        {
            string key = path.Split('[', ']')[1].Trim('"', '\'');
            if (state.RawQuests.TryGetValue(key, out var qObj))
                return qObj.ToString();
            return "null";
        }

        if (path.StartsWith("world.questTree.", StringComparison.OrdinalIgnoreCase))
        {
            string key = path.Substring("world.questTree.".Length).Trim('"', '\'');
            if (state.RawQuests.TryGetValue(key, out var qObj))
                return qObj.ToString();
            return "null";
        }

        if (path == "world.shopinfo.items")
            return JsonConvert.SerializeObject(state.ShopItems);

        if (path == "world.shopinfo.ShopID")
            return state.ShopID.ToString();

        if (path == "world.shopinfo.sName")
            return $"\"{state.ShopName}\"";

        if (path == "serialCmd.servers")
            return JsonConvert.SerializeObject(state.Servers);

        if (path == "serverIP")
            return $"\"{state.ServerIP}\"";

        if (path == "loginInfo.strUsername")
            return $"\"{state.Username}\"";

        if (path == "sfc.isConnected")
            return _session.Client.IsConnected ? "true" : "false";

        if (path.StartsWith("litePreference.data.", StringComparison.OrdinalIgnoreCase))
            return "true";

        return null;
    }

    public T? GetGameObject<T>(string path, T? def = default)
    {
        try
        {
            string? json = GetGameObject(path);
            if (string.IsNullOrWhiteSpace(json))
                return def;

            return JsonConvert.DeserializeObject<T>(json) ?? def;
        }
        catch
        {
            return def;
        }
    }

    public string? GetGameObjectStatic(string path)
    {
        return GetGameObject(path);
    }

    public T? GetGameObjectStatic<T>(string path, T? def = default)
    {
        return GetGameObject<T>(path, def);
    }

    public string? GetGameObjectKey(string path, string key)
    {
        string? raw = GetGameObject(path);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            JObject obj = JObject.Parse(raw);
            return obj[key]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public void SetGameObject(string path, object value)
    {
        var state = _session.State;
        if (path == "world.questTree")
        {
            state.RawQuests.Clear();
        }
    }

    public string? GetArrayObject(string path, int index)
    {
        return GetArrayObjectRaw(path, index);
    }

    public T? GetArrayObject<T>(string path, int index, T? def = default)
    {
        try
        {
            string? json = GetArrayObject(path, index);
            if (string.IsNullOrWhiteSpace(json))
                return def;

            return JsonConvert.DeserializeObject<T>(json) ?? def;
        }
        catch
        {
            return def;
        }
    }

    public List<T> SelectArrayObjects<T>(string path, string selector)
    {
        try
        {
            string raw = SelectArrayObjectsRaw(path, selector);
            return JsonConvert.DeserializeObject<List<T>>(raw) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private string SelectArrayObjectsRaw(string path, string selector)
    {
        string? raw = GetGameObject(path);
        if (string.IsNullOrWhiteSpace(raw))
            return "[]";

        try
        {
            JArray array = JArray.Parse(raw);
            JArray selected = new();
            foreach (var item in array)
            {
                if (item is JObject obj && obj[selector] is JToken token)
                    selected.Add(token);
            }
            return selected.ToString();
        }
        catch
        {
            return "[]";
        }
    }

    private string? GetArrayObjectRaw(string path, int index)
    {
        string? raw = GetGameObject(path);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            JArray array = JArray.Parse(raw);
            if (index >= 0 && index < array.Count)
                return array[index].ToString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        int id = Interlocked.Increment(ref _nextObjectId);
        _trackedObjects[id] = ResolvePathObject(path);
        return new FlashObject<T>(id, this);
    }

    public object FromFlashXml(XElement el)
    {
        return el.Value;
    }

    private object? ResolvePathObject(string path)
    {
        var state = _session.State;
        if (path == "world.actions.active")
            return state.Skills;
        if (path == "world.myAvatar.items")
            return state.Inventory;
        if (path == "world.bankinfo.items")
            return state.Bank;
        if (path == "world.monsters")
            return state.MapMonsters;

        string? json = GetGameObject(path);
        return json is not null ? JsonConvert.DeserializeObject(json) : null;
    }

    private static object? ResolveChildObject(object? parent, string childPath)
    {
        if (parent is null)
            return null;

        if (childPath == "length" && parent is IList list)
            return list.Count;

        if (parent is JObject jObj)
            return jObj[childPath]?.ToObject<object>();

        try
        {
            var prop = parent.GetType().GetProperty(childPath);
            if (prop is not null)
                return prop.GetValue(parent);
        }
        catch
        {
        }

        return null;
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        if (targetType.IsAssignableFrom(value.GetType()))
            return value;

        if (targetType == typeof(string))
            return value is bool b ? (b ? "true" : "false") : value.ToString();

        if (targetType == typeof(bool))
        {
            if (value is bool b) return b;
            if (bool.TryParse(value.ToString(), out bool parsedBool)) return parsedBool;
            if (int.TryParse(value.ToString(), out int parsedInt)) return parsedInt != 0;
            return false;
        }

        if (targetType == typeof(int))
            return Convert.ToInt32(value);

        if (targetType == typeof(long))
            return Convert.ToInt64(value);

        if (targetType == typeof(double))
            return Convert.ToDouble(value);

        return value;
    }

    public void Dispose()
    {
        _session.FlashEventReceived -= OnSessionFlashEvent;
        _trackedObjects.Clear();
    }
}
