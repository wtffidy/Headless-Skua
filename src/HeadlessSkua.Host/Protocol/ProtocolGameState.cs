using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Servers;
using Skua.Core.Models.Shops;
using Skua.Core.Models.Skills;

namespace HeadlessSkua.Protocol;

/// <summary>
/// Thread-safe in-memory store representing the live AQW character, map, inventory, and combat state.
/// </summary>
public sealed class ProtocolGameState
{
    private readonly object _stateLock = new();

    public bool IsLoggedIn { get; set; }
    public bool WorldLoaded { get; set; }
    public bool IsKicked { get; set; }
    public string? Account { get; set; }
    public string? ServerName { get; set; }
    public string? ServerIP { get; set; }

    // Player State
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public long Gold { get; set; }
    public long Coins { get; set; }
    public long XP { get; set; }
    public long RequiredXP { get; set; }
    public int HP { get; set; } = 1000;
    public int MaxHP { get; set; } = 1000;
    public int MP { get; set; } = 100;
    public int MaxMP { get; set; } = 100;
    public int State { get; set; } = 1; // 1 = Alive, 2 = Combat, 0 = Dead
    public bool IsAfk { get; set; }
    public int BagSlots { get; set; } = 40;
    public int BankSlots { get; set; } = 50;
    public int CurrentClassPoints { get; set; }
    public int ClassPointsToRank { get; set; } = 1000;
    public int CurrentClassRank { get; set; } = 1;

    // Map State
    public string MapName { get; set; } = "battleon";
    public string AreaName { get; set; } = "battleon";
    public string FilePath { get; set; } = string.Empty;
    public int RoomID { get; set; } = 1;
    public string Cell { get; set; } = "Enter";
    public string Pad { get; set; } = "Spawn";
    public bool MapLoading { get; set; }

    // Combat State
    public Monster? TargetMonster { get; set; }
    public List<SkillInfo> Skills { get; set; } = new();

    // Shop State
    public int ShopID { get; set; }
    public string ShopName { get; set; } = string.Empty;
    public List<ShopItem> ShopItems { get; set; } = new();

    // Collections
    public List<Server> Servers { get; set; } = new();
    public List<InventoryItem> Inventory { get; set; } = new();
    public List<InventoryItem> TempInventory { get; set; } = new();
    public List<InventoryItem> Bank { get; set; } = new();
    public List<InventoryItem> HouseItems { get; set; } = new();
    public List<Monster> MapMonsters { get; set; } = new();
    public int RoomEntId { get; set; }
    public ConcurrentDictionary<string, JObject> PlayersInCell { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, JObject> RawQuests { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Achievements { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<int, Quest> Quests { get; } = new();
    public ConcurrentDictionary<int, int> ActiveQuests { get; } = new(); // QuestId -> Progress

    public ProtocolGameState()
    {
        InitializeDefaultSkills();
    }

    private void InitializeDefaultSkills()
    {
        Skills = new List<SkillInfo>
        {
            new SkillInfo { ID = 1, ActID = 0, Auto = true, Name = "Auto Attack", Range = 150, Damage = 1.0, MP = 0, Icon = "i_atk" },
            new SkillInfo { ID = 2, ActID = 1, Auto = false, Name = "Skill 1", Range = 200, Damage = 1.2, MP = 10, Icon = "i_s1" },
            new SkillInfo { ID = 3, ActID = 2, Auto = false, Name = "Skill 2", Range = 200, Damage = 1.5, MP = 15, Icon = "i_s2" },
            new SkillInfo { ID = 4, ActID = 3, Auto = false, Name = "Skill 3", Range = 200, Damage = 2.0, MP = 20, Icon = "i_s3" },
            new SkillInfo { ID = 5, ActID = 4, Auto = false, Name = "Skill 4", Range = 200, Damage = 2.5, MP = 25, Icon = "i_s4" }
        };
    }

    /// <summary>
    /// Processes incoming AQW server extension packets to update internal state.
    /// </summary>
    public void HandlePacket(string cmd, JObject data)
    {
        lock (_stateLock)
        {
            switch (cmd)
            {
                case "loginResponse":
                    HandleLoginResponse(data);
                    break;

                case "moveToArea":
                    HandleMoveToArea(data);
                    break;

                case "initUserDatas":
                case "retrieveUserData":
                    HandleUserData(data);
                    break;

                case "ct":
                    HandleCombatTick(data);
                    break;

                case "sars":
                case "sar":
                    HandleSars(data);
                    break;

                case "loadInventoryBig":
                case "loadInventory":
                    HandleLoadInventory(data);
                    break;

                case "dropItem":
                    // Drops are dispatched via Skua events
                    break;

                case "addItems":
                    HandleAddItems(data);
                    break;

                case "buyItem":
                    // Handled in Skua
                    break;

                case "sellItem":
                    HandleSellItem(data);
                    break;

                case "loadBank":
                    HandleLoadBank(data);
                    break;

                case "bankFromInv":
                    HandleBankFromInv(data);
                    break;

                case "bankToInv":
                    HandleBankToInv(data);
                    break;

                case "bankSwapInv":
                    HandleBankSwapInv(data);
                    break;

                case "loadShop":
                    HandleLoadShop(data);
                    break;

                case "getQuests":
                    HandleGetQuests(data);
                    break;

                case "equipItem":
                    HandleEquipItem(data);
                    break;

                case "unequipItem":
                    HandleUnequipItem(data);
                    break;

                case "sAct":
                    HandleActions(data);
                    break;

                case "uotls":
                    HandleUotls(data);
                    break;

                case "respawnMon":
                    HandleRespawnMon(data);
                    break;

                case "acceptQuest":
                    if (data["QuestID"] is not null)
                    {
                        int qId = data["QuestID"]!.Value<int>();
                        ActiveQuests.TryAdd(qId, 1);
                        if (RawQuests.TryGetValue(qId.ToString(), out var qObj))
                            qObj["status"] = "p";
                    }
                    break;

                case "ccqr": // Quest complete response
                    if (data["bSuccess"]?.Value<int>() == 1 && data["QuestID"] is not null)
                    {
                        int qId = data["QuestID"]!.Value<int>();
                        ActiveQuests.TryRemove(qId, out _);
                        if (RawQuests.TryGetValue(qId.ToString(), out var qObj))
                        {
                            qObj.Remove("status");
                            string sField = qObj["sField"]?.Value<string>()?.ToLowerInvariant() ?? string.Empty;
                            int iIndex = qObj["iIndex"]?.Value<int>() ?? -1;
                            if (!string.IsNullOrEmpty(sField) && iIndex >= 0)
                            {
                                Achievements.AddOrUpdate(sField, 1 << iIndex, (k, old) => old | (1 << iIndex));
                            }
                        }
                    }
                    break;

                case "warning":
                    // Server warnings
                    break;
            }
        }
    }

    private void HandleLoginResponse(JObject data)
    {
        IsLoggedIn = true;
        WorldLoaded = true;

        if (data["loginInfo"] is JObject loginInfo)
        {
            Username = loginInfo["strUsername"]?.Value<string>() ?? Username;
            UserId = loginInfo["UserID"]?.Value<int>() ?? UserId;
        }

        if (data["userInfo"] is JObject userInfo)
        {
            Level = userInfo["intLevel"]?.Value<int>() ?? Level;
            Gold = userInfo["intGold"]?.Value<long>() ?? Gold;
            Coins = userInfo["intCoins"]?.Value<long>() ?? Coins;
            XP = userInfo["intExp"]?.Value<long>() ?? XP;
            RequiredXP = userInfo["intExpToLevel"]?.Value<long>() ?? RequiredXP;
            HP = userInfo["intHP"]?.Value<int>() ?? HP;
            MaxHP = userInfo["intHPMax"]?.Value<int>() ?? MaxHP;
            MP = userInfo["intMP"]?.Value<int>() ?? MP;
            MaxMP = userInfo["intMPMax"]?.Value<int>() ?? MaxMP;
            BagSlots = userInfo["iBagSlots"]?.Value<int>() ?? BagSlots;
        }

        // Inventory items
        if (data["items"] is JArray itemsArray)
        {
            string itemsJson = NormalizeFlashJson(itemsArray.ToString());
            var allItems = JsonConvert.DeserializeObject<List<InventoryItem>>(itemsJson) ?? new List<InventoryItem>();
            Inventory = allItems.Where(i => !i.Temp).ToList();
            TempInventory = allItems.Where(i => i.Temp).ToList();
        }

        // Quests
        if (data["quests"] is JObject questsObj)
        {
            foreach (var prop in questsObj.Properties())
            {
                if (prop.Value is JObject qObj)
                {
                    RawQuests[prop.Name] = qObj;
                    if (int.TryParse(prop.Name, out int qId))
                    {
                        try
                        {
                            Quest? quest = JsonConvert.DeserializeObject<Quest>(qObj.ToString());
                            if (quest is not null)
                                Quests[qId] = quest;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        // Update active class
        var currentClass = Inventory.Find(i => i.Equipped && i.Category == ItemCategory.Class);
        if (currentClass is not null)
        {
            CurrentClassRank = currentClass.Level;
        }

        UpdateSelfInCell();
    }

    private void HandleMoveToArea(JObject data)
    {
        MapName = data["strMapName"]?.Value<string>() ?? MapName;
        AreaName = data["strAreaName"]?.Value<string>() ?? MapName;
        FilePath = data["strMapFileName"]?.Value<string>() ?? FilePath;
        RoomID = data["areaId"]?.Value<int>() ?? RoomID;
        MapLoading = false;

        if (data["strExtra"] is not null)
        {
            string extra = data["strExtra"]!.Value<string>() ?? string.Empty;
            string[] parts = extra.Split(':');
            if (parts.Length >= 2)
            {
                Cell = parts[0];
                Pad = parts[1];
            }
        }

        if (data["uoBranch"] is JArray uoBranch)
        {
            foreach (var uo in uoBranch.OfType<JObject>())
            {
                string? uName = uo["strUsername"]?.Value<string>();
                if (!string.IsNullOrEmpty(uName))
                {
                    PlayersInCell[uName] = uo;
                    if (string.Equals(uName, Username, StringComparison.OrdinalIgnoreCase))
                    {
                        RoomEntId = uo["entID"]?.Value<int>() ?? RoomEntId;
                        HP = uo["intHP"]?.Value<int>() ?? HP;
                        MaxHP = uo["intHPMax"]?.Value<int>() ?? MaxHP;
                        MP = uo["intMP"]?.Value<int>() ?? MP;
                        MaxMP = uo["intMPMax"]?.Value<int>() ?? MaxMP;
                        Level = uo["intLevel"]?.Value<int>() ?? Level;
                        State = uo["intState"]?.Value<int>() ?? State;
                        Cell = uo["strFrame"]?.Value<string>() ?? Cell;
                        Pad = uo["strPad"]?.Value<string>() ?? Pad;
                    }
                }
            }
        }

        // Populate MapMonsters
        JArray? monDefArray = data["mondef"] as JArray ?? data["monDef"] as JArray;
        JArray? monMapArray = data["monmap"] as JArray ?? data["monMap"] as JArray;
        JArray? monBranchArray = data["monBranch"] as JArray ?? data["monbranch"] as JArray;

        if (monDefArray is not null || monMapArray is not null || monBranchArray is not null)
        {
            Dictionary<int, JObject> defs = new();
            if (monDefArray is not null)
            {
                foreach (var d in monDefArray.OfType<JObject>())
                {
                    int monId = d["MonID"]?.Value<int>() ?? 0;
                    if (monId > 0)
                        defs[monId] = d;
                }
            }

            Dictionary<int, JObject> maps = new();
            if (monMapArray is not null)
            {
                foreach (var m in monMapArray.OfType<JObject>())
                {
                    int monMapId = m["MonMapID"]?.Value<int>() ?? 0;
                    if (monMapId > 0)
                        maps[monMapId] = m;
                }
            }

            Dictionary<int, JObject> branches = new();
            if (monBranchArray is not null)
            {
                foreach (var b in monBranchArray.OfType<JObject>())
                {
                    int monMapId = b["MonMapID"]?.Value<int>() ?? 0;
                    if (monMapId > 0)
                        branches[monMapId] = b;
                }
            }

            List<Monster> monsters = new();
            var allMonMapIds = maps.Keys.Union(branches.Keys).ToList();

            if (allMonMapIds.Count > 0)
            {
                foreach (int monMapId in allMonMapIds)
                {
                    maps.TryGetValue(monMapId, out JObject? mapObj);
                    branches.TryGetValue(monMapId, out JObject? branchObj);

                    int monId = mapObj?["MonID"]?.Value<int>() ?? branchObj?["MonID"]?.Value<int>() ?? 0;
                    defs.TryGetValue(monId, out JObject? def);

                    string frame = mapObj?["strFrame"]?.Value<string>() ?? branchObj?["strFrame"]?.Value<string>() ?? Cell;
                    string name = def?["strMonName"]?.Value<string>() ?? branchObj?["strMonName"]?.Value<string>() ?? "Unknown";
                    int maxHp = branchObj?["intHPMax"]?.Value<int>() ?? def?["intHPMax"]?.Value<int>() ?? 100;
                    int hp = branchObj?["intHP"]?.Value<int>() ?? maxHp;
                    int state = branchObj?["intState"]?.Value<int>() ?? 1;

                    Monster m = new()
                    {
                        ID = monId,
                        MapID = monMapId,
                        Name = name,
                        HP = hp,
                        MaxHP = maxHp,
                        State = state,
                        Cell = frame
                    };
                    monsters.Add(m);
                }
            }
            else if (defs.Count > 0)
            {
                foreach (var d in defs.Values)
                {
                    Monster m = new()
                    {
                        ID = d["MonID"]?.Value<int>() ?? 0,
                        MapID = d["MonMapID"]?.Value<int>() ?? 0,
                        Name = d["strMonName"]?.Value<string>() ?? "Unknown",
                        HP = d["intHP"]?.Value<int>() ?? d["intHPMax"]?.Value<int>() ?? 100,
                        MaxHP = d["intHPMax"]?.Value<int>() ?? 100,
                        State = 1,
                        Cell = d["strFrame"]?.Value<string>() ?? Cell
                    };
                    monsters.Add(m);
                }
            }

            MapMonsters = monsters;
        }

        PlayersInCell.Clear();
        UpdateSelfInCell();
    }

    private void UpdateSelfInCell()
    {
        if (string.IsNullOrEmpty(Username))
            return;

        PlayersInCell[Username] = new JObject
        {
            ["strUsername"] = Username,
            ["intLevel"] = Level,
            ["intHP"] = HP,
            ["intHPMax"] = MaxHP,
            ["intMP"] = MP,
            ["intMPMax"] = MaxMP,
            ["intState"] = State,
            ["strFrame"] = Cell,
            ["strPad"] = Pad
        };
    }

    private void HandleUserData(JObject data)
    {
        if (data["data"] is JObject singleData)
        {
            ApplySingleUserData(singleData);
        }
        else if (data["a"] is JArray userArray)
        {
            foreach (var item in userArray.OfType<JObject>())
            {
                if (item["data"] is JObject uData)
                {
                    ApplySingleUserData(uData);
                }
            }
        }
    }

    private void ApplySingleUserData(JObject userData)
    {
        string? user = userData["strUsername"]?.Value<string>();
        if (!string.IsNullOrEmpty(user))
        {
            PlayersInCell[user] = userData;
            if (string.Equals(user, Username, StringComparison.OrdinalIgnoreCase))
            {
                HP = userData["intHP"]?.Value<int>() ?? HP;
                MaxHP = userData["intHPMax"]?.Value<int>() ?? MaxHP;
                MP = userData["intMP"]?.Value<int>() ?? MP;
                MaxMP = userData["intMPMax"]?.Value<int>() ?? MaxMP;
                Level = userData["intLevel"]?.Value<int>() ?? Level;
                Gold = userData["intGold"]?.Value<long>() ?? Gold;
                Coins = userData["intCoins"]?.Value<long>() ?? Coins;
                XP = userData["intExp"]?.Value<long>() ?? XP;
                RequiredXP = userData["intExpToLevel"]?.Value<long>() ?? RequiredXP;
                State = userData["intState"]?.Value<int>() ?? State;
                BagSlots = userData["iBagSlots"]?.Value<int>() ?? BagSlots;
                BankSlots = userData["iBankSlots"]?.Value<int>() ?? BankSlots;

                foreach (var prop in userData.Properties())
                {
                    if (prop.Name.StartsWith("id", StringComparison.OrdinalIgnoreCase) && prop.Value.Type == JTokenType.Integer)
                    {
                        Achievements[prop.Name.ToLowerInvariant()] = prop.Value.Value<int>();
                    }
                }
            }
        }
    }

    private void HandleLoadInventory(JObject data)
    {
        if (data["items"] is JArray itemsArray)
        {
            string itemsJson = NormalizeFlashJson(itemsArray.ToString());
            var allItems = JsonConvert.DeserializeObject<List<InventoryItem>>(itemsJson) ?? new List<InventoryItem>();
            Inventory = allItems.Where(i => !i.Temp).ToList();
            TempInventory = allItems.Where(i => i.Temp).ToList();
            var currentClass = Inventory.Find(i => i.Equipped && i.Category == ItemCategory.Class);
            if (currentClass is not null)
                CurrentClassRank = currentClass.Level;
        }
    }

    private void HandleCombatTick(JObject data)
    {
        string user = Username.ToLowerInvariant();
        if (data["p"] is JObject players)
        {
            JToken? myData = players[Username] ?? players[user] ?? (UserId > 0 ? players[UserId.ToString()] : null);
            if (myData is JObject myCombatData)
            {
                HP = myCombatData["intHP"]?.Value<int>() ?? HP;
                MP = myCombatData["intMP"]?.Value<int>() ?? MP;
                State = HP <= 0 ? 0 : 2; // Dead or in combat
            }
        }

        if (data["m"] is JObject monsters)
        {
            foreach (var prop in monsters.Properties())
            {
                if (int.TryParse(prop.Name, out int mapId) && prop.Value is JObject monData)
                {
                    var mon = MapMonsters.FirstOrDefault(m => m.MapID == mapId);
                    if (mon is not null)
                    {
                        mon.HP = monData["intHP"]?.Value<int>() ?? mon.HP;
                        mon.State = mon.HP <= 0 ? 0 : 2;
                        if (mon.HP <= 0 && TargetMonster?.MapID == mapId)
                        {
                            TargetMonster = null;
                        }
                    }
                }
            }
        }

        if (data["sarsa"] is JArray sarsa)
        {
            foreach (var entry in sarsa.OfType<JObject>())
            {
                ProcessCombatActionArray(entry["a"] as JArray);
            }
        }
        else if (data["a"] is JArray actions)
        {
            ProcessCombatActionArray(actions);
        }
    }

    private void HandleSars(JObject data)
    {
        if (data["a"] is JArray actions)
        {
            ProcessCombatActionArray(actions);
        }
    }

    private void ProcessCombatActionArray(JArray? actions)
    {
        if (actions is null) return;
        foreach (var act in actions.OfType<JObject>())
        {
            string? tInf = act["tInf"]?.ToString();
            if (!string.IsNullOrEmpty(tInf))
            {
                if (tInf.StartsWith("m:") && int.TryParse(tInf[2..], out int monMapId))
                {
                    var mon = MapMonsters.FirstOrDefault(m => m.MapID == monMapId);
                    if (mon is not null)
                    {
                        if (act["hp"] is not null)
                        {
                            mon.HP = act["hp"]!.Value<int>();
                        }
                        else if (act["dmg"] is not null)
                        {
                            int dmg = act["dmg"]!.Value<int>();
                            mon.HP = Math.Max(0, mon.HP - dmg);
                        }
                        mon.State = mon.HP <= 0 ? 0 : 2;
                        if (mon.HP <= 0 && TargetMonster?.MapID == monMapId)
                        {
                            TargetMonster = null;
                        }
                    }
                }
                else if (tInf.StartsWith("p:") && int.TryParse(tInf[2..], out int pUid))
                {
                    if (pUid == UserId || pUid == RoomEntId)
                    {
                        if (act["hp"] is not null) HP = act["hp"]!.Value<int>();
                        else if (act["dmg"] is not null) HP = Math.Max(0, HP - act["dmg"]!.Value<int>());
                        if (act["mp"] is not null) MP = act["mp"]!.Value<int>();
                        State = HP <= 0 ? 0 : 2;
                    }
                }
            }
        }
    }

    private void HandleAddItems(JObject data)
    {
        if (data["items"] is JObject itemsObj)
        {
            lock (_stateLock)
            {
                foreach (var prop in itemsObj.Properties())
                {
                    if (prop.Value is JObject itemData)
                    {
                        try
                        {
                            InventoryItem? newItem = JsonConvert.DeserializeObject<InventoryItem>(itemData.ToString());
                            if (newItem is not null)
                            {
                                var targetList = newItem.Temp ? TempInventory : Inventory;
                                int existingIndex = targetList.FindIndex(i => i.ID == newItem.ID || (newItem.CharItemID > 0 && i.CharItemID == newItem.CharItemID));
                                if (existingIndex >= 0)
                                {
                                    int qtyNow = itemData["iQtyNow"]?.Value<int>() ?? newItem.Quantity;
                                    targetList[existingIndex].Quantity = qtyNow;
                                }
                                else
                                {
                                    targetList.Add(newItem);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
    }

    private void HandleSellItem(JObject data)
    {
        int charItemId = data["CharItemID"]?.Value<int>() ?? 0;
        int itemId = data["ItemID"]?.Value<int>() ?? 0;
        lock (_stateLock)
        {
            if (charItemId > 0)
                Inventory.RemoveAll(i => i.CharItemID == charItemId);
            else if (itemId > 0)
                Inventory.RemoveAll(i => i.ID == itemId);
        }
    }

    private void HandleBankFromInv(JObject data)
    {
        int success = data["bSuccess"]?.Value<int>() ?? (data["bSuccess"]?.Value<bool>() == true ? 1 : 0);
        if (success != 1)
            return;

        int itemId = data["ItemID"]?.Value<int>() ?? 0;
        int charItemId = data["CharItemID"]?.Value<int>() ?? 0;

        lock (_stateLock)
        {
            InventoryItem? item = null;
            if (charItemId > 0)
                item = Inventory.FirstOrDefault(i => i.CharItemID == charItemId);
            if (item is null && itemId > 0)
                item = Inventory.FirstOrDefault(i => i.ID == itemId);

            if (item is not null)
            {
                Inventory.Remove(item);
                var existingInBank = Bank.FirstOrDefault(b => b.ID == item.ID);
                if (existingInBank is not null)
                {
                    existingInBank.Quantity = item.Quantity;
                }
                else
                {
                    Bank.Add(item);
                }
            }
            else if (itemId > 0)
            {
                var existingInBank = Bank.FirstOrDefault(b => b.ID == itemId);
                if (existingInBank is null)
                {
                    Bank.Add(new InventoryItem { ID = itemId });
                }
            }

            var houseItem = HouseItems.FirstOrDefault(h => (charItemId > 0 && h.CharItemID == charItemId) || (itemId > 0 && h.ID == itemId));
            if (houseItem is not null)
                HouseItems.Remove(houseItem);
        }
    }

    private void HandleBankToInv(JObject data)
    {
        int success = data["bSuccess"]?.Value<int>() ?? (data["bSuccess"]?.Value<bool>() == true ? 1 : 0);
        if (success != 1)
            return;

        int itemId = data["ItemID"]?.Value<int>() ?? 0;
        int charItemId = data["CharItemID"]?.Value<int>() ?? 0;

        lock (_stateLock)
        {
            InventoryItem? item = null;
            if (charItemId > 0)
                item = Bank.FirstOrDefault(i => i.CharItemID == charItemId);
            if (item is null && itemId > 0)
                item = Bank.FirstOrDefault(i => i.ID == itemId);

            if (item is not null)
            {
                Bank.Remove(item);
                var existingInInv = Inventory.FirstOrDefault(i => i.ID == item.ID);
                if (existingInInv is not null)
                {
                    existingInInv.Quantity = item.Quantity;
                }
                else
                {
                    Inventory.Add(item);
                }
            }
            else if (itemId > 0)
            {
                var existingInInv = Inventory.FirstOrDefault(i => i.ID == itemId);
                if (existingInInv is null)
                {
                    Inventory.Add(new InventoryItem { ID = itemId });
                }
            }
        }
    }

    private void HandleBankSwapInv(JObject data)
    {
        int success = data["bSuccess"]?.Value<int>() ?? (data["bSuccess"]?.Value<bool>() == true ? 1 : 0);
        if (success != 1)
            return;

        int invItemId = data["invItemID"]?.Value<int>() ?? data["ItemID"]?.Value<int>() ?? 0;
        int bankItemId = data["bankItemID"]?.Value<int>() ?? 0;

        lock (_stateLock)
        {
            if (invItemId > 0)
            {
                var invItem = Inventory.FirstOrDefault(i => i.ID == invItemId);
                if (invItem is not null)
                {
                    Inventory.Remove(invItem);
                    Bank.Add(invItem);
                }
            }
            if (bankItemId > 0)
            {
                var bankItem = Bank.FirstOrDefault(i => i.ID == bankItemId);
                if (bankItem is not null)
                {
                    Bank.Remove(bankItem);
                    Inventory.Add(bankItem);
                }
            }
        }
    }

    private void HandleLoadBank(JObject data)
    {
        if (data["items"] is JArray bankArray)
        {
            string bankJson = NormalizeFlashJson(bankArray.ToString());
            lock (_stateLock)
            {
                Bank = JsonConvert.DeserializeObject<List<InventoryItem>>(bankJson) ?? new List<InventoryItem>();
            }
        }
    }

    private void HandleLoadShop(JObject data)
    {
        ShopID = data["ShopID"]?.Value<int>() ?? 0;
        ShopName = data["sName"]?.Value<string>() ?? string.Empty;
        if (data["items"] is JArray shopItemsArray)
        {
            string shopJson = NormalizeFlashJson(shopItemsArray.ToString());
            ShopItems = JsonConvert.DeserializeObject<List<ShopItem>>(shopJson) ?? new List<ShopItem>();
        }
    }

    private void HandleGetQuests(JObject data)
    {
        if (data["quests"] is JObject questsObj)
        {
            foreach (var prop in questsObj.Properties())
            {
                if (prop.Value is JObject qObj)
                {
                    RawQuests[prop.Name] = qObj;
                    if (int.TryParse(prop.Name, out int qId))
                    {
                        try
                        {
                            Quest? quest = JsonConvert.DeserializeObject<Quest>(qObj.ToString());
                            if (quest is not null)
                                Quests[qId] = quest;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
    }

    private void HandleEquipItem(JObject data)
    {
        int uid = data["uid"]?.Value<int>() ?? 0;
        if (uid > 0 && RoomEntId > 0 && uid != RoomEntId)
            return;

        int itemId = data["ItemID"]?.Value<int>() ?? 0;
        string strES = data["strES"]?.Value<string>() ?? string.Empty;
        string sType = data["sType"]?.Value<string>() ?? string.Empty;

        lock (_stateLock)
        {
            var itemToEquip = Inventory.FirstOrDefault(i => i.ID == itemId);
            if (itemToEquip is not null)
            {
                foreach (var item in Inventory.Where(i => i.Equipped))
                {
                    bool sameSlot = item.Category == itemToEquip.Category;

                    if (sameSlot && item.ID != itemId)
                    {
                        item.Equipped = false;
                    }
                }

                itemToEquip.Equipped = true;

                if (itemToEquip.Category == ItemCategory.Class || sType.Equals("Class", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentClassRank = itemToEquip.Level;
                }
            }
        }
    }

    private void HandleUnequipItem(JObject data)
    {
        int uid = data["uid"]?.Value<int>() ?? 0;
        if (uid > 0 && RoomEntId > 0 && uid != RoomEntId)
            return;

        int itemId = data["ItemID"]?.Value<int>() ?? 0;
        lock (_stateLock)
        {
            var item = Inventory.FirstOrDefault(i => i.ID == itemId);
            if (item is not null)
                item.Equipped = false;
        }
    }

    private void HandleActions(JObject data)
    {
        if (data["actions"]?["active"] is JArray activeArray)
        {
            try
            {
                var skills = JsonConvert.DeserializeObject<List<SkillInfo>>(activeArray.ToString());
                if (skills is not null && skills.Count > 0)
                {
                    for (int i = 0; i < skills.Count; i++)
                    {
                        skills[i].ActID = i;
                        skills[i].IsOk = true;
                    }
                    lock (_stateLock)
                    {
                        Skills = skills;
                    }
                }
            }
            catch
            {
            }
        }
    }

    private void HandleUotls(JObject data)
    {
        if (data["o"] is JObject uotlsData)
        {
            string unm = data["unm"]?.Value<string>() ?? string.Empty;
            if (string.Equals(unm, Username, StringComparison.OrdinalIgnoreCase))
            {
                HP = uotlsData["intHP"]?.Value<int>() ?? HP;
                MaxHP = uotlsData["intHPMax"]?.Value<int>() ?? MaxHP;
                MP = uotlsData["intMP"]?.Value<int>() ?? MP;
                MaxMP = uotlsData["intMPMax"]?.Value<int>() ?? MaxMP;
                State = HP <= 0 ? 0 : (State == 0 ? 1 : State);
                if (uotlsData["strFrame"] is not null) Cell = uotlsData["strFrame"]!.Value<string>() ?? Cell;
                if (uotlsData["strPad"] is not null) Pad = uotlsData["strPad"]!.Value<string>() ?? Pad;
                if (uotlsData["afk"] is not null) IsAfk = uotlsData["afk"]!.Value<bool>();
            }
        }

        if (string.Equals(data["typ"]?.Value<string>(), "m", StringComparison.OrdinalIgnoreCase))
        {
            int monMapId = data["id"]?.Value<int>() ?? 0;
            if (monMapId > 0 && data["o"] is JObject monO)
            {
                var mon = MapMonsters.FirstOrDefault(m => m.MapID == monMapId);
                if (mon is not null)
                {
                    mon.HP = monO["intHP"]?.Value<int>() ?? mon.HP;
                    mon.State = mon.HP <= 0 ? 0 : 2;
                    if (mon.HP <= 0 && TargetMonster?.MapID == monMapId)
                    {
                        TargetMonster = null;
                    }
                }
            }
        }
    }

    private void HandleRespawnMon(JObject data)
    {
        int rMapId = data["MonMapID"]?.Value<int>() ?? data["monMapID"]?.Value<int>() ?? data["id"]?.Value<int>() ?? 0;
        int rHp = data["intHP"]?.Value<int>() ?? 0;
        var respawnedMon = MapMonsters.FirstOrDefault(m => m.MapID == rMapId);
        if (respawnedMon is not null)
        {
            respawnedMon.HP = rHp > 0 ? rHp : respawnedMon.MaxHP;
            respawnedMon.State = 1;
        }
    }

    public static string NormalizeFlashJson(string json)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains(".0"))
            return json;

        StringBuilder sb = new(json.Length);
        bool inQuotes = false;
        bool escaped = false;
        int length = json.Length;

        for (int i = 0; i < length; i++)
        {
            char c = json[i];
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
                continue;
            }

            if (!inQuotes && c == '.' && i + 1 < length && json[i + 1] == '0'
                && (i + 2 == length || !char.IsDigit(json[i + 2])))
            {
                i++; // skip .0
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
