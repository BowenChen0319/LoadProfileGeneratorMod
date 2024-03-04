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
//  “This product includes software developed by the TU Chemnitz, Prof. Technische Thermodynamik and its contributors.”
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

#region

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Automation;
using Automation.ResultFiles;
using Common;
using Common.SQLResultLogging;
using Common.SQLResultLogging.InputLoggers;
using JetBrains.Annotations;

#endregion

namespace CalculationEngine.HouseholdElements {
    public class CalcPersonDesires {
        private readonly CalcRepo _calcRepo;

        [NotNull]
        private static readonly Dictionary<Tuple<string, HouseholdKey>, int> _persons =
            new Dictionary<Tuple<string, HouseholdKey>, int>();
        [ItemNotNull]
        [NotNull]
        private List<string> _lastAffordances = new List<string>();
        [ItemNotNull]
        [NotNull]
        //private List<TimeStep> _timeOfLastAffordance = new List<TimeStep>();
        private Dictionary<string, TimeStep> _lastAffordanceTime = new Dictionary<string, TimeStep>();
        private Dictionary<string, DateTime> _lastAffordanceDate = new Dictionary<string, DateTime>();
        [NotNull]
        private readonly DateStampCreator _dsc;
        private StreamWriter? _sw;

        public bool _debug_print = false;

        public bool _lookback = false;


        public CalcPersonDesires(CalcRepo calcRepo) {
            _calcRepo = calcRepo;
            Desires = new Dictionary<int, CalcDesire>();
            _persons.Clear();
            _dsc = new DateStampCreator(_calcRepo.CalcParameters);
        }

        [NotNull]
        public Dictionary<int, CalcDesire> Desires { get; }

        public void AddDesires([NotNull] CalcDesire cd) {
            if (!Desires.ContainsKey(cd.DesireID)) {
                Desires.Add(cd.DesireID, cd);
            }
        }

        

        public void ApplyAffordanceEffectNew([NotNull][ItemNotNull] List<CalcDesire> satisfactionvalues, bool randomEffect,
            [NotNull] string affordance, int durationInMinutes, Boolean firsttime, TimeStep currentTimeStep, DateTime now) {
            if (firsttime)
            {
                while (_lastAffordances.Count > 10)
                {
                    _lastAffordances.RemoveAt(0);
                    //_timeOfLastAffordance.RemoveAt(0);

                }

                TimeStep durationAsTimestep = new(durationInMinutes, 0, false);
                _lastAffordances.Add(affordance);
                //_timeOfLastAffordance.Add(currentTimeStep + durationAsTimestep);
                //_timeOfLastAffordance.Add(currentTimeStep);
                var words = affordance.Split(' ');
                string affordanceKey = string.Join(" ", words.Take(3)); // 取 affordance 中的前三个单词

                if (_lastAffordanceTime.ContainsKey(affordanceKey))
                {
                    _lastAffordanceTime[affordanceKey] = currentTimeStep; // 更新时间
                }
                else
                {
                    _lastAffordanceTime.Add(affordanceKey, currentTimeStep); // 添加新条目
                }


                if (_lastAffordanceDate.ContainsKey(affordanceKey))
                {
                    _lastAffordanceDate[affordanceKey] = now; // 更新时间
                }
                else
                {
                    _lastAffordanceDate.Add(affordanceKey, now); // 添加新条目
                }


                //Debug.WriteLine("                              duration" + durationAsTimestep);
                //
            }
            
            foreach (var satisfactionvalue in satisfactionvalues) {
                if (Desires.ContainsKey(satisfactionvalue.DesireID)) {
                    Desires[satisfactionvalue.DesireID].Value += (satisfactionvalue.Value)/durationInMinutes;
                    if (Desires[satisfactionvalue.DesireID].Value > 1) {
                        Desires[satisfactionvalue.DesireID].Value = 1;
                    }
                }
            }

            if (firsttime)
            {
                if (randomEffect)
                {
                    var usedDesires = new Dictionary<CalcDesire, bool>();
                    foreach (var satisfactionvalue in satisfactionvalues)
                    {
                        usedDesires.Add(satisfactionvalue, true);
                    }
                    var desiresArray = new CalcDesire[Desires.Count];
                    Desires.Values.CopyTo(desiresArray, 0);
                    var affectedCount = _calcRepo.Rnd.Next(Desires.Count - usedDesires.Count + 1);
                    for (var i = 0; i < affectedCount; i++)
                    {
                        CalcDesire? d = null;
                        var loopcount = 0;
                        while (d == null)
                        {
                            var selectedkey = _calcRepo.Rnd.Next(Desires.Count);
                            d = desiresArray[selectedkey];
                            loopcount++;
                            if (usedDesires.ContainsKey(d))
                            {
                                d = null;
                            }
                            if (loopcount > 500)
                            {
                                throw new LPGException("Random result failed after 500 tries...");
                            }
                        }
                        d.Value += (decimal)_calcRepo.Rnd.NextDouble();
                        if (d.Value > 1)
                        {
                            d.Value = 1;
                        }
                    }
                }
            }

            
        }

        public void ApplyAffordanceEffect([NotNull][ItemNotNull] List<CalcDesire> satisfactionvalues, bool randomEffect,
    [NotNull] string affordance)
        {
            while (_lastAffordances.Count > 10)
            {
                _lastAffordances.RemoveAt(0);
            }
            _lastAffordances.Add(affordance);
            foreach (var satisfactionvalue in satisfactionvalues)
            {
                if (Desires.ContainsKey(satisfactionvalue.DesireID))
                {
                    Desires[satisfactionvalue.DesireID].Value += satisfactionvalue.Value;
                    if (Desires[satisfactionvalue.DesireID].Value > 1)
                    {
                        Desires[satisfactionvalue.DesireID].Value = 1;
                    }
                }
            }
            if (randomEffect)
            {
                var usedDesires = new Dictionary<CalcDesire, bool>();
                foreach (var satisfactionvalue in satisfactionvalues)
                {
                    usedDesires.Add(satisfactionvalue, true);
                }
                var desiresArray = new CalcDesire[Desires.Count];
                Desires.Values.CopyTo(desiresArray, 0);
                var affectedCount = _calcRepo.Rnd.Next(Desires.Count - usedDesires.Count + 1);
                for (var i = 0; i < affectedCount; i++)
                {
                    CalcDesire? d = null;
                    var loopcount = 0;
                    while (d == null)
                    {
                        var selectedkey = _calcRepo.Rnd.Next(Desires.Count);
                        d = desiresArray[selectedkey];
                        loopcount++;
                        if (usedDesires.ContainsKey(d))
                        {
                            d = null;
                        }
                        if (loopcount > 500)
                        {
                            throw new LPGException("Random result failed after 500 tries...");
                        }
                    }
                    d.Value += (decimal)_calcRepo.Rnd.NextDouble();
                    if (d.Value > 1)
                    {
                        d.Value = 1;
                    }
                }
            }
        }

        public void ApplyDecay([NotNull] TimeStep timestep) {
            foreach (var calcDesire in Desires.Values) {
                calcDesire.ApplyDecay(timestep);
            }
        }

        

        /// <summary>
        /// Test
        /// </summary>
        /// <param name="timestep"></param>
        /// <param name="satisfactionvalues"></param>
        public void ApplyDecayWithoutSomeNew([NotNull] TimeStep timestep, List<CalcDesire> satisfactionvalues)
        {
            //TODO: Consider apply decay to all desires
            // Create a HashSet containing the DesireIDs from satisfactionvalues
            HashSet<int> satisfactionDesireIDs = new HashSet<int>(satisfactionvalues.Select(sv => sv.DesireID));

            // Apply decay to each desire that is not in the HashSet
            foreach (var calcDesire in Desires.Values)
            {
                if (!satisfactionDesireIDs.Contains(calcDesire.DesireID))
                {
                    calcDesire.ApplyDecay(timestep);
                }
            }
        }

        public decimal Getcurrent_TotalWeightedDeviation()
        {
            decimal total = 0;
            //decimal weightsum = 0;
            foreach (var calcDesire in Desires.Values)
            {
                if (calcDesire.Value > 0)
                {
                    total += (1 - calcDesire.Value) * calcDesire.Weight;
                }
                
            }
            return total ;
        }

        public decimal CalcEffect([NotNull][ItemNotNull] IEnumerable<CalcDesire> satisfactionvalues, out string? thoughtstring,
            [NotNull] string affordanceName) {
            // calc decay
            foreach (var calcDesire in Desires.Values) {
                calcDesire.TempValue = calcDesire.Value;
            }
            decimal modifier = 1;
            if (_lastAffordances.Contains(affordanceName)) {
                var index = _lastAffordances.IndexOf(affordanceName);
                for (var i = index; i >= 0; i--) {
                    modifier *= 0.9m;
                }
            }
            // add value
            foreach (var satisfactionvalue in satisfactionvalues) {
                if (Desires.ContainsKey(satisfactionvalue.DesireID)) {
                    var desire = Desires[satisfactionvalue.DesireID];
                    if (desire.TempValue + satisfactionvalue.Value > 1) {
                        desire.TempValue += satisfactionvalue.Value / modifier;
                    }
                    else {
                        desire.TempValue += satisfactionvalue.Value * modifier;
                    }
                }
            }
            // get results
            return CalcTotalDeviation(out thoughtstring);
        }


        //public decimal CalcEffectPartly([NotNull][ItemNotNull] IEnumerable<CalcDesire> satisfactionvalues, out string? thoughtstring,
        //[NotNull] string affordanceName, Boolean interruptable, Boolean careForAll, int duration, TimeStep currentTime)
        public (decimal totalDeviation, double WeightSum) CalcEffectPartly(ICalcAffordanceBase affordance, TimeStep currentTime, Boolean careForAll, out string? thoughtstring, DateTime now)
        {
            //var useNewAlgo = false;
            
            var satisfactionvalues = affordance.Satisfactionvalues;
            var affordanceName = affordance.Name;
            var affordanceCategory = affordance.AffCategory;
            var interruptable = affordance.IsInterruptable;
            var duration = affordance.GetDuration();
            var restTime = 0;

            if (_lookback)
            {
                restTime = affordance.GetRestTimeWindows(currentTime);
            }
            

            if (_debug_print)
            {
                //Debug.WriteLine("     aff: " + affordanceName + "   restTime:  "+restTime);
            }

            var satisfactionvalueRAW = satisfactionvalues;
            // calc decay
            foreach (var calcDesire in Desires.Values)
            {
                calcDesire.TempValue = calcDesire.Value;
            }
            decimal modifier = 1;
            TimeStep edge = new TimeStep(180,0,false);
            //TimeStep edge = new TimeStep(1440, 0, false);
            TimeStep edge1 = new TimeStep(180, 0, false);//diff
            TimeStep edgeWeek = new TimeStep(60 * 24 * 7, 0, false);
            int priorityInfo = 1;
            List<string> whiteList = new List<string> { "go to the toilet", "sleep bed"};
            List<string> whiteList2 = new List<string> { "work", "office","study", "school", "job", "sleep" };
            TimeStep lastTime;
            var words = affordanceName.Split(' ');
            string affordanceKey = string.Join(" ", words.Take(3));
            DateTime lastDate;
            _lastAffordanceDate.TryGetValue(affordanceKey, out lastDate);

            if (_lastAffordanceTime.TryGetValue(affordanceKey, out lastTime))
            {
                bool inWhiteList1 = whiteList.Any(affordance => affordanceName.ToLower().Contains(affordance.ToLower()));

                bool inWhiteList2 = whiteList2.Any(affordance => affordanceCategory.ToLower().Contains(affordance.ToLower()));


                if (inWhiteList1 || inWhiteList2)
                {
                    if ((currentTime - lastTime) < edge1)
                    {
                        priorityInfo = 0;
                    }
                    else
                    {
                        modifier *= 1;
                        priorityInfo = 2;
                    }
                }
                else
                {
                    if ((currentTime - lastTime) < edge)
                    {
                        //modifier *= 0.1m;
                        //priorityInfo = 0;
                        if (now.Date == lastDate.Date)
                        {

                            //modifier *= 0.1m;
                            modifier *= 1;
                            priorityInfo = 0;
                        }
                    }
                    if (duration > 120 && (currentTime - lastTime) < edgeWeek)//action, which too long
                    {
                        //modifier *= 0.1m;
                        modifier *= 1;
                        priorityInfo = 0;
                    }
                }
            }
            else
            {
                bool inWhiteList1 = whiteList.Any(affordance => affordanceName.ToLower().Contains(affordance.ToLower()));

                bool inWhiteList2 = whiteList2.Any(affordance => affordanceCategory.ToLower().Contains(affordance.ToLower()));

                if (inWhiteList1 || inWhiteList2)
                {
                    modifier *= 1;
                    priorityInfo = 2;
                }

            }

            // add value
            foreach (var satisfactionvalue in satisfactionvalues)
            {
                if (Desires.ContainsKey(satisfactionvalue.DesireID))
                {
                    var desire = Desires[satisfactionvalue.DesireID];
                    if (desire.TempValue + satisfactionvalue.Value > 1)
                    {
                        //if (interruptable == true)
                        //{
                        //    //desire.TempValue += (satisfactionvalue.Value / modifier) /duration;
                        //    satisfactionvalue.Value = (satisfactionvalue.Value / modifier) / duration;
                        //}
                        //else
                        //{
                        //    //desire.TempValue += satisfactionvalue.Value / modifier;
                        //    satisfactionvalue.Value = satisfactionvalue.Value / modifier;
                        //}
                        desire.TempValue += satisfactionvalue.Value / modifier;
                    }
                    else
                    {
                        //if (interruptable == true)
                        //{
                        //    //desire.TempValue += (satisfactionvalue.Value * modifier) /duration;
                        //    satisfactionvalue.Value = (satisfactionvalue.Value * modifier) / duration;
                        //}
                        //else
                        //{
                        //    //desire.TempValue += satisfactionvalue.Value * modifier;
                        //    satisfactionvalue.Value = satisfactionvalue.Value * modifier;
                        //}
                        desire.TempValue += satisfactionvalue.Value * modifier;
                    }
                }
            }
            // get results
            if (careForAll == true)
            {
                if (interruptable == true)
                {
                    return CalcTotalDeviationAllasAreaNew(1, satisfactionvalues, out thoughtstring, priorityInfo, restTime);
                }
                else
                {
                    //Debug.WriteLine("Date   " + now.Date);
                    return CalcTotalDeviationAllasAreaNew(duration, satisfactionvalues, out thoughtstring, priorityInfo, restTime);
                }

                //return CalcTotalDeviationAllasArea(duration, satisfactionvalues, out thoughtstring, alreadyUsed);
                //return CalcTotalDeviationAll(duration, satisfactionvalues, out thoughtstring, alreadyUsed);
            }
            else
            {
                return (CalcEffect(satisfactionvalues, out thoughtstring, affordanceName), -1);
            }
            //return CalcTotalDeviation(out thoughtstring);


        }

        public (decimal totalDeviation, double WeightSum, Dictionary<string, (double,double)> desireName_ValueAfterApply_Dict, Dictionary<string, (double, double)> desireName_ValueBeforeApply_Dict) CalcEffectPartlyRL(ICalcAffordanceBase affordance, TimeStep currentTime, Boolean careForAll, out string? thoughtstring, DateTime now, List<double>? optionalList = null, List<CalcDesire>? satValue = null, int? newDuration = 0)
        {
            
            List<CalcDesire> satisfactionvalues;
            //var affordanceName = affordance.Name;
            //var affordanceCategory = affordance.AffCategory;
            bool interruptable;
            int duration;
            if(newDuration != 0)
            {
                duration = (int)newDuration;
            }
            else
            {
                duration = affordance.GetDuration();
            }

            if (satValue != null)
            {
                satisfactionvalues = satValue;
            }
            else
            {
                satisfactionvalues =  affordance.Satisfactionvalues;
            }

            if (affordance != null)
            {
                interruptable = affordance.IsInterruptable;
            }
            else
            {
                interruptable = false;
            }

            //var restTime = 0;

            if (_debug_print)
            {
                //Debug.WriteLine("     aff: " + affordanceName + "   restTime:  "+restTime);
            }

            var satisfactionvalueRAW = satisfactionvalues;
            // calc decay

            foreach (var calcDesire in Desires.Values)
            {
                calcDesire.TempValue = calcDesire.Value;
            }
            //decimal modifier = 1;
            if (interruptable == true)
            {
                return CalcTotalDeviationAllasAreaNewRL(1, satisfactionvalues, out thoughtstring,optionalList);
            }
            else
            {
                return CalcTotalDeviationAllasAreaNewRL(duration, satisfactionvalues, out thoughtstring,optionalList);
            }

        }

        private (decimal totalDeviation, double WeightSum, Dictionary<string, (double,double)> desireName_ValueAfterApply, Dictionary<string, (double, double)> desireName_ValueBeforeApply) CalcTotalDeviationAllasAreaNewRL(int duration, IEnumerable<CalcDesire> satisfactionvalues, out string? thoughtstring, List<double>? optionalList = null)
        {
            Dictionary<string, (double,double)> desireName_ValueAfterApply_Dict = new Dictionary<string, (double,double)>();
            Dictionary<string, (double, double)> desireName_ValueBeforeApply_Dict = new Dictionary<string, (double, double)>();

            decimal totalDeviation = 0;
            StringBuilder? sb = null;
            var makeThoughts = _calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile);
            if (makeThoughts)
            {
                sb = new StringBuilder(_calcRepo.CalcParameters.CSVCharacter);
            }

            var satisfactionValueDictionary = satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);

            double weight_sum = 0;

            int index = 0;

            foreach (var calcDesire in Desires.Values)
            {
                var desireID = calcDesire.DesireID;
                satisfactionValueDictionary.TryGetValue(desireID, out var satisfactionvalueDBL);

                var decayrate = calcDesire.GetDecayRateDoulbe();
                var currentValueDBL = (double)calcDesire.TempValue;
                var weightDBL = (double)calcDesire.Weight;

                if (optionalList != null && optionalList.Count == Desires.Values.Count)
                {
                    // 从optionalList获取对应位置的值作为currentValueDBL的值
                    currentValueDBL = optionalList[index];
                }

                index++;

                desireName_ValueBeforeApply_Dict[calcDesire.Name] = ((double)calcDesire.Weight, currentValueDBL);

                //var short_duration = 30;
                var short_duration = int.MaxValue;

                if (satisfactionvalueDBL > 0)
                {
                    weight_sum += weightDBL;
                }

                //double Deviration = 1 - currentValueDBL;
                double deviration = 0;
                //double profitValue = 0;
                double updateValue = currentValueDBL;
                if (satisfactionvalueDBL > 0)
                {
                    if (duration >= short_duration)
                    {
                        deviration += ((1 - updateValue) + Math.Max(0, 1 - updateValue - satisfactionvalueDBL)) * Math.Min(((1 - updateValue) / (satisfactionvalueDBL / duration)), duration) / 2;
                    }
                    else
                    {
                        for (var i = 0; i < duration; i++)
                        {
                            updateValue = Math.Min(1, updateValue + (satisfactionvalueDBL / duration));
                            deviration += (1 - updateValue);
                        }
                    }
                 
                }
                else
                {
                    if (duration >= short_duration)
                    {
                        deviration += duration - (updateValue / (Math.Log(decayrate)) * (Math.Pow(decayrate, duration) - 1));
                    }
                    else
                    {
                        for (var i = 0; i < duration; i++)
                        {
                            updateValue *= decayrate;
                            deviration += (1 - updateValue);
                        }
                    }
                    //for (var i = 0; i < duration; i++)
                    //{
                    //    updateValue *= decayrate;
                    //    profitValue += (1 - updateValue);
                    //}
                }

                desireName_ValueAfterApply_Dict[calcDesire.Name] = ((double)calcDesire.Weight, updateValue);

                //profitValue = profitValue * weightDBL / duration;
                var weightedDeviration = deviration * weightDBL;

                totalDeviation += (decimal)weightedDeviration;

                if (sb != null)
                {
                    var deviation = (((1 - (decimal)currentValueDBL) + (1 - (decimal)updateValue)) / 2) * 100;
                    sb.Append(calcDesire.Name);
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append("'");
                    sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                    sb.Append("*");
                    sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                    sb.Append("*");
                    sb.Append(calcDesire.Weight.ToString("0#.#", Config.CultureInfo));
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter);
                    sb.Append(deviration.ToString("0#.#", Config.CultureInfo));
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append(" ");
                }
            }

            weight_sum = weight_sum < 1 ? 1 : weight_sum;

            thoughtstring = sb?.ToString() ?? null;

            return (totalDeviation, weight_sum, desireName_ValueAfterApply_Dict,desireName_ValueBeforeApply_Dict);
        }

        public (double totalDeviation, double WeightSum, Dictionary<string, (double, double)> desireName_ValueAfterApply_Dict, Dictionary<string, (double, double)> desireName_ValueBeforeApply_Dict) CalcEffectPartlyRL_New(ICalcAffordanceBase affordance, TimeStep currentTime, Boolean careForAll, out string? thoughtstring, DateTime now, List<double>? optionalList = null, Dictionary<int,double>? satValue = null, int? newDuration = 0)
        {

            //List<CalcDesire> satisfactionvalues;
            Dictionary<int, double> satisfactionvalues;
            //var affordanceName = affordance.Name;
            //var affordanceCategory = affordance.AffCategory;
            bool interruptable;
            int duration;
            if (newDuration != 0)
            {
                duration = (int)newDuration;
            }
            else
            {
                duration = affordance.GetDuration();
            }

            if (satValue != null)
            {
                satisfactionvalues = satValue;
            }
            else
            {
                satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value); ;
            }

            if (affordance != null)
            {
                interruptable = affordance.IsInterruptable;
            }
            else
            {
                interruptable = false;
            }

            //var restTime = 0;

            if (_debug_print)
            {
                //Debug.WriteLine("     aff: " + affordanceName + "   restTime:  "+restTime);
            }

            var satisfactionvalueRAW = satisfactionvalues;
            // calc decay

            foreach (var calcDesire in Desires.Values)
            {
                calcDesire.TempValue = calcDesire.Value;
            }
            //decimal modifier = 1;
            if (interruptable == true)
            {
                return CalcTotalDeviationAllasAreaNewRL_New(1, satisfactionvalues, out thoughtstring, optionalList);
            }
            else
            {
                return CalcTotalDeviationAllasAreaNewRL_New(duration, satisfactionvalues, out thoughtstring, optionalList);
            }

        }

        private (double totalDeviation, double WeightSum, Dictionary<string, (double, double)> desireName_ValueAfterApply, Dictionary<string, (double, double)> desireName_ValueBeforeApply) CalcTotalDeviationAllasAreaNewRL_New(int duration, Dictionary<int,double> satisfactionvaluesDict, out string? thoughtstring, List<double>? optionalList = null)
        {
            Dictionary<string, (double, double)> desireName_ValueAfterApply_Dict = new Dictionary<string, (double, double)>();
            Dictionary<string, (double, double)> desireName_ValueBeforeApply_Dict = new Dictionary<string, (double, double)>();

            double totalDeviation = 0;
            StringBuilder? sb = null;
            var makeThoughts = _calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile);
            if (makeThoughts)
            {
                sb = new StringBuilder(_calcRepo.CalcParameters.CSVCharacter);
            }

            //var satisfactionValueDictionary = satisfactionvalues.ToDictionary(s => s.DesireID, s => s.Value);
            var satisfactionValueDictionary = satisfactionvaluesDict;

            double weight_sum = 0;

            int index = 0;

            foreach (var calcDesire in Desires.Values)
            {
                var desireID = calcDesire.DesireID;
                satisfactionValueDictionary.TryGetValue(desireID, out var satisfactionvalueDBL);

                
                var decayrate = calcDesire.GetDecayRateDoulbe();
                var currentValueDBL = (double)calcDesire.TempValue;
                var weightDBL = (double)calcDesire.Weight;

                if (optionalList != null && optionalList.Count == Desires.Values.Count)
                {
                    // 从optionalList获取对应位置的值作为currentValueDBL的值
                    currentValueDBL = optionalList[index];
                }

                index++;

                desireName_ValueBeforeApply_Dict[calcDesire.Name] = ((double)calcDesire.Weight, currentValueDBL);

                //var short_duration = 30;
                //var short_duration = int.MaxValue;

                if (satisfactionvalueDBL > 0)
                {
                    weight_sum += weightDBL;
                }

                //double Deviration = 1 - currentValueDBL;
                double deviration = 0;
                //double profitValue = 0;
                double updateValue = currentValueDBL;
                if (satisfactionvalueDBL > 0)
                {
                    for (var i = 0; i < duration; i++)
                    {
                        updateValue = Math.Min(1, updateValue + (satisfactionvalueDBL / duration));
                        deviration += (1 - updateValue);
                    }

                }
                else
                {
                    for (var i = 0; i < duration; i++)
                    {
                        updateValue *= decayrate;
                        deviration += (1 - updateValue);
                    }
                    
                }

                desireName_ValueAfterApply_Dict[calcDesire.Name] = ((double)calcDesire.Weight, updateValue);

                //profitValue = profitValue * weightDBL / duration;
                var weightedDeviration = deviration * weightDBL;

                totalDeviation += weightedDeviration;

                if (sb != null)
                {
                    var deviation = (((1 - currentValueDBL) + (1 - updateValue)) / 2) * 100;
                    sb.Append(calcDesire.Name);
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append("'");
                    sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                    sb.Append("*");
                    sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                    sb.Append("*");
                    sb.Append(calcDesire.Weight.ToString("0#.#", Config.CultureInfo));
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter);
                    sb.Append(deviration.ToString("0#.#", Config.CultureInfo));
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append(" ");
                }
            }

            weight_sum = weight_sum < 1 ? 1 : weight_sum;

            thoughtstring = sb?.ToString() ?? null;

            return (totalDeviation, weight_sum, desireName_ValueAfterApply_Dict, desireName_ValueBeforeApply_Dict);
        }

        private (decimal totalDeviation, double WeightSum) CalcTotalDeviationAllasAreaNew(int duration, IEnumerable<CalcDesire> satisfactionvalues, out string? thoughtstring, int priorityInfo, int restTime)
        {
            decimal totalDeviation = 0;
            StringBuilder? sb = null;
            var makeThoughts = _calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile);
            if (makeThoughts)
            {
                sb = new StringBuilder(_calcRepo.CalcParameters.CSVCharacter);
            }

            var satisfactionValueDictionary = satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);

            double weight_sum = 0;



            //Debug.WriteLine("Desires Count: " + Desires.Values.Count + "  Date: ");
            //get last element from Desires.Values


            //Debug.WriteLine("Last Desire Name: " + Desires.Values.Last().Name);

            foreach (var calcDesire in Desires.Values)
            {
                var desireID = calcDesire.DesireID;
                satisfactionValueDictionary.TryGetValue(desireID, out var satisfactionvalueDBL);

                var decayrate = calcDesire.GetDecayRateDoulbe();
                var currentValueDBL = (double)calcDesire.TempValue;
                var weightDBL = (double)calcDesire.Weight;

                var short_duration = 30;
                //short_duration = int.MaxValue;

                if (satisfactionvalueDBL > 0)
                {
                    weight_sum += weightDBL;
                }

                double profitValue = 1 - currentValueDBL;
                //double profitValue = 0;
                double updateValue = currentValueDBL;
                if (satisfactionvalueDBL > 0)
                {
                    if (duration >= short_duration)
                    {
                        profitValue += ((1 - updateValue) + Math.Max(0, 1 - updateValue - satisfactionvalueDBL)) * Math.Min(((1 - updateValue) / (satisfactionvalueDBL / duration)), duration) / 2;
                    }
                    else
                    {
                        for (var i = 0; i < duration; i++)
                        {
                            updateValue = Math.Min(1, updateValue + (satisfactionvalueDBL / duration));
                            profitValue += (1 - updateValue);
                        }
                    }
                    //for (var i = 0; i < duration; i++)
                    //{
                    //    updateValue = Math.Min(1, updateValue + (satisfactionvalueDBL / duration));
                    //    profitValue += (1 - updateValue);
                    //}


                }
                else
                {
                    if (duration >= short_duration)
                    {
                        profitValue += duration - (updateValue / (Math.Log(decayrate)) * (Math.Pow(decayrate, duration) - 1));
                    }
                    else
                    {
                        for (var i = 0; i < duration; i++)
                        {
                            updateValue *= decayrate;
                            profitValue += (1 - updateValue);
                        }
                    }
                    //for (var i = 0; i < duration; i++)
                    //{
                    //    updateValue *= decayrate;
                    //    profitValue += (1 - updateValue);
                    //}



                }

                profitValue = profitValue * weightDBL / duration;
                totalDeviation += (decimal)profitValue;

                if (sb != null)
                {
                    var deviation = (((1 - (decimal)currentValueDBL) + (1 - (decimal)updateValue)) / 2) * 100;
                    sb.Append(calcDesire.Name);
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append("'");
                    sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                    sb.Append("*");
                    sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                    sb.Append("*");
                    sb.Append(calcDesire.Weight.ToString("0#.#", Config.CultureInfo));
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter);
                    sb.Append(profitValue.ToString("0#.#", Config.CultureInfo));
                    sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append(" ");
                }
            }

            weight_sum = weight_sum < 1 ? 1 : weight_sum;

            thoughtstring = sb?.ToString() ?? null;

            if (priorityInfo == 0)
            {
                return (1000000000000000, weight_sum);
            }
            else
            {
                return (totalDeviation, weight_sum);
            }
        }


        private decimal CalcTotalDeviation(out string? thoughtstring) {
            decimal totalDeviation = 0;
            StringBuilder? sb = null;
            var makeThoughts = _calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile);
            if (makeThoughts) {
                sb = new StringBuilder(_calcRepo.CalcParameters.CSVCharacter);
            }

            foreach (var calcDesire in Desires.Values) {
                if (calcDesire.TempValue < calcDesire.Threshold || calcDesire.TempValue > 1) {
                    var deviation = (1 - calcDesire.TempValue) * 100;
                    var desirevalue = deviation * deviation * calcDesire.Weight;
                    totalDeviation += desirevalue;
                    if (sb!=null) {
                        sb.Append(calcDesire.Name);
                        sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append("'");
                        sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                        sb.Append("*");
                        sb.Append(deviation.ToString("0#.#", Config.CultureInfo));
                        sb.Append("*");
                        sb.Append(calcDesire.Weight.ToString("0#.#", Config.CultureInfo));
                        sb.Append(_calcRepo.CalcParameters.CSVCharacter);
                        sb.Append(desirevalue.ToString("0#.#", Config.CultureInfo));
                        sb.Append(_calcRepo.CalcParameters.CSVCharacter).Append(" ");
                    }
                }
            }
            if (sb!=null) {
                thoughtstring = sb.ToString();
            }
            else {
                thoughtstring = null;
            }
            //thoughtstring = null;
            return totalDeviation;
        }


        public void CheckForCriticalThreshold([NotNull] CalcPerson person, [NotNull] TimeStep time, [NotNull] FileFactoryAndTracker fft,
                                              [NotNull] HouseholdKey householdKey) {
            if ( time.ExternalStep < 0 &&
                !time.ShowSettling) {
                return;
            }

            var builder = new StringBuilder();
            foreach (var calcDesire in Desires) {
                if (calcDesire.Value.CriticalThreshold > 0) {
                    if (calcDesire.Value.Value < calcDesire.Value.CriticalThreshold) {
                        builder.Append("1");
                    }
                    else {
                        builder.Append("0");
                    }
                    builder.Append(_calcRepo.CalcParameters.CSVCharacter);
                }
            }
            if (builder.Length > 0) {
                var sb = new StringBuilder();
                _dsc.GenerateDateStampForTimestep(time, sb);

                if (_sw == null) {
                    var personNumber = _persons.Count;
                    _persons.Add(new Tuple<string, HouseholdKey>(person.Name, householdKey), personNumber);
                    _sw = fft.MakeFile<StreamWriter>(
                        "CriticalThresholdViolations." + householdKey + "." + person + ".csv",
                        "Lists the critical threshold violations for " + person, true,
                        ResultFileID.CriticalThresholdViolations, householdKey,
                        TargetDirectory.Debugging, _calcRepo.CalcParameters.InternalStepsize, CalcOption.CriticalViolations, null,person.MakePersonInformation());
                    var header = _dsc.GenerateDateStampHeader();
                    foreach (var calcDesire in Desires) {
                        if (calcDesire.Value.CriticalThreshold > 0) {
#pragma warning disable CC0039 // Don't concatenate strings in loops
                            header += calcDesire.Value.Name;
                            header += _calcRepo.CalcParameters.CSVCharacter;
#pragma warning restore CC0039 // Don't concatenate strings in loops
                        }
                    }
                    if(_sw==null) {
                        throw new LPGException("SW was null");
                    }
                    _sw.WriteLine(header);
                }
                sb.Append(builder);
                _sw.WriteLine(sb);
            }
        }

        public void CopyOtherDesires([NotNull] CalcPersonDesires otherdesires) {
            foreach (var calcDesire in Desires.Values) {
                if (calcDesire.DecayTime < 100) {
                    calcDesire.Value = 1;
                }
                if (otherdesires.Desires.ContainsKey(calcDesire.DesireID)) {
                    var tmpdes = otherdesires.Desires[calcDesire.DesireID];
                    calcDesire.Value = tmpdes.Value;
                }
            }
            if (_sw == null && otherdesires._sw != null) {
                _sw = otherdesires._sw;
            }
        }

        public bool HasAtLeastOneDesireBelowThreshold([NotNull] ICalcAffordanceBase aff) {
            foreach (var desire in aff.Satisfactionvalues) {
                if (Desires.ContainsKey(desire.DesireID)) {
                    if (Desires[desire.DesireID].Value < Desires[desire.DesireID].Threshold) {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}