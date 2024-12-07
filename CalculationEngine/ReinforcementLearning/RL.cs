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
        /// Selects the optimal affordance from a list of available affordances using an adapted Q-Learning algorithm 
        /// in a reinforcement learning (RL) framework. This method evaluates each affordance, calculates predicted rewards, 
        /// and updates the Q-Table for the current state-action pairs.
        ///
        /// <b>Inputs:</b>
        /// - <paramref name="time"/>: The current time step of the simulation.
        /// - <paramref name="allAvailableAffordances"/>: A list of affordances that can be performed at the current state.
        /// - <paramref name="now"/>: The current DateTime in the simulation.
        ///
        /// <b>Outputs:</b>
        /// - Returns the affordance with the highest predicted Q-value (that is not recently executed, to ensure diversity).
        ///
        /// <b>Details:</b>
        /// 1. <b>Initialization:</b>
        ///    - Loads the Q-Table from the file if it is empty.
        ///    - Performs experience replay to update Q-values based on past experiences.
        ///    - Sets up variables to track the best affordance, current state, and counters for search and found actions.
        ///
        /// 2. <b>Parallel Evaluation:</b>
        ///    - Processes each affordance in parallel to improve computational efficiency.
        ///    - For each affordance:
        ///       - Computes immediate rewards (R_S_A) and next state information (newState) using <c>Q_Learning_Stage1_RL</c>.
        ///       - Predicts future rewards using n-step predictions from <c>Q_Learning_Stage2_RL</c>.
        ///       - Updates the Q-value in the Q-Table using the Bellman equation.
        ///       (Optional) - Considers constraints like avoiding recently executed affordances and prioritizes critical actions (e.g., sleep).
        ///
        /// 3. <b>Selection of Best Affordance:</b>
        ///    - Maintains a thread-safe mechanism to select the affordance with the highest Q-value.
        ///    - Skips affordances marked as "Replacement Activity" to focus on meaningful actions.
        ///
        /// 4. <b>Global Metrics Update:</b>
        ///    - Updates global counters to track search and found actions for monitoring learning efficiency.
        ///
        /// <b>Usage:</b>
        /// - Invoke this method during the RL process to determine the next action to take based on the learned policy.
        /// - Ensure the Q-Table is populated and updated regularly for accurate predictions.
        /// - Use the returned affordance to execute the next step in the simulation.
        ///
        /// <b>Key Parameters:</b>
        /// - <c>alpha</c>: Learning rate, balances new information and existing knowledge.
        /// - <c>gamma</c>: Discount factor, determines the weight of future rewards in decision-making.
        /// - <c>currentState</c>: Represents the current state as a combination of desire levels and time information.
        ///
        /// This method is critical to the RL process, enabling dynamic and intelligent decision-making based on 
        /// learned state-action relationships, ensuring efficient and adaptive simulation behavior.
        /// </summary>
        public static (double, double) GetGemmaAndAlpha()
        {
            return (0.8, 0.2);
        }


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
            //(Dictionary<string, int>, string) currentState = (desire_level_before, MakeTimeSpan_RL(now, 0));
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

                //GetNextStateAndQ_R_Value(state, time,now)// input: State, time, now // output: Q, R, next state
                var firstStageQ_Learning_Info = Q_Learning_Stage1_RL(affordance, currentState, time, now, ref localQTable, ref localPersonDesires);
                var R_S_A = firstStageQ_Learning_Info.R_S_A;
                var Q_S_A = firstStageQ_Learning_Info.Q_S_A;
                var nextState = firstStageQ_Learning_Info.newState;
                var weightSum = firstStageQ_Learning_Info.weightSum;
                var TimeAfter = firstStageQ_Learning_Info.TimeAfter;

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
        /// Implements experience replay for reinforcement learning (RL) by revisiting previously encountered states
        /// in the Q-Table and updating their Q-values to refine the learned policy. This mechanism enhances
        /// learning stability and efficiency by allowing the agent to learn from past experiences without
        /// needing additional interactions with the environment.
        ///
        /// <b>Inputs:</b>
        /// - <paramref name="seed"/>: An integer seed used for generating random numbers to ensure reproducibility.
        ///
        /// <b>Details:</b>
        /// 1. <b>State Selection:</b>
        ///    - Randomly selects up to 100 states from the Q-Table or the total number of states if fewer than 100 exist.
        ///    - Uses a random number generator to ensure diverse state sampling.
        ///
        /// 2. <b>Q-Value Update:</b>
        ///    - For each selected state, retrieves its associated actions and their Q-values.
        ///    - Identifies the top actions based on their Q-values and processes each action to update the Q-value:
        ///       - Calculates the predicted Q-value using the Bellman equation, factoring in immediate rewards (R_S_A)
        ///         and future rewards (from the next state).
        ///       - Updates the Q-value in the Q-Table for the current state-action pair.
        ///
        /// 3. <b>Parallel Processing:</b>
        ///    - Leverages parallel computation to update Q-values for multiple states simultaneously,
        ///      improving performance for large Q-Tables.
        ///
        /// <b>Usage:</b>
        /// - Call this method during training phases to reinforce learning without additional environmental interactions.
        /// - Integrate it as part of the RL algorithm to smooth out Q-value updates and improve convergence.
        ///
        /// <b>Key Parameters:</b>
        /// - <c>alpha</c>: The learning rate, controls the weight of new information versus existing knowledge.
        /// - <c>gamma</c>: The discount factor, determines the importance of future rewards (commented out here but configurable).
        ///
        /// <b>Example Workflow:</b>
        /// 1. Initialize the Q-Table during the RL setup phase.
        /// 2. Populate the Q-Table with state-action data through interaction with the environment.
        /// 3. Periodically invoke this method to update Q-values and enhance the policy's performance.
        ///
        /// This method is integral to RL processes, supporting efficient learning through experience replay, which reduces reliance
        /// on real-time interactions and improves policy stability and accuracy over time.
        /// </summary>

        public static void Q_Learning_Experience_Replay_RL(int seed, QTable qTable)
        {
            (double gamma, double alpha) = GetGemmaAndAlpha();
            if (qTable.StatesCount == 0)
            {
                return;
            }
            int m = Math.Min(100, qTable.StatesCount);

            Random rand = new Random(seed);

            // Get all states from Q-Table
            var allStates = qTable.Table.Keys.ToList();

            // Randomly select m states
            var randomStates = allStates.OrderBy(x => rand.Next()).Take(m).ToList();

            // Parallel loop to update Q-values for best actions in selected states
            Parallel.ForEach(randomStates, state =>
            {
                if (qTable.Table.TryGetValue(state, out var current_State))
                {
                    //int numer_of_update_action = Math.Max(1, current_State.Count/4);
                    TimeSpan time_state = TimeSpan.Parse(state.TimeOfDay.Substring(2));
                    int numer_of_update_action = current_State.Count;
                    var topActions = current_State.OrderByDescending(action => action.Value.QValue).Take(numer_of_update_action).ToList();

                    foreach (var bestActionEntry in topActions)
                    {
                        if (bestActionEntry.Key == null)
                        {
                            continue;
                        }

                        var bestAction = bestActionEntry.Key;
                        var newStateInfo = bestActionEntry.Value.NextState;
                        TimeSpan time_newState = TimeSpan.Parse(newStateInfo.TimeOfDay.Substring(2));
                        var R_S_A = bestActionEntry.Value.RValue;
                        var Q_S_A = bestActionEntry.Value.QValue;
                        bool inTheSameDay = time_newState >= time_state;

                        double prediction = R_S_A;


                        if (qTable.Table.TryGetValue(newStateInfo, out var nextStateActions))
                        {
                            if (inTheSameDay)
                            {

                                var nextActionEntry = nextStateActions
                                    .DefaultIfEmpty()
                                    .MaxBy(action => action.Value.QValue);

                                if (nextActionEntry.Key == null || nextActionEntry.Value.QValue == 0)
                                {
                                    continue;
                                }
                                prediction += gamma * nextActionEntry.Value.QValue;
                            }
                        }


                        double new_Q_S_A = (1 - alpha) * Q_S_A + alpha * prediction;


                        var updatedActionInfo = new ActionInfo(new_Q_S_A, bestActionEntry.Value.WeightSum, R_S_A, newStateInfo);

                        current_State.AddOrUpdate(bestAction, updatedActionInfo, (key, oldValue) => updatedActionInfo);
                    }
                }
            });
        }

        /// <summary>
        /// Performs the first stage of Q-Learning for reinforcement learning (RL), calculating the immediate reward (R_S_A),
        /// next state (newState), and Q-value (Q_S_A) for a given affordance and state.
        /// This method evaluates the impact of the affordance by simulating its execution and updating the RL parameters accordingly.
        ///
        /// <b>Inputs:</b>
        /// - <paramref name="affordance"/>: The affordance being evaluated.
        /// - <paramref name="currentState"/>: The current state, represented as a tuple of desires and time state.
        /// - <paramref name="time"/>: The time step of the simulation.
        /// - <paramref name="now"/>: The current simulation time as a DateTime object.
        ///
        /// <b>Outputs:</b>
        /// - <c>Q_S_A</c>: The Q-value associated with the current state-action pair.
        /// - <c>R_S_A</c>: The immediate reward for performing the action in the current state.
        /// - <c>newState</c>: The resulting state after the action is executed.
        /// - <c>weightSum</c>: The summed weights of the desires affected by the action.
        /// - <c>TimeAfter</c>: The simulation time after executing the action.
        ///
        /// <b>Usage:</b>
        /// 1. Call this method during the RL process to evaluate potential actions.
        /// 2. Use the returned Q-value to update the Q-Table or make policy decisions.
        /// 3. Leverage the reward and new state to guide future actions or refine the policy.
        ///
        /// <b>Details:</b>
        /// - The reward (R_S_A) is calculated based on the deviation of desire satisfaction values and prioritized for critical actions.
        /// - The next state is generated by applying the affordance's effects on the current state.
        /// - The method initializes Q-values to zero if the action is not previously encountered in the Q-Table.
        /// - Time and satisfaction are adjusted to reflect the affordance's duration and impact.
        ///
        /// This method is a core part of the RL algorithm, bridging simulation states and the Q-Table for learning.
        /// </summary>
        public static (ActionInfo Q_S_A, double R_S_A, StateInfo newState, double weightSum, DateTime TimeAfter) Q_Learning_Stage1_RL(ICalcAffordanceBase affordance, StateInfo currentState, TimeStep time, DateTime now, ref QTable qTable, ref CalcPersonDesires PersonDesires)
        {
            int duration = time == null ? affordance.GetDuration() : affordance.GetRealDuration(time);
            bool isInterruptable = affordance.IsInterruptable;
            var satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);
            var (desireDiff, weightSum, desire_ValueAfter, realDuration) = PersonDesires.CalcEffect_Partly_Linear(duration, out var thoughtstring, satValue: satisfactionvalues, interruptable: isInterruptable);

            string newTimeState = MakeTimeSpan_RL(now, duration);
            Dictionary<string, int> desire_level_after = MergeDictAndLevels_RL(desire_ValueAfter);
            StateInfo newState = new StateInfo(desire_level_after, newTimeState);

            var R_S_A = -desireDiff + 1000000;
            if (weightSum >= 1000)
            {
                R_S_A = 20000000;
            }


            var actionDetails = qTable.GetOrAdd(
                currentState,
                affordance.Name
            );

            ActionInfo Q_S_A = new ActionInfo(actionDetails.QValue, actionDetails.WeightSum, actionDetails.RValue, actionDetails.NextState);
            var TimeAfter = now.AddMinutes(duration);

            return (Q_S_A, R_S_A, newState, weightSum, TimeAfter);
        }


        /// <summary>
        /// Calculates the maximum Q-value for a given state during the second stage of Q-Learning 
        /// in a reinforcement learning (RL) process. This method identifies the best possible action 
        /// from the current state to predict future rewards effectively.
        ///
        /// <b>Inputs:</b>
        /// - <paramref name="currentState"/>: A tuple representing the current state in the format 
        ///   (<c>Dictionary&lt;string, int&gt;</c>, <c>string</c>), where:
        ///   - The dictionary maps desires to their levels.
        ///   - The string represents the current time state.
        ///
        /// <b>Outputs:</b>
        /// - Returns the maximum Q-value (<c>double</c>) for the given state, based on the best possible action.
        ///
        /// <b>Details:</b>
        /// 1. <b>State Validation:</b>
        ///    - Checks if the Q-Table contains entries for the given state.
        ///    - Ensures that the state has associated actions before proceeding.
        ///
        /// 2. <b>Action Evaluation:</b>
        ///    - Iterates over all actions associated with the state.
        ///    - Identifies the action with the highest Q-value using <c>MaxBy</c>.
        ///    - Extracts the Q-value of the best action for use in reward prediction.
        ///
        /// 3. <b>Output:</b>
        ///    - If valid actions exist, returns the highest Q-value for the given state.
        ///    - Otherwise, returns 0, indicating no viable actions for the state.
        ///
        /// <b>Usage:</b>
        /// - Call this method during the RL process to evaluate future rewards for a given state.
        /// - Use the returned Q-value as part of the Bellman equation to update Q-values in the Q-Table.
        /// - Ensure that the Q-Table is populated with state-action pairs for accurate predictions.
        ///
        /// <b>Example Workflow:</b>
        /// 1. During a Q-Learning update, invoke this method to determine the maximum Q-value of the next state.
        /// 2. Incorporate the result into the reward calculation to refine the policy.
        /// 3. Repeat the process for multiple states to improve the overall learning accuracy.
        ///
        /// This method plays a vital role in the RL algorithm by enabling forward-looking decision-making 
        /// and ensuring that the policy considers the best possible outcomes for a given state.
        /// </summary>
        /// 

        /// <summary>
        /// 
        /// </summary>
        /// <param name="currentPersonDesireState"></param>
        /// <param name="qTable"></param>
        /// <returns></returns>
        public static double Q_Learning_Stage2_RL(StateInfo currentPersonDesireState, ref QTable qTable)
        {
            double maxQ_Value = 0;


            if (qTable.Table.TryGetValue(currentPersonDesireState, out var Q_newState_actions_nS))
            {
                if (Q_newState_actions_nS != null && Q_newState_actions_nS.Any())
                {

                    var maxAction = Q_newState_actions_nS.MaxBy(action => action.Value.QValue);

                    if (maxAction.Key != null)
                    {
                        maxQ_Value = maxAction.Value.QValue;
                    }
                }
            }

            return maxQ_Value;
        }


        /// <summary>
        /// Merges a dictionary of desires with their weights and values into a simplified dictionary for RL.
        /// Each desire's level is calculated based on its weight and value, with higher weights increasing the granularity of levels.
        /// Only desires with a weight of 20 or higher are included in the resulting dictionary.
        /// This method is designed to support reinforcement learning by simplifying the representation of desire states.
        /// </summary>
        public static Dictionary<string, int> MergeDictAndLevels_RL(Dictionary<string, (int, double)> desireName_Value_Dict)
        {
            var mergedDict = new Dictionary<string, int>();

            foreach (var kvp in desireName_Value_Dict)
            {
                var key = kvp.Key;
                var desire_Info = kvp.Value;
                int desire_weight = desire_Info.Item1;
                double desire_valueAfter = desire_Info.Item2;

                int slot = 1;
                if (desire_weight >= 1) slot = 2;
                if (desire_weight >= 20) slot = 3;
                if (desire_weight >= 100) slot = 5;

                //var desire_level = (int)(desire_valueAfter / (1 / slot));
                var desire_level = (int)Math.Floor(desire_valueAfter * slot);

                if (desire_weight >= 20)//20
                {
                    mergedDict[key] = desire_level;
                }

            }

            return mergedDict;
        }

        /// <summary>
        /// Generates a time span string for RL state representation, rounded to the nearest 15-minute interval.
        /// Adjusts the time by a given offset and prefixes the result with "R:" for weekends or "W:" for weekdays.
        /// This method standardizes time representation for use in reinforcement learning processes.
        /// </summary>
        public static string MakeTimeSpan_RL(DateTime time, int offset)
        {
            int unit = 15;
            var newTime = time.AddMinutes(offset);
            var rounded_minutes_new = newTime.Minute % unit;
            newTime = newTime.AddMinutes(-rounded_minutes_new);
            string prefix = newTime.DayOfWeek == DayOfWeek.Saturday || newTime.DayOfWeek == DayOfWeek.Sunday ? "R:" : "W:";
            string newTimeState = prefix + newTime.ToString("HH:mm");
            return newTimeState;
        }
    }
}