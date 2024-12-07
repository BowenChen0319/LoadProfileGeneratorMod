using CalculationEngine.HouseholdElements;
using CalculationEngine.ReinforcementLearning;
using Common;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalculationEngine.ReinforcementLearning
{
    public static class RL
    {
        /// <summary>
        /// Gets the gamma and alpha values for the Q-Learning algorithm in reinforcement learning (RL).
        /// </summary>
        /// <returns></returns>
        public static (double, double) GetGemmaAndAlpha()
        {
            return (0.8, 0.2);
        }

        /// <summary>
        /// Selects the best affordance from a list of available affordances using an adapted Q-Learning algorithm.
        /// </summary>
        /// <param name="time">The current simulation time step.</param>
        /// <param name="allAvailableAffordances">A list of all affordances available for selection.</param>
        /// <param name="now">The current time.</param>
        /// <param name="PersonName">The name of the person for whom the affordance is being selected.</param>
        /// <param name="isHumanInterventionInvolved">
        /// A flag indicating whether human intervention affects affordance selection.
        /// </param>
        /// <param name="qTable">
        /// A reference to the Q-Table used for storing and updating state-action values.
        /// </param>
        /// <param name="PersonDesires">The person's desire states, used to calculate rewards and future states.</param>
        /// <param name="executedAffordance">
        /// A reference to a dictionary storing affordances executed in the past, used for decision constraints.
        /// </param>
        /// <param name="searchCounter">
        /// A reference to the search counter tracking the total number of affordances evaluated.
        /// </param>
        /// <param name="foundCounter">
        /// A reference to the found counter tracking the number of valid affordances found.
        /// </param>
        /// <returns>The best affordance determined by the Q-Learning algorithm.</returns>
        /// <remarks>
        /// This method implements an adapted Q-Learning algorithm to find the best affordance based on the
        /// current state, past actions, and predictions for future rewards. It supports parallel computation
        /// for affordance evaluation and updates the Q-Table with learned values.
        /// </remarks>
        /// <exception cref="LPGException">Thrown if the Q-Table cannot be loaded or updated.</exception>

        public static ICalcAffordanceBase GetBestAffordanceFromList_Adapted_Q_Learning_RL([JetBrains.Annotations.NotNull] TimeStep time,
                                                      [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, DateTime now, string PersonName, bool isHumanInterventionInvolved, ref QTable qTable, CalcPersonDesires PersonDesires, ref Dictionary<DateTime, (string, string)> executedAffordance, ref int searchCounter, ref int foundCounter)
        {
            (double gamma, double alpha) = GetGemmaAndAlpha();
            //If the QTable is empty, then load it from the file
            if (time.InternalStep == 0 && qTable.Table.IsEmpty)
            {
                qTable = QTable.LoadQTableFromFile_RL(PersonName);
            }

            //Experience Replay for better Update Rate
            var localQTable = qTable;
            var localPersonDesires = PersonDesires;
            Q_Learning_Experience_Replay_RL(time.InternalStep, localQTable);

            //Initilize the variables
            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;

            var desire_ValueBefore = PersonDesires.GetCurrentDesireValue_Linear();
            var desire_level_before = MergeDictAndLevels_RL(desire_ValueBefore);
            StateInfo currentState = new StateInfo(desire_level_before, MakeTimeSpan_RL(now, 0));
            int currentSearchCounter = allAvailableAffordances.Count;
            int currentFoundCounter = 0;

            //Initilize the Lock object
            object locker = new object();
            var localExecutedAffordance = isHumanInterventionInvolved ? executedAffordance : null;

            //First prediction, in Parallel Computing       
            Parallel.ForEach(allAvailableAffordances, affordance =>
            {
                //If the affordance is a replacement activity, then skip it
                if (affordance.Name.Contains("Replacement Activity"))
                {
                    Interlocked.Increment(ref currentFoundCounter);
                    return;
                }

                //Hyperparameters
                bool existsInPastThreeHoursCurrent = false;
                DateTime threeHoursAgo = now.AddHours(-3);

                //initiliz the Search and Found Counter
                int affordanceSearchCounter = 0;
                int affordanceFoundCounter = 0;

                //Check is this affordance and his similar affordance already executed in the last 3 hours
                if (isHumanInterventionInvolved)
                {
                    existsInPastThreeHoursCurrent = localExecutedAffordance
                       .Where(kvp => kvp.Key >= threeHoursAgo && kvp.Key <= now && kvp.Value.Item2 == affordance.AffCategory)
                       .Any();
                }
                
                (ActionInfo Q_S_A, double R_S_A, StateInfo nextState, double weightSum, DateTime TimeAfter) = Q_Learning_Stage1_RL(affordance, currentState, time, now, ref localQTable, ref localPersonDesires);


                affordanceFoundCounter += Q_S_A.QValue > 0 ? 1 : 0; //Update Counter, if this state is already visited, then increase the FoundCounter

                //Start prediction, variable initialization
                double prediction = R_S_A;
                //StateInfo nextState = new StateInfo(newState.Item1, newState.Item2);
                DateTime nextTime = TimeAfter;

                affordanceSearchCounter++;// Update Counter

                //Get next-step prediction Infomation 
                DateTime endOfDay = now.Date.AddHours(23).AddMinutes(59);
                bool state_after_pass_day = TimeAfter > endOfDay;

                if (!state_after_pass_day)
                {
                    var max_Q_ns = Q_Learning_Stage2_RL(nextState, ref localQTable);
                    prediction += gamma * max_Q_ns;
                    affordanceFoundCounter += max_Q_ns > 0 ? 1 : 0;
                }

                // Update the Q value for the current state and action
                double new_Q_S_A = Q_S_A.QValue == 0 ? R_S_A : (1 - alpha) * Q_S_A.QValue + alpha * prediction;
                double new_R_S_A = Q_S_A.QValue == 0 ? R_S_A : (1 - alpha) * Q_S_A.RValue + alpha * R_S_A;
                ActionInfo actionInfo = new ActionInfo(new_Q_S_A, affordance.GetDuration(), R_S_A, nextState);

                // Use a local variable to avoid using ref parameter inside the lambda

                localQTable.AddOrUpdate(currentState, affordance.Name, actionInfo);

                //Get Best Affordance
                if (new_Q_S_A > bestQ_S_A && !existsInPastThreeHoursCurrent)
                {
                    lock (locker)
                    {
                        if (new_Q_S_A > bestQ_S_A && !existsInPastThreeHoursCurrent)
                        {
                            bestQ_S_A = new_Q_S_A;
                            bestAffordance = affordance;
                        }
                    }
                }

                //Update local Search and Found Counter
                Interlocked.Add(ref currentSearchCounter, affordanceSearchCounter);
                Interlocked.Add(ref currentFoundCounter, affordanceFoundCounter);

                //if sleep in the wait list, then it has the highest priority
                if (weightSum >= 1000)
                {
                    sleep = affordance;
                }

            });

            //Update global daily Search and Found Counter
            searchCounter += currentSearchCounter;
            foundCounter += currentFoundCounter;

            //return affordance
            return bestAffordance;
        }

        /// <summary>
        /// Performs experience replay to improve Q-value updates in the Q-Table.
        /// </summary>
        /// <param name="seed">The random seed used to select states for experience replay.</param>
        /// <param name="qTable">The <see cref="QTable"/> object containing state-action mappings to be updated.</param>
        /// <remarks>
        /// Experience replay selects a subset of states from the Q-Table and updates Q-values for all actions in these states.
        /// This process improves the stability and convergence of the Q-Learning algorithm. The updates are performed in parallel for efficiency.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="qTable"/> is null.
        /// </exception>

        public static void Q_Learning_Experience_Replay_RL(int seed, QTable qTable)
        {
            // Retrieve the gamma and alpha hyperparameters for the Q-Learning algorithm.
            (double gamma, double alpha) = GetGemmaAndAlpha();

            // If the Q-Table is empty, exit the method as there is nothing to update.
            if (qTable.StatesCount == 0)
            {
                return;
            }

            // Determine the number of states to select for experience replay.
            int m = Math.Min(100, qTable.StatesCount);
            // Initialize a random number generator with the given seed.
            Random rand = new Random(seed);
            // Get all state keys from the Q-Table.
            var allStates = qTable.Table.Keys.ToList();
            // Randomly select m states for experience replay.
            var randomStates = allStates.OrderBy(x => rand.Next()).Take(m).ToList();

            // Use a parallel loop to process each selected state for Q-value updates.
            Parallel.ForEach(randomStates, state =>
            {
                // Retrieve the actions associated with the current state.
                if (qTable.Table.TryGetValue(state, out var current_State))
                {
                    // Determine the number of top actions to update (currently all actions).
                    TimeSpan time_state = TimeSpan.Parse(state.TimeOfDay.Substring(2));
                    int numer_of_update_action = current_State.Count;
                    // Select the top actions in the current state based on Q-value.
                    var topActions = current_State.OrderByDescending(action => action.Value.QValue).Take(numer_of_update_action).ToList();

                    // Update Q-values for each top action in the current state.
                    foreach (var bestActionEntry in topActions)
                    {
                        if (bestActionEntry.Key == null)
                        {
                            continue;
                        }

                        // Deconstruct the current action's information.
                        (string bestAction, ActionInfo bestActionInfo) = bestActionEntry;
                        (StateInfo newStateInfo, double R_S_A, double Q_S_A) = (bestActionInfo.NextState, bestActionInfo.RValue, bestActionInfo.QValue);
                        // Calculate if the new state occurs on the same day as the current state.
                        TimeSpan time_newState = TimeSpan.Parse(newStateInfo.TimeOfDay.Substring(2));
                        bool inTheSameDay = time_newState >= time_state;
                        // Initialize prediction with the reward value.
                        double prediction = R_S_A;

                        // Check if the next state exists in the Q-Table and retrieve the best next action.
                        if (qTable.Table.TryGetValue(newStateInfo, out var nextStateActions))
                        {
                            if (inTheSameDay)
                            {
                                var nextActionEntry = nextStateActions
                                    .DefaultIfEmpty()
                                    .MaxBy(action => action.Value.QValue);
                                // Skip updates if no valid next action exists.
                                if (nextActionEntry.Key == null || nextActionEntry.Value.QValue == 0)
                                {
                                    continue;
                                }
                                // Add the discounted future Q-value to the prediction.
                                prediction += gamma * nextActionEntry.Value.QValue;
                            }
                        }

                        // Update the Q-value using the Bellman equation.
                        double new_Q_S_A = (1 - alpha) * Q_S_A + alpha * prediction;
                        // Create a new ActionInfo object with the updated Q-value.
                        var updatedActionInfo = new ActionInfo(new_Q_S_A, bestActionEntry.Value.WeightSum, R_S_A, newStateInfo);
                        // Update the Q-Table with the new action information.
                        current_State.AddOrUpdate(bestAction, updatedActionInfo, (key, oldValue) => updatedActionInfo);
                    }
                }
            });
        }

        /// <summary>
        /// Executes the first stage of the Q-Learning process by calculating the immediate reward, next state, and other parameters
        /// for a given affordance and the current state.
        /// </summary>
        /// <param name="affordance">The affordance being evaluated, which defines its satisfaction values and duration.</param>
        /// <param name="currentState">The current state as a <see cref="StateInfo"/> object.</param>
        /// <param name="time">The current simulation time step.</param>
        /// <param name="now">The current real-world time.</param>
        /// <param name="qTable">A reference to the Q-Table for retrieving or updating state-action values.</param>
        /// <param name="PersonDesires">The desires of the person, used to compute the effect of the affordance.</param>
        /// <returns>
        /// A tuple containing the following components:
        /// <list type="bullet">
        /// <item><description><see cref="ActionInfo"/> Q_S_A: The Q-value and detail infos of the selected state-action pair.</description></item>
        /// <item><description>double R_S_A: The immediate reward for taking the action in the current state.</description></item>
        /// <item><description><see cref="StateInfo"/> newState: The resulting state after applying the affordance's effects.</description></item>
        /// <item><description>double weightSum: The sum of all weight of affordances, which are relevent with this action.</description></item>
        /// <item><description>DateTime TimeAfter: The time after the affordance has been applied.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// This method calculates the immediate reward for taking the affordance, determines the next state, and retrieves
        /// relevant Q-Table information.
        /// </remarks>
        public static (ActionInfo Q_S_A, double R_S_A, StateInfo newState, double weightSum, DateTime TimeAfter)
        Q_Learning_Stage1_RL(
            ICalcAffordanceBase affordance, 
            StateInfo currentState, 
            TimeStep time, 
            DateTime now, 
            ref QTable qTable, 
            ref CalcPersonDesires PersonDesires)
        {
            // Determine the duration of the affordance, either real or standard duration on the time step.
            int duration = time == null ? affordance.GetDuration() : affordance.GetRealDuration(time);
            // Check if the affordance is interruptable.
            bool isInterruptable = affordance.IsInterruptable;
            // Retrieve the satisfaction values for the affordance.
            var satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);

            // Calculate the effect of the affordance on the person's desires.
            // Includes changes in desire levels, total weight, updated desires, and real duration.
            var (desireDiff, weightSum, desire_ValueAfter, realDuration) = PersonDesires.CalcEffect_Partly_Linear(
                duration,
                out var thoughtstring,
                satValue: satisfactionvalues,
                interruptable: isInterruptable
            );

            // Create a string representation of the next state's time span.
            string newTimeState = MakeTimeSpan_RL(now, duration);
            // Merge the updated desire values into levels for the resulting state.
            Dictionary<string, int> desire_level_after = MergeDictAndLevels_RL(desire_ValueAfter);
            // Construct the new state information.
            StateInfo newState = new StateInfo(desire_level_after, newTimeState);

            // Calculate the immediate reward (R_S_A) based on the desire difference.
            // Apply upper bound if the weightSum exceeds a predefined threshold.
            // 1000000 is the big M value, which is used to ensure that the reward is always positive.
            var R_S_A = -desireDiff + 1000000;
            if (weightSum >= 1000)
            {
                R_S_A = 20000000;
            }

            // Retrieve or add the current state-action pair's details from the Q-Table.
            var actionDetails = qTable.GetOrAdd(
                currentState,
                affordance.Name
            );

            // Create an ActionInfo object to represent the current state-action pair.
            ActionInfo Q_S_A = new ActionInfo(actionDetails.QValue, actionDetails.WeightSum, actionDetails.RValue, actionDetails.NextState);
            // Calculate the time after the affordance's duration.
            var TimeAfter = now.AddMinutes(duration);

            // Return the tuple containing the action details, immediate reward, new state, weight sum, and resulting time.
            return (Q_S_A, R_S_A, newState, weightSum, TimeAfter);
        }


        /// <summary>
        /// Executes the second stage of the Q-Learning process by determining the maximum Q-value for a given state.
        /// </summary>
        /// <param name="currentPersonDesireState">The current state as a <see cref="StateInfo"/> object.</param>
        /// <param name="qTable">
        /// A reference to the Q-Table containing state-action mappings used to calculate the maximum Q-value.
        /// </param>
        /// <returns>
        /// The maximum Q-value of all actions associated with the given state.
        /// Returns 0 if the state is not present in the Q-Table or if no actions are associated with it.
        /// </returns>
        /// <remarks>
        /// This method retrieves the actions for a given state from the Q-Table and calculates the maximum Q-value among them.
        /// It is used to evaluate the future potential reward for transitioning to this state.
        /// </remarks>
        public static double Q_Learning_Stage2_RL(StateInfo currentPersonDesireState, ref QTable qTable)
        {
            // Initialize the maximum Q-value to 0.
            double maxQ_Value = 0;

            // Attempt to retrieve the actions associated with the current state from the Q-Table.
            if (qTable.Table.TryGetValue(currentPersonDesireState, out var Q_newState_actions_nS))
            {
                // Check if the retrieved action dictionary is not null and contains actions.
                if (Q_newState_actions_nS != null && Q_newState_actions_nS.Any())
                {
                    // Find the action with the maximum Q-value.
                    var maxAction = Q_newState_actions_nS.MaxBy(action => action.Value.QValue);

                    // If a valid action is found, update the maximum Q-value.
                    if (maxAction.Key != null)
                    {
                        maxQ_Value = maxAction.Value.QValue;
                    }
                }
            }

            // Return the maximum Q-value found for the current state.
            return maxQ_Value;
        }


        /// <summary>
        /// Merges a dictionary of desires and their associated weights and values into a dictionary of desire levels.
        /// </summary>
        /// <param name="desireName_Value_Dict">
        /// A dictionary where the key is the desire name, and the value is a tuple containing:
        /// <list type="bullet">
        /// <item><description>The desire weight as an integer.</description></item>
        /// <item><description>The updated desire value as a double.</description></item>
        /// </list>
        /// </param>
        /// <returns>
        /// A dictionary where the key is the desire name, and the value is the computed desire level as an integer.
        /// Only desires with a weight of 20 or greater are included in the result.
        /// </returns>
        /// <remarks>
        /// This method calculates the desire levels based on their weights and values using scaling slots (levels).
        /// It excludes any desires with weights below 20 from the resulting dictionary.
        /// </remarks>
        public static Dictionary<string, int> MergeDictAndLevels_RL(Dictionary<string, (int, double)> desireName_Value_Dict)
        {
            // Initialize a new dictionary to store the merged desire levels.
            var mergedDict = new Dictionary<string, int>();

            // Iterate through the input dictionary to process each desire.
            foreach (var kvp in desireName_Value_Dict)
            {
                // Extract the desire name (key) and its associated weight and value.
                (var key, var desire_Info) = kvp;
                (int desire_weight, double desire_valueAfter) = desire_Info;
 
                // Determine the scaling slot (level) based on the weight of the desire.
                int slot = 1;
                if (desire_weight >= 1) slot = 2;
                if (desire_weight >= 20) slot = 3;
                if (desire_weight >= 100) slot = 5;

                // Calculate the desire level using the scaling slot.
                var desire_level = (int)(desire_valueAfter * slot);

                // Only include desires with a weight of 20 or greater in the result.
                if (desire_weight >= 20)
                {
                    mergedDict[key] = desire_level;
                }
            }

            // Return the resulting dictionary containing the computed desire levels.
            return mergedDict;
        }

        /// <summary>
        /// Generates a time state string based on the given time and offset, rounded to the nearest 15-minute interval.
        /// </summary>
        /// <param name="time">The starting <see cref="DateTime"/> value.</param>
        /// <param name="offset">The number of minutes to offset the starting time.</param>
        /// <returns>
        /// A string representing the rounded time state. The format is either:
        /// <list type="bullet">
        /// <item><description>"W:HH:mm" for weekdays (Monday to Friday).</description></item>
        /// <item><description>"R:HH:mm" for weekends (Saturday and Sunday).</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// This method calculates a new time by adding the specified offset in minutes to the provided time,
        /// rounds it down to the nearest 15-minute interval, and prefixes the result with either "W:" or "R:" 
        /// depending on whether the new time falls on a weekday or weekend.
        /// </remarks>
        public static string MakeTimeSpan_RL(DateTime time, int offset)
        {
            // Define the rounding unit for minutes (15-minute intervals).
            int unit = 15;
            // Add the specified offset in minutes to the provided time.
            var newTime = time.AddMinutes(offset);
            // Calculate the remainder when the current minutes are divided by the rounding unit.
            var rounded_minutes_new = newTime.Minute % unit;
            // Subtract the remainder to round down to the nearest 15-minute interval.
            newTime = newTime.AddMinutes(-rounded_minutes_new);
            // Determine the prefix based on whether the new time is on a weekend or a weekday.
            string prefix = newTime.DayOfWeek == DayOfWeek.Saturday || newTime.DayOfWeek == DayOfWeek.Sunday ? "R:" : "W:";
            // Format the new time as "HH:mm" and add the prefix.
            string newTimeState = prefix + newTime.ToString("HH:mm");
            // Return the formatted time state string.
            return newTimeState;
        }
    }
}