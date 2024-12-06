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
    }

    public class ActionInfo(double qValue, int weightSum, double rValue, StateInfo nextState)
    {
        public double QValue { get; set; } = qValue;

        public int WeightSum { get; set; } = weightSum;

        public double RValue { get; set; } = rValue;

        public StateInfo NextState { get; set; } = nextState;
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

        public void SaveQTableToFile_RL(string personName)
        {
            (string baseDir2, string filePath) = GetQTablePath(personName);
            var convertedQTable = new Dictionary<string, string>();

            foreach (var outerEntry in Table)
            {
                var outerKeyDictSerialized = string.Join("±", outerEntry.Key.DesireStates.Select(d => $"{d.Key}⦿{d.Value}"));
                var outerKey = $"{outerKeyDictSerialized}§{outerEntry.Key.TimeOfDay}";

                var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                    $"{innerEntry.Key}¶{innerEntry.Value.QValue}‖{innerEntry.Value.WeightSum}‖{innerEntry.Value.RValue}‖" +
                    $"{string.Join("¥", innerEntry.Value.NextState.DesireStates.Select(d => $"{d.Key}○{d.Value}"))}♯{innerEntry.Value.NextState.TimeOfDay}"
                );
                convertedQTable[outerKey] = string.Join("★", innerDictSerialized);
            }

            if (!Directory.Exists(baseDir2))
            {
                Directory.CreateDirectory(baseDir2);
            }

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

        public static QTable LoadQTableFromFile_RL(string personName)
        {
            Debug.WriteLine("Now Loading QTable from file...");
            QTable readedQTable = new QTable();
            (string baseDir, string filePath) = GetQTablePath(personName);
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            if (File.Exists(filePath))
            {
                try
                {

                    var jsonString = File.ReadAllText(filePath);
                    var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);


                    var loadedQTable = new ConcurrentDictionary<StateInfo, ConcurrentDictionary<string, ActionInfo>>();

                    foreach (var outerEntry in convertedQTable)
                    {

                        var outerKeyParts = outerEntry.Key.Split('§');
                        var desireStates = outerKeyParts[0]
                            .Split('±')
                            .Select(p => p.Split('⦿'))
                            .ToDictionary(p => p[0], p => int.Parse(p[1]));
                        var timeOfDay = outerKeyParts[1];
                        var outerKey = new StateInfo(desireStates, timeOfDay);


                        var innerDict = new ConcurrentDictionary<string, ActionInfo>();
                        var innerEntries = outerEntry.Value.Split(new string[] { "★" }, StringSplitOptions.None);
                        foreach (var innerEntry in innerEntries)
                        {
                            var parts = innerEntry.Split('¶');
                            var actionName = parts[0];
                            var valueParts = parts[1].Split('‖');


                            var qValue = double.Parse(valueParts[0]);
                            var weightSum = int.Parse(valueParts[1]);
                            var rValue = double.Parse(valueParts[2]);
                            var nextStateParts = valueParts[3].Split('♯');


                            var nextStateDesireStates = nextStateParts[0]
                                .Split('¥')
                                .Select(p => p.Split('○'))
                                .ToDictionary(p => p[0], p => int.Parse(p[1]));
                            var nextStateTimeOfDay = nextStateParts[1];
                            var nextState = new StateInfo(nextStateDesireStates, nextStateTimeOfDay);


                            innerDict[actionName] = new ActionInfo(qValue, weightSum, rValue, nextState);
                        }


                        loadedQTable[outerKey] = innerDict;
                    }

                    foreach (var kvp in loadedQTable)
                    {
                        readedQTable.Table[kvp.Key] = kvp.Value;
                    }

                    Debug.WriteLine("QTable has been successfully loaded from " + filePath);
                    Logger.Info("QTable has been successfully loaded from " + filePath);
                    return readedQTable;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error loading QTable: " + ex.Message);
                    Logger.Info("Error loading QTable: " + ex.Message);
                    throw new LPGException("SW was null");
                    return null;
                }
            }
            else
            {
                Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
                Logger.Info("No saved QTable found. Initializing a new QTable.");
                return new QTable();
            }
        }
    }
}