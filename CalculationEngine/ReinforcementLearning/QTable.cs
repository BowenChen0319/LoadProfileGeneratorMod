using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Automation.ResultFiles;
using Common;





namespace CalculationEngine.ReinforcementLearning
{
    public record StateInfo(Dictionary<string, int> DesireStates, string TimeOfDay)
    {
        public virtual bool Equals(StateInfo? other)
        {
            if (other == null)
            {
                return false;
            }

            if (!TimeOfDay.Equals(other.TimeOfDay))
            {
                return false;
            }

            //check if the two dictionaries are equal
            return DesireStates.Count == other.DesireStates.Count && !DesireStates.Except(other.DesireStates).Any();

            //return true;
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

        public string Serialize()
        {
            var desireStatesSerialized = string.Join("±", DesireStates.Select(d => $"{d.Key}⦿{d.Value}"));
            return $"{desireStatesSerialized}§{TimeOfDay}";
        }

        public static StateInfo Deserialize(string serialized)
        {
            var parts = serialized.Split('§');
            var desireStates = parts[0]
                .Split('±')
                .Select(p => p.Split('⦿'))
                .ToDictionary(p => p[0], p => int.Parse(p[1]));
            return new StateInfo(desireStates,parts[1]);
        }
    }

    public class ActionInfo(double qValue, int weightSum, double rValue, StateInfo nextState)
    {
        public double QValue { get; set; } = qValue;

        public int WeightSum { get; set; } = weightSum;

        public double RValue { get; set; } = rValue;

        public StateInfo NextState { get; set; } = nextState;

        public string Serialize()
        {
            var nextStateSerialized = NextState.Serialize();
            return $"{QValue}‖{WeightSum}‖{RValue}‖{nextStateSerialized}";
        }

        public static ActionInfo Deserialize(string serialized)
        {
            var parts = serialized.Split('‖');
            var qValue = double.Parse(parts[0]);
            var weightSum = int.Parse(parts[1]);
            var rValue = double.Parse(parts[2]);
            var nextState = StateInfo.Deserialize(parts[3]);

            return new ActionInfo(qValue, weightSum, rValue, nextState);
        }
    }

    public class QTable
    {
        public ConcurrentDictionary<StateInfo, ConcurrentDictionary<string, ActionInfo>> Table { get; } = [];


        public int Count => Table.Count;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="state"></param>
        /// <param name="action"></param>
        /// <param name="actionDetails"></param>
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

        public ActionInfo GetOrAdd(StateInfo state, string action)
        {
            var actions = Table.GetOrAdd(
                state,
                _ => new ConcurrentDictionary<string, ActionInfo>()
            );
            return actions.GetOrAdd(action, _ => new ActionInfo(0, 0, 0, state));
        }

        public static (string,string) GetQTablePath(string personName)
        {
            //string baseDir = @"ActivityModels";
            string baseDir = @"C:\Work\ML\Models";
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }
            string sanitizedPersonName = personName.Replace("/", "_");
            string filePath = Path.Combine(baseDir, $"qTable-{sanitizedPersonName}.json");
            return (baseDir, filePath);
        }

        //public void SaveQTableToFile_RL(string personName)
        //{
        //    (string baseDir2, string filePath) = GetQTablePath(personName);
        //    var convertedQTable = new Dictionary<string, string>();

        //    foreach (var outerEntry in Table)
        //    {
        //        var outerKey = outerEntry.Key.Serialize();
        //        var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
        //            $"{innerEntry.Key}¶{innerEntry.Value.Serialize()}"
        //        );

        //        convertedQTable[outerKey] = string.Join("★", innerDictSerialized);
        //    }

        //    if (!Directory.Exists(baseDir2))
        //    {
        //        Directory.CreateDirectory(baseDir2);
        //    }

        //    try
        //    {
        //        var options = new JsonSerializerOptions { WriteIndented = true };
        //        string jsonString = JsonSerializer.Serialize(convertedQTable, options);
        //        File.WriteAllText(filePath, jsonString);
        //        Debug.WriteLine("QTable has been successfully saved to " + filePath);
        //        Logger.Info("QTable has been successfully saved to " + filePath);
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine("Error saving QTable: " + ex.Message);
        //        Logger.Info("Error saving QTable: " + ex.Message);
        //        throw new LPGException("Error in Q-Table Saving");
        //    }
        //}

        //public static QTable LoadQTableFromFile_RL(string personName)
        //{
        //    (string baseDir2, string filePath) = GetQTablePath(personName);

        //    if (!File.Exists(filePath))
        //    {
        //        Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
        //        Logger.Info("No saved QTable found. Initializing a new QTable.");
        //        return new QTable();
        //    }

        //    try
        //    {
        //        var jsonString = File.ReadAllText(filePath);
        //        var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
        //        QTable readedQTable = new();
        //        foreach (var outerEntry in convertedQTable)
        //        {
        //            var outerKey = StateInfo.Deserialize(outerEntry.Key);
        //            var innerDict = outerEntry.Value
        //                .Split('★')
        //                .Select(innerSerialized =>
        //                {
        //                    var parts = innerSerialized.Split('¶');
        //                    var actionName = parts[0];
        //                    var actionInfo = ActionInfo.Deserialize(parts[1]);
        //                    return new KeyValuePair<string, ActionInfo>(actionName, actionInfo);
        //                })
        //                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        //            readedQTable.Table[outerKey] = new ConcurrentDictionary<string, ActionInfo>(innerDict);
        //        }

        //        Debug.WriteLine("QTable has been successfully loaded from " + filePath);
        //        Logger.Info("QTable has been successfully loaded from " + filePath);
        //        return readedQTable;
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine("Error loading QTable: " + ex.Message);
        //        Logger.Info("Error loading QTable: " + ex.Message);
        //        throw new LPGException("Error in Q-Table Loading");
        //    }
        //}
        public string Serialize()
        {
            var convertedQTable = new Dictionary<string, string>();

            foreach (var outerEntry in Table)
            {
                var outerKey = outerEntry.Key.Serialize();
                var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                    $"{innerEntry.Key}¶{innerEntry.Value.Serialize()}"
                );

                convertedQTable[outerKey] = string.Join("★", innerDictSerialized);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(convertedQTable, options);
        }

        public static QTable Deserialize(string jsonString)
        {
            var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
            QTable qTable = new();

            foreach (var outerEntry in convertedQTable)
            {
                var outerKey = StateInfo.Deserialize(outerEntry.Key);
                var innerDict = outerEntry.Value
                    .Split('★')
                    .Select(innerSerialized =>
                    {
                        var parts = innerSerialized.Split('¶');
                        var actionName = parts[0];
                        var actionInfo = ActionInfo.Deserialize(parts[1]);
                        return new KeyValuePair<string, ActionInfo>(actionName, actionInfo);
                    })
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                qTable.Table[outerKey] = new ConcurrentDictionary<string, ActionInfo>(innerDict);
            }

            return qTable;
        }

        public void SaveQTableToFile_RL(string personName)
        {
            (string baseDir2, string filePath) = GetQTablePath(personName);

            if (!Directory.Exists(baseDir2))
            {
                Directory.CreateDirectory(baseDir2);
            }

            try
            {
                var jsonString = Serialize();
                File.WriteAllText(filePath, jsonString);
                Debug.WriteLine("QTable has been successfully saved to " + filePath);
                Logger.Info("QTable has been successfully saved to " + filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving QTable: " + ex.Message);
                Logger.Info("Error saving QTable: " + ex.Message);
                throw new LPGException("Error in Q-Table Saving");
            }
        }

        public static QTable LoadQTableFromFile_RL(string personName)
        {
            (string baseDir2, string filePath) = GetQTablePath(personName);

            if (!File.Exists(filePath))
            {
                Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
                Logger.Info("No saved QTable found. Initializing a new QTable.");
                return new QTable();
            }

            try
            {
                var jsonString = File.ReadAllText(filePath);
                var qTable = QTable.Deserialize(jsonString);
                Debug.WriteLine("QTable has been successfully loaded from " + filePath);
                Logger.Info("QTable has been successfully loaded from " + filePath);
                return qTable;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading QTable: " + ex.Message);
                Logger.Info("Error loading QTable: " + ex.Message);
                throw new LPGException("Error in Q-Table Loading");
            }
        }
    }
}