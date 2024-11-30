//using CalculationEngine.HouseholdElements;
//using Common;
//using JetBrains.Annotations;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Speech.Recognition;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//public class RL
//{

//    /// <summary>
//    /// Selects the optimal affordance from a list of available affordances using an adapted Q-Learning algorithm 
//    /// in a reinforcement learning (RL) framework. This method evaluates each affordance, calculates predicted rewards, 
//    /// and updates the Q-Table for the current state-action pairs.
//    ///
//    /// <b>Inputs:</b>
//    /// - <paramref name="time"/>: The current time step of the simulation.
//    /// - <paramref name="allAvailableAffordances"/>: A list of affordances that can be performed at the current state.
//    /// - <paramref name="now"/>: The current DateTime in the simulation.
//    ///
//    /// <b>Outputs:</b>
//    /// - Returns the affordance with the highest predicted Q-value (that is not recently executed, to ensure diversity).
//    ///
//    /// <b>Details:</b>
//    /// 1. <b>Initialization:</b>
//    ///    - Loads the Q-Table from the file if it is empty.
//    ///    - Performs experience replay to update Q-values based on past experiences.
//    ///    - Sets up variables to track the best affordance, current state, and counters for search and found actions.
//    ///
//    /// 2. <b>Parallel Evaluation:</b>
//    ///    - Processes each affordance in parallel to improve computational efficiency.
//    ///    - For each affordance:
//    ///       - Computes immediate rewards (R_S_A) and next state information (newState) using <c>Q_Learning_Stage1_RL</c>.
//    ///       - Predicts future rewards using n-step predictions from <c>Q_Learning_Stage2_RL</c>.
//    ///       - Updates the Q-value in the Q-Table using the Bellman equation.
//    ///       (Optional) - Considers constraints like avoiding recently executed affordances and prioritizes critical actions (e.g., sleep).
//    ///
//    /// 3. <b>Selection of Best Affordance:</b>
//    ///    - Maintains a thread-safe mechanism to select the affordance with the highest Q-value.
//    ///    - Skips affordances marked as "Replacement Activity" to focus on meaningful actions.
//    ///
//    /// 4. <b>Global Metrics Update:</b>
//    ///    - Updates global counters to track search and found actions for monitoring learning efficiency.
//    ///
//    /// <b>Usage:</b>
//    /// - Invoke this method during the RL process to determine the next action to take based on the learned policy.
//    /// - Ensure the Q-Table is populated and updated regularly for accurate predictions.
//    /// - Use the returned affordance to execute the next step in the simulation.
//    ///
//    /// <b>Key Parameters:</b>
//    /// - <c>alpha</c>: Learning rate, balances new information and existing knowledge.
//    /// - <c>gamma</c>: Discount factor, determines the weight of future rewards in decision-making.
//    /// - <c>currentState</c>: Represents the current state as a combination of desire levels and time information.
//    ///
//    /// This method is critical to the RL process, enabling dynamic and intelligent decision-making based on 
//    /// learned state-action relationships, ensuring efficient and adaptive simulation behavior.
//    /// </summary>

//    private ICalcAffordanceBase GetBestAffordanceFromList_Adapted_Q_Learning_RL([JetBrains.Annotations.NotNull] TimeStep time,
//                                                  [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, DateTime now)
//    {
//        //If the QTable is empty, then load it from the file
//        if (qTable.Table.IsEmpty)
//        {
//            LoadQTableFromFile_RL();
//        }

//        //Experience Replay for better Update Rate
//        Q_Learning_Experience_Replay_RL(time.InternalStep);

//        //Initilize the variables
//        var bestQ_S_A = double.MinValue;
//        var bestAffordance = allAvailableAffordances[0];
//        ICalcAffordanceBase sleep = null;

//        var desire_ValueBefore = PersonDesires.GetCurrentDesireValue_Linear();
//        var desire_level_before = MergeDictAndLevels_RL(desire_ValueBefore);
//        (Dictionary<string, int>, string) currentState = (desire_level_before, MakeTimeSpan_RL(now, 0));

//        int currentSearchCounter = allAvailableAffordances.Count;
//        int currentFoundCounter = 0;

//        //Initilize the Lock object
//        object locker = new object();

//        //First prediction, in Parallel Computing
//        Parallel.ForEach(allAvailableAffordances, affordance =>
//        {
//            //If the affordance is a replacement activity, then skip it
//            if (affordance.Name.Contains("Replacement Activity"))
//            {
//                Interlocked.Increment(ref currentFoundCounter);
//                return;
//            }

//            //Hyperparameters
//            double alpha = 0.2;

//            bool existsInPastThreeHoursCurrent = false;
//            DateTime threeHoursAgo = now.AddHours(-3);

//            //initiliz the Search and Found Counter
//            int affordanceSearchCounter = 0;
//            int affordanceFoundCounter = 0;

//            //Check is this affordance and his similar affordance already executed in the last 3 hours
//            if (isHumanInterventionInvolved)
//            {
//                existsInPastThreeHoursCurrent = executedAffordance
//                   .Where(kvp => kvp.Key >= threeHoursAgo && kvp.Key <= now && kvp.Value.Item2 == affordance.AffCategory)
//                   .Any();
//            }


//            //GetNextStateAndQ_R_Value(state, time,now)// input: State, time, now // output: Q, R, next state
//            var firstStageQ_Learning_Info = Q_Learning_Stage1_RL(affordance, currentState, time, now);
//            var R_S_A = firstStageQ_Learning_Info.R_S_A;
//            var Q_S_A = firstStageQ_Learning_Info.Q_S_A;
//            var newState = firstStageQ_Learning_Info.newState;
//            var weightSum = firstStageQ_Learning_Info.weightSum;
//            var TimeAfter = firstStageQ_Learning_Info.TimeAfter;

//            affordanceFoundCounter += Q_S_A.Item1 > 0 ? 1 : 0; //Update Counter, if this state is already visited, then increase the FoundCounter


//            //Start prediction, variable initialization
//            double prediction = R_S_A;
//            //(Dictionary<string, int>, string) nextState = newState; //new state
//            StateInfo nextState = new StateInfo(newState.Item1, newState.Item2);
//            DateTime nextTime = TimeAfter;

//            affordanceSearchCounter++;// Update Counter

//            //Get next-step prediction Infomation 

//            DateTime endOfDay = now.Date.AddHours(23).AddMinutes(59);
//            bool state_after_pass_day = TimeAfter > endOfDay;

//            if (!state_after_pass_day)
//            {
//                var max_Q_ns = Q_Learning_Stage2_RL(nextState);
//                prediction += gamma * max_Q_ns;
//                affordanceFoundCounter += max_Q_ns > 0 ? 1 : 0;
//            }

//            // Update the Q value for the current state and action

//            double new_Q_S_A = Q_S_A.Item1 == 0 ? R_S_A : (1 - alpha) * Q_S_A.Item1 + alpha * prediction;
//            double new_R_S_A = Q_S_A.Item1 == 0 ? R_S_A : (1 - alpha) * Q_S_A.Item3 + alpha * R_S_A;
//            //var QSA_Info = (new_Q_S_A, affordance.GetDuration(), R_S_A, firstStageQ_Learning_Info.newState);
//            StateInfo stateToAdd = new StateInfo(currentState.Item1, currentState.Item2);
//            StateInfo stateToAddNext = new StateInfo(newState.Item1, newState.Item2);
//            ActionInfo actionInfo = new ActionInfo(new_Q_S_A, affordance.GetDuration(), R_S_A, stateToAddNext);
//            qTable.AddOrUpdate(stateToAdd, affordance.Name, actionInfo);
//            //var currentStateData = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, double, (Dictionary<string, int>, string))>());
//            //currentStateData.AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);

//            //Get Best Affordance
//            if (new_Q_S_A > bestQ_S_A && !existsInPastThreeHoursCurrent)
//            {
//                lock (locker)
//                {
//                    if (new_Q_S_A > bestQ_S_A && !existsInPastThreeHoursCurrent)
//                    {
//                        bestQ_S_A = new_Q_S_A;
//                        bestAffordance = affordance;
//                    }
//                }
//            }

//            //Update local Search and Found Counter
//            Interlocked.Add(ref currentSearchCounter, affordanceSearchCounter);
//            Interlocked.Add(ref currentFoundCounter, affordanceFoundCounter);

//            //if sleep in the wait list, then it has the highest priority
//            if (weightSum >= 1000)
//            {
//                sleep = affordance;
//            }

//        });

//        //Update global daily Search and Found Counter
//        searchCounter += currentSearchCounter;
//        foundCounter += currentFoundCounter;

//        //return affordance
//        return bestAffordance;

//    }
//}
