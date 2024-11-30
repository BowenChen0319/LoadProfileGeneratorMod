//-----------------------------------------------------------------------

// <copyright>
//
// Copyright (c) TU Chemnitz, Prof. Technische Thermodynamik
// Written by Noah Pflugradt.
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//  Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the distribution.
//  All advertising materials mentioning features or use of this software must display the following acknowledgement:
//  This product includes software developed by the TU Chemnitz, Prof. Technische Thermodynamik and its contributors.
//  Neither the name of the University nor the names of its contributors may be used to endorse or promote products
//  derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE UNIVERSITY 'AS IS' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE UNIVERSITY OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, S
// PECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; L
// OSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
// STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
// ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

// </copyright>

//-----------------------------------------------------------------------


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Common;



public class  StateInfo
{
    public Dictionary<string, int> DesireStates { get; }
    public string TimeOfDay { get; }

    public StateInfo(Dictionary<string, int> desireStates, string timeOfDay)
    {
        DesireStates = desireStates;
        TimeOfDay = timeOfDay;
    }
    public bool Equals(StateInfo other)
    {
        if (other == null)
        {
            return false;
        }

        if (!TimeOfDay.Equals(other.TimeOfDay))
        {
            return false;
        }

        if (DesireStates.Count != other.DesireStates.Count)
        {
            return false;
        }

        foreach (var kvp in DesireStates)
        {
            if (!other.DesireStates.TryGetValue(kvp.Key, out var value) || kvp.Value != value)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object obj)
    {
        return Equals(obj as StateInfo);
    }

    public override int GetHashCode()
    {
        int hash = 17;

        hash = hash * 23 + TimeOfDay.GetHashCode();

        foreach (var kvp in DesireStates.OrderBy(kvp => kvp.Key))
        {
            hash = hash * 23 + kvp.Key.GetHashCode();
            hash = hash * 23 + kvp.Value.GetHashCode();
        }

        return hash;
    }
}

public class  ActionInfo
{
    public double QValue { get; set; }

    public int weightSum { get; set; }

    public double RValue { get; set; }
    
    public StateInfo nextState { get; set; }

    public ActionInfo(double qValue, int weightSum, double rValue, StateInfo nextState)
    {
        QValue = qValue;
        this.weightSum = weightSum;
        RValue = rValue;
        this.nextState = nextState;
    }
}

public class QTable
{
    public ConcurrentDictionary<StateInfo, ConcurrentDictionary<string, ActionInfo>> Table { get; }

    public QTable()
    {
        Table = new ConcurrentDictionary<StateInfo, ConcurrentDictionary<string, ActionInfo>>();
    }

    public int Count => Table.Count;

    public void AddOrUpdate(StateInfo state, string action, ActionInfo actionDetails)
    {
        Table.AddOrUpdate(
            state,
            new ConcurrentDictionary<string, ActionInfo> { [action] = actionDetails },
            (_, existingActions) =>
            {
                existingActions.AddOrUpdate(action, actionDetails, (_, __) => actionDetails);
                return existingActions;
            });
    }

    public ActionInfo GetOrAdd(StateInfo state, string action, ActionInfo defaultActionInfo)
    {
        // 获取或添加状态的动作字典
        var actions = Table.GetOrAdd(
            state,
            _ => new ConcurrentDictionary<string, ActionInfo>()
        );

        // 获取或添加具体的动作信息
        return actions.GetOrAdd(action, _ => defaultActionInfo);
    }


    //public ConcurrentDictionary<string, actionInfo> GetActions(PersonDesireState state)
    //{
    //    return Table.TryGetValue(state, out var actions) ? actions : null;
    //}

    //public bool RemoveAction(PersonDesireState state, string action)
    //{
    //    if (Table.TryGetValue(state, out var actions))
    //    {
    //        return actions.TryRemove(action, out _);
    //    }
    //    return false;
    //}

    //public bool RemoveState(PersonDesireState state)
    //{
    //    return Table.TryRemove(state, out _);
    //}

    public void SaveQTableToFile_RL(string personName)
    {
        string baseDir2 = @"C:\Work\ML\Models";

        var convertedQTable = new Dictionary<string, string>();

        foreach (var outerEntry in Table)
        {
            var outerKeyDictSerialized = string.Join("±", outerEntry.Key.DesireStates.Select(d => $"{d.Key}⦿{d.Value}"));
            var outerKey = $"{outerKeyDictSerialized}§{outerEntry.Key.TimeOfDay}";

            var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                $"{innerEntry.Key}¶{innerEntry.Value.QValue}‖{innerEntry.Value.weightSum}‖{innerEntry.Value.RValue}‖" +
                $"{string.Join("¥", innerEntry.Value.nextState.DesireStates.Select(d => $"{d.Key}○{d.Value}"))}♯{innerEntry.Value.nextState.TimeOfDay}"
            );
            convertedQTable[outerKey] = string.Join("★", innerDictSerialized);
        }

        if (!Directory.Exists(baseDir2))
        {
            Directory.CreateDirectory(baseDir2);
        }

        string sanitizedPersonName = personName.Replace("/", "_");
        string filePath = Path.Combine(baseDir2, $"qTable-{sanitizedPersonName}.json");

        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(convertedQTable, options);
            File.WriteAllText(filePath, jsonString);
            Debug.WriteLine("QTable has been successfully saved to " + filePath);
            Logger.Info("QTable has been successfully saved to " + filePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error saving QTable: " + ex.Message);
            Logger.Info("Error saving QTable: " + ex.Message);
        }
    }

    public void LoadQTableFromFile_RL(string personName)
    {
        Debug.WriteLine("Now Loading QTable from file...");
        string baseDir = @"C:\Work\ML\Models";

        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        string sanitizedPersonName = personName.Replace("/", "_");
        string filePath = Path.Combine(baseDir, $"qTable-{sanitizedPersonName}.json");

        if (File.Exists(filePath))
        {
            try
            {
                // 从文件读取 JSON 数据
                var jsonString = File.ReadAllText(filePath);
                var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);

                // 创建新的 QTable 实例
                var loadedQTable = new ConcurrentDictionary<StateInfo, ConcurrentDictionary<string, ActionInfo>>();

                foreach (var outerEntry in convertedQTable)
                {
                    // 反序列化外层键（PersonDesireState）
                    var outerKeyParts = outerEntry.Key.Split('§');
                    var desireStates = outerKeyParts[0]
                        .Split('±')
                        .Select(p => p.Split('⦿'))
                        .ToDictionary(p => p[0], p => int.Parse(p[1]));
                    var timeOfDay = outerKeyParts[1];
                    var outerKey = new StateInfo(desireStates, timeOfDay);

                    // 反序列化内层动作字典
                    var innerDict = new ConcurrentDictionary<string, ActionInfo>();
                    var innerEntries = outerEntry.Value.Split(new string[] { "★" }, StringSplitOptions.None);
                    foreach (var innerEntry in innerEntries)
                    {
                        var parts = innerEntry.Split('¶');
                        var actionName = parts[0];
                        var valueParts = parts[1].Split('‖');

                        // 解析 actionInfo 的字段
                        var qValue = double.Parse(valueParts[0]);
                        var weightSum = int.Parse(valueParts[1]);
                        var rValue = double.Parse(valueParts[2]);
                        var nextStateParts = valueParts[3].Split('♯');

                        // 解析 nextState
                        var nextStateDesireStates = nextStateParts[0]
                            .Split('¥')
                            .Select(p => p.Split('○'))
                            .ToDictionary(p => p[0], p => int.Parse(p[1]));
                        var nextStateTimeOfDay = nextStateParts[1];
                        var nextState = new StateInfo(nextStateDesireStates, nextStateTimeOfDay);

                        // 构建 actionInfo 并添加到内层字典
                        innerDict[actionName] = new ActionInfo(qValue, weightSum, rValue, nextState);
                    }

                    // 添加到 QTable
                    loadedQTable[outerKey] = innerDict;
                }

                // 替换当前实例的 QTable
                this.Table.Clear();
                foreach (var kvp in loadedQTable)
                {
                    this.Table[kvp.Key] = kvp.Value;
                }

                Debug.WriteLine("QTable has been successfully loaded from " + filePath);
                Logger.Info("QTable has been successfully loaded from " + filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading QTable: " + ex.Message);
                Logger.Info("Error loading QTable: " + ex.Message);

                // 初始化一个空的 QTable
                this.Table.Clear();
            }
        }
        else
        {
            Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
            Logger.Info("No saved QTable found. Initializing a new QTable.");
            this.Table.Clear();
        }
    }
}


