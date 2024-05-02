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

#region

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Automation;
using Automation.ResultFiles;
using CalculationEngine.Helper;
using CalculationEngine.OnlineLogging;
using CalculationEngine.Transportation;
using Common;
using Common.CalcDto;
using Common.Enums;
using Common.JSON;
using Common.SQLResultLogging.InputLoggers;
using Database.Tables.BasicHouseholds;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using System.Text.Json.Serialization;
using System.IO;
using System.Runtime.CompilerServices;
using static System.Collections.Specialized.BitVector32;

#endregion

namespace CalculationEngine.HouseholdElements {

    public class CalcPerson : CalcBase {
        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        private readonly BitArray _isBusy;
        [JetBrains.Annotations.NotNull]
        private readonly PotentialAffs _normalPotentialAffs = new PotentialAffs();
        [JetBrains.Annotations.NotNull]
        private readonly CalcPersonDesires _normalDesires;
        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        private readonly List<ICalcAffordanceBase> _previousAffordances;
        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        private readonly List<Tuple<ICalcAffordanceBase, TimeStep>> _previousAffordancesWithEndTime =
            new List<Tuple<ICalcAffordanceBase, TimeStep>>();
        [JetBrains.Annotations.NotNull]
        private readonly PotentialAffs _sicknessPotentialAffs = new PotentialAffs();
        private bool _alreadyloggedvacation;

        private ICalcAffordanceBase? _currentAffordance;

        private bool _isCurrentlyPriorityAffordanceRunning;
        private bool _isCurrentlySick;
        [JetBrains.Annotations.NotNull]
        private readonly CalcPersonDto _calcPerson;

        [JetBrains.Annotations.NotNull]
        public HouseholdKey HouseholdKey => _calcPerson.HouseholdKey;

        private readonly CalcRepo _calcRepo;

        public CalcPerson([JetBrains.Annotations.NotNull] CalcPersonDto calcPerson,
                          [JetBrains.Annotations.NotNull] CalcLocation startingLocation,
                          [JetBrains.Annotations.NotNull][ItemNotNull] BitArray isSick,
                          [JetBrains.Annotations.NotNull][ItemNotNull] BitArray isOnVacation, CalcRepo calcRepo)
            : base(calcPerson.Name, calcPerson.Guid)
        {
            _calcPerson = calcPerson;
            _calcRepo = calcRepo;
            _isBusy = new BitArray(_calcRepo.CalcParameters.InternalTimesteps);
            _normalDesires = new CalcPersonDesires(_calcRepo);
            PersonDesires = _normalDesires;
            SicknessDesires = new CalcPersonDesires(_calcRepo);
            _previousAffordances = new List<ICalcAffordanceBase>();
            IsSick = isSick;
            IsOnVacation = isOnVacation;
            CurrentLocation = startingLocation;
            _vacationAffordanceGuid = System.Guid.NewGuid().ToStrGuid();
        }

        //guid for all vacations of this person
        private readonly StrGuid _vacationAffordanceGuid;
        // use one vacation location guid for all persons
        private static readonly StrGuid _vacationLocationGuid = System.Guid.NewGuid().ToStrGuid();
        [JetBrains.Annotations.NotNull]
        private CalcLocation CurrentLocation { get; set; }

        public int DesireCount => PersonDesires.Desires.Count;

        //public string HouseholdKey => _person_householdKey;

        [JetBrains.Annotations.NotNull]
        [ItemNotNull]
        public BitArray IsOnVacation { get; }

        [JetBrains.Annotations.NotNull]
        [ItemNotNull]
        private BitArray IsSick { get; }

        [JetBrains.Annotations.NotNull]
        public CalcPersonDesires PersonDesires { get; private set; }

        [JetBrains.Annotations.NotNull]
        public CalcPersonDesires SicknessDesires { get; }

        private TimeStep? TimeToResetActionEntryAfterInterruption { get; set; }

        public int ID => _calcPerson.ID;

        public int _remainingExecutionSteps = 0;

        public int _currentDuration = 0;

        public bool _debug_print = false;

        public bool _lookback = false;

        public decimal totalWeightedDeviation = 0;

        public ICalcAffordanceBase _executingAffordance = null;

        public Dictionary<DateTime,Dictionary<DateTime,ICalcAffordanceBase>> AffordanceSequence { get; set; } = new Dictionary<DateTime, Dictionary<DateTime, ICalcAffordanceBase>>();

        public Dictionary<DateTime,Dictionary<string, (string,int)>> TrainingAffordanceSequence { get; set; } = new Dictionary<DateTime, Dictionary<string, (string,int)>>();

        
        public bool firstTimeRecorded = true;

        public int trainingCounter = 0;

        public Dictionary<string, decimal> updatedWeight = new Dictionary<string, decimal>();

        //public Dictionary<(State, string), double> qTable = new Dictionary<(State, string), double>();

        public ConcurrentDictionary<(Dictionary<string,int> , string ), ConcurrentDictionary<string,(double,int,Dictionary<int,double>)>> qTable =  new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());

        public ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>> max_qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());

        public ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>> qTable2 = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());

        public ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>> max_qTable2 = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());


        public (Dictionary<string, int>, string) currentState = (null, null);

        public string next_affordance_name = null;

        public int searchCounter = 0;

        public int foundCounter = 0;

        public int sumSearchCounter = 0;

        public int sumFoundCounter = 0;

        public double averageReward = 0;

        public DateTime lastSleepTime = new DateTime();

        public Dictionary<DateTime, int> TableNumberEachDay = new Dictionary<DateTime, int>();


        [JetBrains.Annotations.NotNull]
        public string PrettyName => _calcPerson.Name + "(" + _calcPerson.Age + "/" + _calcPerson.Gender + ")";

        [JetBrains.Annotations.NotNull]
        public PersonInformation MakePersonInformation() => new PersonInformation(Name, Guid, _calcPerson.TraitTag);

        public bool NewIsBasicallyValidAffordance([JetBrains.Annotations.NotNull] ICalcAffordanceBase aff, bool sickness, bool logDetails)
        {
            // affordanzen l鰏chen, wo alter nicht passt
            if (_calcPerson.Age > aff.MaximumAge || _calcPerson.Age < aff.MiniumAge) {
                if (logDetails) {
                    Logger.Debug("Sickness: " + sickness + " Age doesn't fit");
                }

                return false;
            }

            // affordanzen l鰏chen, wo geschlecht nicht passt
            if (aff.PermittedGender != PermittedGender.All && aff.PermittedGender != _calcPerson.Gender) {
                if (logDetails) {
                    Logger.Debug("Sickness: " + sickness + " Gender doesn't fit");
                }

                return false;
            }
            // affordanzen l鰏chen, die nicht mindestens ein bed黵fnis der Person befriedigen

            CalcPersonDesires desires;
            if (sickness) {
                desires = SicknessDesires;
            }
            else {
                desires = _normalDesires;
            }

            var satisfactionCount = 0;
            foreach (var satisfactionvalue in aff.Satisfactionvalues) {
                if (desires.Desires.ContainsKey(satisfactionvalue.DesireID)) {
                    satisfactionCount++;
                }
            }

            if (!aff.RequireAllAffordances && satisfactionCount > 0) {
                if (logDetails) {
                    Logger.Debug("Sickness: " + sickness + " At least one desire satisfied");
                }

                return true;
            }

            if (aff.RequireAllAffordances && aff.Satisfactionvalues.Count == satisfactionCount) {
                if (logDetails) {
                    Logger.Debug("Sickness: " + sickness + " All required desires satisfied");
                }

                return true;
            }

            if (logDetails) {
                Logger.Debug("Satisfaction count doesn't fit: Desires satisfied: " + satisfactionCount +
                             " Requires all: " + aff.RequireAllAffordances + " Number of desires on aff: " +
                             aff.Satisfactionvalues.Count);
            }

            return false;
        }

        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        public void NextStepNew([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcLocation> locs, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                             [JetBrains.Annotations.NotNull] HouseholdKey householdKey,
                             [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcPerson> persons,
                             int simulationSeed,DateTime now)
        {
            if (_calcRepo.Logfile == null) {
                throw new LPGException("Logfile was null.");
            }

            if (time.InternalStep == 0) {
                Init(locs, _sicknessPotentialAffs, true);
                Init(locs, _normalPotentialAffs, false);
            }

            if (_previousAffordances.Count > _calcRepo.CalcParameters.AffordanceRepetitionCount) {
                _previousAffordances.RemoveAt(0);
            }

            if (_calcRepo.CalcParameters.IsSet(CalcOption.CriticalViolations)) {
                //if (_lf == null) {                    throw new LPGException("Logfile was null.");                }

                PersonDesires.CheckForCriticalThreshold(this, time, _calcRepo.FileFactoryAndTracker, householdKey);
            }

            //PersonDesires.ApplyDecay(time);
            if (_executingAffordance != null)
            {
                PersonDesires.ApplyDecayWithoutSomeNew(time, _executingAffordance.Satisfactionvalues);

            }
            else
            {
                PersonDesires.ApplyDecay(time);
            }


            //PersonDesires.ApplyDecay(time);



            WriteDesiresToLogfileIfNeeded(time, householdKey);

            ReturnToPreviousActivityIfPreviouslyInterrupted(time);

            // bereits besch鋐tigt
            if (_isBusy[time.InternalStep]) {
                if (_executingAffordance != null && _remainingExecutionSteps > 0)
                {
                    //Debug.WriteLine("Time " + time + " Current: " + _executingAffordance + " remain "+ _remainingExecutionSteps);
                    _remainingExecutionSteps--;
                    //here use ApplyAffordanceEffectPartly to get the correct affordance effect
                    PersonDesires.ApplyAffordanceEffectNew(_executingAffordance.Satisfactionvalues, _executingAffordance.RandomEffect, _executingAffordance.Name, _currentDuration, false, time, now);
                }
                InterruptIfNeededNew(time, isDaylight, false,now);
                //continue with current activity

                //totalWeightedDeviation = PersonDesires.getcurrent_TotalWeightedDeviation();

                return;
            }

            if (IsOnVacation[time.InternalStep])
            {
                BeOnVacation(time);

                //totalWeightedDeviation = PersonDesires.getcurrent_TotalWeightedDeviation();

                return;
            }

            _alreadyloggedvacation = false;
            if (!_isCurrentlySick && IsSick[time.InternalStep]) {
                // neue krank geworden
                BecomeSick(time);
            }

            if (_isCurrentlySick && !IsSick[time.InternalStep]) {
                // neue gesund geworden
                BecomeHealthy(time);
            }

            //activate new affordance
            var bestaff = FindBestAffordanceNew(time,  persons,
                simulationSeed, now);
            //MessageWindowHandler.Mw.ShowInfoMessage(bestaff.ToString(), "Success");
            //Logger.Info(bestaff.ToString());
            //System.Console.WriteLine(bestaff.ToString());
            //Debug.WriteLine(time);
            if (_debug_print)
            {
                Debug.WriteLine("Time:   " + now + "  " + _calcPerson.Name + "    " + bestaff.Name + "  restTime:  " + bestaff.GetRestTimeWindows(time));

            }

            ActivateAffordanceNew(time, isDaylight,  bestaff, now);
            _isCurrentlyPriorityAffordanceRunning = false;

            //totalWeightedDeviation = PersonDesires.getcurrent_TotalWeightedDeviation();

            //Debug.WriteLine("Time:   " + now +  "  totalWeightedDeviation:  " + PersonDesires.getcurrent_TotalWeightedDeviation());

            //PersonDesires.ApplyDecayNew();
        }

        public decimal GetCurrent_TotalWeightedDeviation()
        {
            return PersonDesires.Getcurrent_TotalWeightedDeviation();
        }

        public void NextStep([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcLocation> locs, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                             [JetBrains.Annotations.NotNull] HouseholdKey householdKey,
                             [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcPerson> persons,
                             int simulationSeed)
        {
            if (_calcRepo.Logfile == null)
            {
                throw new LPGException("Logfile was null.");
            }

            if (time.InternalStep == 0)
            {
                Init(locs, _sicknessPotentialAffs, true);
                Init(locs, _normalPotentialAffs, false);
            }

            if (_previousAffordances.Count > _calcRepo.CalcParameters.AffordanceRepetitionCount)
            {
                _previousAffordances.RemoveAt(0);
            }

            if (_calcRepo.CalcParameters.IsSet(CalcOption.CriticalViolations))
            {
                //if (_lf == null) {                    throw new LPGException("Logfile was null.");                }

                PersonDesires.CheckForCriticalThreshold(this, time, _calcRepo.FileFactoryAndTracker, householdKey);
            }

            PersonDesires.ApplyDecay(time);
            WriteDesiresToLogfileIfNeeded(time, householdKey);

            ReturnToPreviousActivityIfPreviouslyInterrupted(time);

            // bereits besch鋐tigt
            if (_isBusy[time.InternalStep])
            {
                InterruptIfNeeded(time, isDaylight, false);
                return;
            }

            if (IsOnVacation[time.InternalStep])
            {
                BeOnVacation(time);

                return;
            }

            _alreadyloggedvacation = false;
            if (!_isCurrentlySick && IsSick[time.InternalStep])
            {
                // neue krank geworden
                BecomeSick(time);
            }

            if (_isCurrentlySick && !IsSick[time.InternalStep])
            {
                // neue gesund geworden
                BecomeHealthy(time);
            }

            //activate new affordance
            var bestaff = FindBestAffordance(time, persons,
                simulationSeed);
            //MessageWindowHandler.Mw.ShowInfoMessage(bestaff.ToString(), "Success");
            Logger.Info(bestaff.ToString());
            Console.WriteLine(bestaff.ToString());
            ActivateAffordance(time, isDaylight, bestaff);
            _isCurrentlyPriorityAffordanceRunning = false;
        }

        private ICalcAffordanceBase FindBestAffordance([JetBrains.Annotations.NotNull] TimeStep time,
                                                       [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcPerson> persons, int simulationSeed)
        {
            var allAffs = IsSick[time.InternalStep] ? _sicknessPotentialAffs : _normalPotentialAffs;

            if (_calcRepo.Rnd == null)
            {
                throw new LPGException("Random number generator was not initialized");
            }

            var allAffordances =
                NewGetAllViableAffordancesAndSubs(time, null, false, allAffs, false);
            if (allAffordances.Count == 0 && (time.ExternalStep < 0 || _calcRepo.CalcParameters.IgnorePreviousActivitesWhenNeeded))
            {
                allAffordances =
                    NewGetAllViableAffordancesAndSubs(time, null, false, allAffs, true);
            }
            allAffordances.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            //no affordances, so search again for the error messages
            if (allAffordances.Count == 0)
            {

                var status = new AffordanceStatusClass();
                NewGetAllViableAffordancesAndSubs(time, status, false, allAffs, false);
                var ts = new TimeSpan(0, 0, 0,
                    (int)_calcRepo.CalcParameters.InternalStepsize.TotalSeconds * time.InternalStep);
                var dt = _calcRepo.CalcParameters.InternalStartTime.Add(ts);
                var s = "At Timestep " + time.ExternalStep + " (" + dt.ToLongDateString() + " " + dt.ToShortTimeString() + ")" +
                        " not a single affordance was available for " + Name +
                        " in the household " + _calcPerson.HouseholdName + "." + Environment.NewLine +
                        "Since the people in this simulation can't do nothing, calculation can not continue." +
                        " The simulation seed was " + simulationSeed + ". " + Environment.NewLine + Name + " was ";
                if (IsSick[time.InternalStep])
                {
                    s += " sick at the time." + Environment.NewLine;
                }
                else
                {
                    s += " not sick at the time." + Environment.NewLine;
                }

                s += _calcPerson.Name + " was at " + CurrentLocation.Name + "." + Environment.NewLine;
                s += "The setting for the number of required unique affordances in a row was set to " + _calcRepo.CalcParameters.AffordanceRepetitionCount + "." + Environment.NewLine;
                if (status.Reasons.Count > 0)
                {
                    s += " The status of each affordance is as follows:" + Environment.NewLine;
                    foreach (var reason in status.Reasons)
                    {
                        s = s + Environment.NewLine + reason.Affordance.Name + ":" + reason.Reason;
                    }
                }
                else
                {
                    s += " Not a single viable affordance was found.";
                }

                s += Environment.NewLine + Environment.NewLine + "The last activity of each Person was:";
                foreach (var calcPerson in persons)
                {
                    var name = "(none)";
                    if (calcPerson._currentAffordance != null)
                    {
                        name = calcPerson._currentAffordance.Name;
                    }

                    s += Environment.NewLine + calcPerson.Name + ": " + name;
                }
                if (_calcRepo.CalcParameters.EnableIdlemode)
                {
                    var idleaff = CurrentLocation.IdleAffs[this];
                    idleaff.IsBusy(time, CurrentLocation, _calcPerson);
                    //Logger.Info(s);
                    return idleaff;
                }
                throw new DataIntegrityException(s);
            }

            if (_calcRepo.Rnd == null)
            {
                throw new LPGException("Random number generator was not initialized");
            }

            return GetBestAffordanceFromList(time, allAffordances);
        }

        private ICalcAffordanceBase GetBestAffordanceFromList([JetBrains.Annotations.NotNull] TimeStep time,
                                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances)
        {
            var bestdiff = decimal.MaxValue;
            var bestaff = allAvailableAffordances[0];
            var bestaffordances = new List<ICalcAffordanceBase>();
            foreach (var affordance in allAvailableAffordances)
            {
                var desireDiff =
                    PersonDesires.CalcEffect(affordance.Satisfactionvalues, out var thoughtstring, affordance.Name);
                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (//_lf == null ||
                        _calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }

                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }

                if (desireDiff < bestdiff)
                {
                    bestdiff = desireDiff;
                    bestaff = affordance;
                    bestaffordances.Clear();
                }

                if (desireDiff == bestdiff)
                {
                    bestaffordances.Add(affordance);
                }
            }

            if (bestaffordances.Count > 1)
            {
                //if (_lf == null) {throw new LPGException("Logfile was null.");}

                bestaff = PickRandomAffordanceFromEquallyAttractiveOnes(bestaffordances, time,
                    this, _calcPerson.HouseholdKey);
            }

            return bestaff;
        }

        private void ActivateAffordance([JetBrains.Annotations.NotNull] TimeStep currentTimeStep, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                                         [JetBrains.Annotations.NotNull] ICalcAffordanceBase bestaff)
        {
            if (_calcRepo.Logfile == null)
            {
                throw new LPGException("Logfile was null.");
            }

            if (_calcRepo.CalcParameters.TransportationEnabled)
            {
                if (!(bestaff is AffordanceBaseTransportDecorator))
                {
                    throw new LPGException(
                        "Trying to activate a non-transport affordance in a household that has transportation enabled. This is a bug and should never happen. The affordance was: " +
                        bestaff.Name + ". Affordance Type: " + bestaff.GetType().FullName);
                }
            }

            _calcRepo.OnlineLoggingData.AddLocationEntry(
                new LocationEntry(_calcPerson.HouseholdKey,
                    _calcPerson.Name,
                    _calcPerson.Guid,
                     currentTimeStep,
                    bestaff.ParentLocation.Name,
                    bestaff.ParentLocation.Guid));
            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
            {
                if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                {
                    throw new LPGException("Logfile was null.");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, currentTimeStep, "Action selected:" + bestaff),
                    _calcPerson.HouseholdKey);
            }

            _calcRepo.OnlineLoggingData.AddActionEntry(currentTimeStep, Guid,
                Name, _isCurrentlySick, bestaff.Name,
                bestaff.Guid, _calcPerson.HouseholdKey,
                bestaff.AffCategory, bestaff.BodilyActivityLevel);
            PersonDesires.ApplyAffordanceEffect(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name);
            bestaff.Activate(currentTimeStep, Name, CurrentLocation,
                out var personTimeProfile);
            CurrentLocation = bestaff.ParentLocation;
            //todo: fix this for transportation
            var duration = SetBusy(currentTimeStep, personTimeProfile, bestaff.ParentLocation, isDaylight,
                bestaff.NeedsLight);
            _previousAffordances.Add(bestaff);
            _previousAffordancesWithEndTime.Add(
                new Tuple<ICalcAffordanceBase, TimeStep>(bestaff, currentTimeStep.AddSteps(duration)));
            while (_previousAffordancesWithEndTime.Count > 5)
            {
                _previousAffordancesWithEndTime.RemoveAt(0);
            }

            if (bestaff is CalcSubAffordance subaff)
            {
                _previousAffordances.Add(subaff.ParentAffordance);
            }

            _currentAffordance = bestaff;
            //else {
            //    if (_calcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
            //        if(_lf == null) {
            //            throw new LPGException("Logfile was null");
            //        }
            //        _lf.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "No Action selected"),
            //            _householdKey);
            //    }
            //}
        }

        private void InterruptIfNeeded([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                                       bool ignoreAlreadyExecutedActivities)
        {
            if (_currentAffordance?.IsInterruptable == true &&
                !_isCurrentlyPriorityAffordanceRunning)
            {
                PotentialAffs aff;
                if (IsSick[time.InternalStep])
                {
                    aff = _sicknessPotentialAffs;
                }
                else
                {
                    aff = _normalPotentialAffs;
                }

                var availableInterruptingAffordances =
                    NewGetAllViableAffordancesAndSubs(time, null, true, aff, ignoreAlreadyExecutedActivities);
                if (availableInterruptingAffordances.Count != 0)
                {
                    var bestAffordance = GetBestAffordanceFromList(time, availableInterruptingAffordances);
                    ActivateAffordance(time, isDaylight, bestAffordance);
                    switch (bestAffordance.AfterInterruption)
                    {
                        case ActionAfterInterruption.LookForNew:
                            var currentTime = time;
                            while (currentTime.InternalStep < _calcRepo.CalcParameters.InternalTimesteps &&
                                   _isBusy[currentTime.InternalStep])
                            {
                                _isBusy[currentTime.InternalStep] = false;
                                currentTime = currentTime.AddSteps(1);
                            }

                            break;
                        case ActionAfterInterruption.GoBackToOld:
                            if (_previousAffordancesWithEndTime.Count > 2) //set the old affordance again
                            {
                                var endtime =
                                    _previousAffordancesWithEndTime[_previousAffordancesWithEndTime.Count - 1]
                                        .Item2;
                                var endtimePrev =
                                    _previousAffordancesWithEndTime[_previousAffordancesWithEndTime.Count - 2]
                                        .Item2;
                                if (endtimePrev > endtime)
                                {
                                    TimeToResetActionEntryAfterInterruption = endtime;
                                }
                            }

                            break;
                        default: throw new LPGException("Forgotten ActionAfterInterruption");
                    }

                    if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                    {
                        if (//_lf == null ||
                            _calcRepo.Logfile.ThoughtsLogFile1 == null)
                        {
                            throw new LPGException("Logfile was null.");
                        }

                        _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                            new ThoughtEntry(this, time,
                                "Interrupting the previous affordance for " + bestAffordance.Name),
                            _calcPerson.HouseholdKey);
                    }

                    _isCurrentlyPriorityAffordanceRunning = true;
                }
            }

            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
            {
                if (//_lf == null ||
                    _calcRepo.Logfile.ThoughtsLogFile1 == null)
                {
                    throw new LPGException("Logfile was null.");
                }

                if (!_isCurrentlySick)
                {
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I'm busy and healthy"),
                        _calcPerson.HouseholdKey);
                }
                else
                {
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I'm busy and sick"),
                        _calcPerson.HouseholdKey);
                }
            }
        }

        [JetBrains.Annotations.NotNull]
        [ItemNotNull]
        private List<ICalcAffordanceBase> NewGetAllViableAffordancesAndSubs([JetBrains.Annotations.NotNull] TimeStep timeStep,
                                                                            AffordanceStatusClass? errors,
                                                                            bool getOnlyInterrupting,
                                                                            [JetBrains.Annotations.NotNull] PotentialAffs potentialAffs, bool tryHarder)
        {
            var getOnlyRelevantDesires = getOnlyInterrupting; // just for clarity
            // normal affs
            var resultingAff = new List<ICalcAffordanceBase>();
            List<ICalcAffordanceBase> srcList;
            if (getOnlyInterrupting)
            {
                srcList = potentialAffs.PotentialInterruptingAffordances;
            }
            else
            {
                srcList = potentialAffs.PotentialAffordances;
            }

            foreach (var calcAffordanceBase in srcList)
            {
                if (NewIsAvailableAffordance(timeStep, calcAffordanceBase, errors, getOnlyRelevantDesires,
                    CurrentLocation.CalcSite, tryHarder))
                {
                    resultingAff.Add(calcAffordanceBase);
                }
            }

            // subaffs
            List<ICalcAffordanceBase> subSrcList;
            if (getOnlyInterrupting)
            {
                subSrcList = potentialAffs.PotentialAffordancesWithInterruptingSubAffordances;
            }
            else
            {
                subSrcList = potentialAffs.PotentialAffordancesWithSubAffordances;
            }

            foreach (var affordance in subSrcList)
            {
                var spezsubaffs =
                    affordance.CollectSubAffordances(timeStep, getOnlyInterrupting, CurrentLocation);
                if (spezsubaffs.Count > 0)
                {
                    foreach (var spezsubaff in spezsubaffs)
                    {
                        if (NewIsAvailableAffordance(timeStep, spezsubaff, errors,
                            getOnlyRelevantDesires, CurrentLocation.CalcSite, tryHarder))
                        {
                            resultingAff.Add(spezsubaff);
                        }
                    }
                }
            }

            if (getOnlyInterrupting)
            {
                foreach (var affordance in resultingAff)
                {
                    if (!PersonDesires.HasAtLeastOneDesireBelowThreshold(affordance))
                    {
                        throw new LPGException("something went wrong while getting an interrupting affordance!");
                    }
                }
            }

            return resultingAff;
        }

        private bool NewIsAvailableAffordance([JetBrains.Annotations.NotNull] TimeStep timeStep,
                                              [JetBrains.Annotations.NotNull] ICalcAffordanceBase aff,
                                              AffordanceStatusClass? errors, bool checkForRelevance,
                                              CalcSite? srcSite, bool ignoreAlreadyExecutedActivities)
        {
            if (_calcRepo.CalcParameters.TransportationEnabled)
            {
                if (aff.Site != srcSite && !(aff is AffordanceBaseTransportDecorator))
                {
                    //person is not at the right place and can't transport -> not available.
                    return false;
                }
            }

            if (!ignoreAlreadyExecutedActivities && _previousAffordances.Contains(aff))
            {
                if (errors != null)
                {
                    errors.Reasons.Add(new AffordanceStatusTuple(aff, "Just did this."));
                }

                return false;
            }

            var busynessResult = aff.IsBusy(timeStep, CurrentLocation, _calcPerson);
            if (busynessResult != BusynessType.NotBusy)
            {
                if (errors != null)
                {
                    errors.Reasons.Add(new AffordanceStatusTuple(aff, "Affordance is busy:" + busynessResult.ToString()));
                }

                return false;
            }

            if (checkForRelevance && !PersonDesires.HasAtLeastOneDesireBelowThreshold(aff))
            {
                if (errors != null)
                {
                    errors.Reasons.Add(new AffordanceStatusTuple(aff,
                        "Person has no desires below the threshold for this affordance, so it is not relevant right now."));
                }

                return false;
            }

            return true;
        }


        private void BecomeHealthy([JetBrains.Annotations.NotNull] TimeStep time)
        {
            PersonDesires = _normalDesires;
            PersonDesires.CopyOtherDesires(SicknessDesires);
            _isCurrentlySick = false;
            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (
                    //_lf == null ||
                    _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Logfile was null.");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I've just become healthy."),
                    _calcPerson.HouseholdKey);
            }
        }

        private void BecomeSick([JetBrains.Annotations.NotNull] TimeStep time)
        {
            PersonDesires = SicknessDesires;
            PersonDesires.CopyOtherDesires(_normalDesires);
            _isCurrentlySick = true;
            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (//_lf == null ||
                    _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Logfile was null.");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I've just become sick."),
                    _calcPerson.HouseholdKey);
            }
        }

        private void BeOnVacation([JetBrains.Annotations.NotNull] TimeStep time)
        {
            if (_calcRepo.Logfile == null) {
                throw new LPGException("Logfile was null.");
            }

            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (_calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Logfile was null.");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I'm on vacation."), _calcPerson.HouseholdKey);
            }

            // only log vacation if not done already and if the current time step does not belong to the setup time frame
            if (!_alreadyloggedvacation && time.DisplayThisStep) {
                _calcRepo.OnlineLoggingData.AddActionEntry(time, _calcPerson.Guid, _calcPerson.Name,
                    _isCurrentlySick, "taking a vacation", _vacationAffordanceGuid, _calcPerson.HouseholdKey,
                    "Vacation", BodilyActivityLevel.Outside);
                _calcRepo.OnlineLoggingData.AddLocationEntry(new LocationEntry(_calcPerson.HouseholdKey,
                    _calcPerson.Name, _calcPerson.Guid, time, "Vacation", _vacationLocationGuid));
                _alreadyloggedvacation = true;
            }
        }

        private void InterruptIfNeededNew([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                                       bool ignoreAlreadyExecutedActivities, DateTime now)
        {
            if (_currentAffordance?.IsInterruptable == true &&
                !_isCurrentlyPriorityAffordanceRunning) {
                PotentialAffs aff;
                if (IsSick[time.InternalStep]) {
                    aff = _sicknessPotentialAffs;
                }
                else {
                    aff = _normalPotentialAffs;
                }

                var availableInterruptingAffordances =
                    NewGetAllViableAffordancesAndSubsNew(time, null, true, aff, ignoreAlreadyExecutedActivities);
                if (availableInterruptingAffordances.Count != 0) {
                    var bestAffordance = GetBestAffordanceFromList_RL(time, availableInterruptingAffordances, true, now);
                    //Debug.WriteLine("Interrupting " + _currentAffordance + " with " + bestAffordance);
                    if(_debug_print)
                    {
                        Debug.WriteLine("Time:   " + now + "  " + _calcPerson.Name + "    " + bestAffordance.Name + "  !!! Interrupt  !!!   " + _currentAffordance.Name);
                    }
                    //Debug.WriteLine("Time:   " + now + "  " + _calcPerson.Name + "    " + bestAffordance.Name+"  !!! Interrupt  !!!   "+_currentAffordance.Name);
                    ActivateAffordanceNew(time, isDaylight,  bestAffordance, now);
                    
                    
                    switch (bestAffordance.AfterInterruption) {
                        case ActionAfterInterruption.LookForNew:
                            var currentTime = time;
                            while (currentTime.InternalStep < _calcRepo.CalcParameters.InternalTimesteps &&
                                   _isBusy[currentTime.InternalStep]) {
                                _isBusy[currentTime.InternalStep] = false;
                                currentTime = currentTime.AddSteps(1);
                            }

                            break;
                        case ActionAfterInterruption.GoBackToOld:
                            if (_previousAffordancesWithEndTime.Count > 2) //set the old affordance again
                            {
                                var endtime =
                                    _previousAffordancesWithEndTime[_previousAffordancesWithEndTime.Count - 1]
                                        .Item2;
                                var endtimePrev =
                                    _previousAffordancesWithEndTime[_previousAffordancesWithEndTime.Count - 2]
                                        .Item2;
                                if (endtimePrev > endtime) {
                                    TimeToResetActionEntryAfterInterruption = endtime;
                                }
                            }

                            break;
                        default: throw new LPGException("Forgotten ActionAfterInterruption");
                    }

                    if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                        if (//_lf == null ||
                            _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                            throw new LPGException("Logfile was null.");
                        }

                        _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                            new ThoughtEntry(this, time,
                                "Interrupting the previous affordance for " + bestAffordance.Name),
                            _calcPerson.HouseholdKey);
                    }

                    _isCurrentlyPriorityAffordanceRunning = true;
                }
            }

            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (//_lf == null ||
                    _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Logfile was null.");
                }

                if (!_isCurrentlySick) {
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I'm busy and healthy"),
                        _calcPerson.HouseholdKey);
                }
                else {
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "I'm busy and sick"),
                        _calcPerson.HouseholdKey);
                }
            }
        }

        private void ReturnToPreviousActivityIfPreviouslyInterrupted([JetBrains.Annotations.NotNull] TimeStep time)
        {
            if (time == TimeToResetActionEntryAfterInterruption) {
                //if (_lf == null) {throw new LPGException("Logfile was null.");}

                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null) {
                        throw new LPGException("Logfile was null.");
                    }

                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Back to " + _previousAffordancesWithEndTime[_previousAffordancesWithEndTime.Count - 2]),
                        _calcPerson.HouseholdKey);
                }

                //-2 to get the one before the interrupting one
                ICalcAffordanceBase prevAff =
                    _previousAffordancesWithEndTime[_previousAffordancesWithEndTime.Count - 2].Item1;
                _calcRepo.OnlineLoggingData.AddActionEntry(time, Guid, Name, _isCurrentlySick, prevAff.Name, prevAff.Guid,
                    _calcPerson.HouseholdKey, prevAff.AffCategory, prevAff.BodilyActivityLevel);
                TimeToResetActionEntryAfterInterruption = null;
            }
        }

        private void WriteDesiresToLogfileIfNeeded([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull] HouseholdKey householdKey)
        {
            if (_calcRepo.CalcParameters.IsSet(CalcOption.DesiresLogfile)) {
                if (//_lf == null ||
                    _calcRepo.Logfile.DesiresLogfile == null) {
                    throw new LPGException("Logfile was null.");
                }

                if (!_isCurrentlySick) {
                    _calcRepo.Logfile.DesiresLogfile.WriteEntry(
                        new DesireEntry(this, time, PersonDesires, _calcRepo.Logfile.DesiresLogfile, _calcRepo.CalcParameters), householdKey);
                }
                else {
                    _calcRepo.Logfile.DesiresLogfile.WriteEntry(
                        new DesireEntry(this, time, SicknessDesires, _calcRepo.Logfile.DesiresLogfile, _calcRepo.CalcParameters),householdKey);
                }
            }
        }

        [JetBrains.Annotations.NotNull]
        public ICalcAffordanceBase PickRandomAffordanceFromEquallyAttractiveOnes(
            [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> bestaffordances,
            [JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull] CalcPerson person, [JetBrains.Annotations.NotNull] HouseholdKey householdKey)
        {
            // I dont remember why i did this?
            // collect the subaffs for maybe eliminating them
            //var subaffs = new List<CalcAffordanceBase>();
            //foreach (var calcAffordanceBase in bestaffordances)
            //{
            //    if (calcAffordanceBase is CalcSubAffordance)
            //    {
            //        subaffs.Add(calcAffordanceBase);
            //    }
            //}
            //// definitely eliminate
            //if (subaffs.Count < bestaffordances.Count)
            //{
            //    foreach (var calcAffordanceBase in subaffs)
            //    {
            //        bestaffordances.Remove(calcAffordanceBase);
            //    }
            //}
            var bestaffnames = string.Empty;
            foreach (var calcAffordance in bestaffordances) {
                bestaffnames = bestaffnames + calcAffordance.Name + "(" + calcAffordance.Weight + "), ";
            }

            var weightsum = bestaffordances.Sum(x => x.Weight);
            var pick = _calcRepo.Rnd.Next(weightsum);
            ICalcAffordanceBase? selectedAff = null;
            var idx = 0;
            var cumulativesum = 0;

            while (idx < bestaffordances.Count) {
                var start = cumulativesum;
                var currentAff = bestaffordances[idx];
                var end = cumulativesum + currentAff.Weight;
                if (pick >= start && pick <= end) {
                    selectedAff = currentAff;
                    idx = bestaffordances.Count;
                }

                cumulativesum += currentAff.Weight;
                idx++;
            }

            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (_calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Thoughtslogfile was null");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                    new ThoughtEntry(person, time,
                        "Found " + bestaffordances.Count + " affordances with identical attractiveness:" +
                        bestaffnames), householdKey);
            }

            if (selectedAff == null) {
                throw new LPGException("Could not select an affordance. Please fix.");
            }

            return selectedAff;
            //= bestaffordances[r.Next(bestaffordances.Count)];
        }

        public override string ToString() => "Person:" + Name;

        private void ActivateAffordanceNew([JetBrains.Annotations.NotNull] TimeStep currentTimeStep, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                                         [JetBrains.Annotations.NotNull] ICalcAffordanceBase bestaff, DateTime now)
        {
            if (_calcRepo.Logfile == null) {
                throw new LPGException("Logfile was null.");
            }

            if (_calcRepo.CalcParameters.TransportationEnabled) {
                if (!(bestaff is AffordanceBaseTransportDecorator)) {
                    throw new LPGException(
                        "Trying to activate a non-transport affordance in a household that has transportation enabled. This is a bug and should never happen. The affordance was: " +
                        bestaff.Name + ". Affordance Type: " + bestaff.GetType().FullName);
                }
            }

            _calcRepo.OnlineLoggingData.AddLocationEntry(
                new LocationEntry(_calcPerson.HouseholdKey,
                    _calcPerson.Name,
                    _calcPerson.Guid,
                     currentTimeStep,
                    bestaff.ParentLocation.Name,
                    bestaff.ParentLocation.Guid));
            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (_calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Logfile was null.");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, currentTimeStep, "Action selected:" + bestaff),
                    _calcPerson.HouseholdKey);
            }

            _calcRepo.OnlineLoggingData.AddActionEntry(currentTimeStep, Guid,
                Name, _isCurrentlySick, bestaff.Name,
                bestaff.Guid, _calcPerson.HouseholdKey,
                bestaff.AffCategory, bestaff.BodilyActivityLevel);


            //apply the effect

            //int RealdurationInMinutes = bestaff.GetRealDuration(currentTimeStep);

            bestaff.Activate(currentTimeStep, Name,  CurrentLocation,
                out var personTimeProfile);
            
            //PersonDesires.ApplyAffordanceEffect(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name);
            int durationInMinutes = personTimeProfile.StepValues.Count;

            //Debug.WriteLine("Time:" +now+ "Activating R " + bestaff.Name + " Time: " + durationInMinutes);
            //int testDuration = 
            
            //Debug.WriteLine("Activating D " + bestaff.Name + " Time: " + RealdurationInMinutes);

            if (bestaff?.IsInterruptable == false)
            {
                //for (var i = 0; i <= durationInMinutes; i++)
                //{
                    //PersonDesires.ApplyAffordanceEffectPartly(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name, durationInMinutes);
                //}
            }
            PersonDesires.ApplyAffordanceEffectNew(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name, durationInMinutes, true, currentTimeStep, now);
            _executingAffordance = bestaff;
            _remainingExecutionSteps = durationInMinutes - 1;
            _currentDuration = durationInMinutes;
            
            CurrentLocation = bestaff.ParentLocation;
            //todo: fix this for transportation
            var duration = SetBusy(currentTimeStep, personTimeProfile, bestaff.ParentLocation, isDaylight,
                bestaff.NeedsLight);

            
            //Debug.WriteLine("Duration: " + personTimeProfile.StepValues.Count + " && " + duration);
            //duration is in minutes ：personTimeProfile.StepValues.Count

            _previousAffordances.Add(bestaff);
            _previousAffordancesWithEndTime.Add(
                new Tuple<ICalcAffordanceBase, TimeStep>(bestaff, currentTimeStep.AddSteps(duration)));
            while (_previousAffordancesWithEndTime.Count > 5) {
                _previousAffordancesWithEndTime.RemoveAt(0);
            }

            if (bestaff is CalcSubAffordance subaff) {
                _previousAffordances.Add(subaff.ParentAffordance);
            }

            _currentAffordance = bestaff;

            //if (!AffordanceSequence.ContainsKey(now.Date))
            //{
            //    AffordanceSequence[now.Date] = new Dictionary<DateTime, ICalcAffordanceBase>();
            //}

            //AffordanceSequence[now.Date][now] = bestaff;

            //Dictionary<DateTime, ICalcAffordanceBase> innerDict;
            if (!AffordanceSequence.TryGetValue(now.Date, out var innerDict))
            {
                innerDict = new Dictionary<DateTime, ICalcAffordanceBase>();
                AffordanceSequence[now.Date] = innerDict;
            }

            innerDict[now] = bestaff;


            //else {
            //    if (_calcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
            //        if(_lf == null) {
            //            throw new LPGException("Logfile was null");
            //        }
            //        _lf.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "No Action selected"),
            //            _householdKey);
            //    }
            //}
        }

        public void LogPersonStatus([JetBrains.Annotations.NotNull] TimeStep timestep)
        {
            var ps = new PersonStatus(_calcPerson.HouseholdKey,_calcPerson.Name,
                _calcPerson.Guid,CurrentLocation.Name,CurrentLocation.Guid,CurrentLocation.CalcSite?.Name,
                CurrentLocation.CalcSite?.Guid,_currentAffordance?.Name,_currentAffordance?.Guid, timestep);
            _calcRepo.OnlineLoggingData.AddPersonStatus(ps);
            //Console.WriteLine();
        }
        
        [JetBrains.Annotations.NotNull]
        private ICalcAffordanceBase FindBestAffordanceNew([JetBrains.Annotations.NotNull] TimeStep time,
                                                       [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcPerson> persons, int simulationSeed, DateTime now)
        {
            var allAffs = IsSick[time.InternalStep] ? _sicknessPotentialAffs : _normalPotentialAffs;

            if (_calcRepo.Rnd == null) {
                throw new LPGException("Random number generator was not initialized");
            }

            var allAffordances =
                NewGetAllViableAffordancesAndSubsNew(time, null, false,  allAffs, false);
            if(allAffordances.Count == 0 && (time.ExternalStep < 0 || _calcRepo.CalcParameters.IgnorePreviousActivitesWhenNeeded))
            {
                allAffordances =
                    NewGetAllViableAffordancesAndSubsNew(time, null,  false, allAffs, true);
            }
            allAffordances.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            //no affordances, so search again for the error messages
            if (allAffordances.Count == 0) {

                var status = new AffordanceStatusClass();
                NewGetAllViableAffordancesAndSubsNew(time, status,  false,  allAffs, false);
                var ts = new TimeSpan(0, 0, 0,
                    (int)_calcRepo.CalcParameters.InternalStepsize.TotalSeconds * time.InternalStep);
                var dt = _calcRepo.CalcParameters.InternalStartTime.Add(ts);
                var s = "At Timestep " + time.ExternalStep + " (" + dt.ToLongDateString() + " " + dt.ToShortTimeString() + ")" +
                        " not a single affordance was available for " + Name +
                        " in the household " + _calcPerson.HouseholdName + "." + Environment.NewLine +
                        "Since the people in this simulation can't do nothing, calculation can not continue." +
                        " The simulation seed was " + simulationSeed + ". " + Environment.NewLine + Name + " was ";
                if (IsSick[time.InternalStep]) {
                    s += " sick at the time."+ Environment.NewLine;
                }
                else {
                    s += " not sick at the time."+ Environment.NewLine;
                }

                s += _calcPerson.Name + " was at " + CurrentLocation.Name + "." + Environment.NewLine;
                s += "The setting for the number of required unique affordances in a row was set to " + _calcRepo.CalcParameters.AffordanceRepetitionCount + "." + Environment.NewLine;
                if (status.Reasons.Count > 0) {
                    s += " The status of each affordance is as follows:" + Environment.NewLine;
                    foreach (var reason in status.Reasons) {
                        s = s + Environment.NewLine + reason.Affordance.Name + ":" + reason.Reason;
                    }
                }
                else {
                    s += " Not a single viable affordance was found.";
                }

                s += Environment.NewLine + Environment.NewLine + "The last activity of each Person was:";
                foreach (var calcPerson in persons) {
                    var name = "(none)";
                    if (calcPerson._currentAffordance != null) {
                        name = calcPerson._currentAffordance.Name;
                    }

                    s += Environment.NewLine + calcPerson.Name + ": " + name;
                }
                if (_calcRepo.CalcParameters.EnableIdlemode)
                {
                    var idleaff = CurrentLocation.IdleAffs[this];
                    idleaff.IsBusyNew(time, CurrentLocation, _calcPerson);
                    //Logger.Info(s);
                    return idleaff;
                }
                throw new DataIntegrityException(s);
            }

            if (_calcRepo.Rnd == null) {
                throw new LPGException("Random number generator was not initialized");
            }

            return GetBestAffordanceFromList_RL(time,  allAffordances, true, now);
        }

        [JetBrains.Annotations.NotNull]

        private ICalcAffordanceBase GetBestAffordanceFromListNew([JetBrains.Annotations.NotNull] TimeStep time,
                                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            var bestDiff = decimal.MaxValue;
            var bestAffordance = allAvailableAffordances[0];
            //var bestaffordances = new List<(ICalcAffordanceBase, double)>();
            
            //bestaffordances.Add(bestAffordance);
            
            double bestWeightSum = -1;

            //var affordanceDetails = new Dictionary<string, Tuple<decimal, int, int, double>>();

            foreach (var affordance in allAvailableAffordances)
            {
                var duration = affordance.GetDuration();

                var calcTotalDeviationResult = PersonDesires.CalcEffectPartly(affordance, time, careForAll, out var thoughtstring,now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;

                if (desireDiff == 1000000000000000)
                {
                    continue;
                }



                ////V1 if sleep in the wait list, then direct run it
                if (weightSum >= 1000)
                {
                    bestAffordance = affordance;
                    break;
                }

                //V2 & V3
                //if (duration >= 120)
                //{
                //    DateTime newTime = now.AddMinutes(duration);
                //    //if (newTime.TimeOfDay > new TimeSpan(1, 0, 0) && newTime.Date > now.Date)
                //    if (newTime.Date > now.Date)
                //    {
                //        continue;
                //    }

                //}

                //////Load sample data
                //////string hourString = now.ToString("hh");
                //////int hourInt = int.Parse(hourString);
                //////float hourFloat = (float)hourInt;

                

                if(updatedWeight.TryGetValue(affordance.Name, out var weight))
                {
                    //desireDiff = weight * TunningDeviation((double)desireDiff, duration);
                    desireDiff = weight * desireDiff;
                }
                else
                {
                    //desireDiff = TunningDeviation((double)desireDiff, duration);
                    desireDiff = weight * desireDiff;

                }

                //desireDiff = TunningDeviation((double)desireDiff, duration);
                //desireDiff = desireDiff / (decimal)weightSum;

                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }

                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }

                if (_lookback)
                {
                    var restTimeWindows = affordance.GetRestTimeWindows(time);
                    //affordanceDetails[affordance.Name] = Tuple.Create(desireDiff, duration, restTimeWindows, weightSum);
                }

                if (desireDiff < bestDiff)
                {

                    //if (!firstTimeRecorded && (now.Hour >= 19 || now.Hour <= 3))
                    ////if (!firstTimeRecorded)
                    //{
                    //    //ML_Time_Aff_Bool_Model.ReloadModel();

                    //    //var roundedTime = now;
                    //    //var rounded_minutes = roundedTime.Minute - ((roundedTime.Minute % 15) * 15);
                    //    //roundedTime = roundedTime.AddMinutes(-rounded_minutes);

                    //    var sampleData = new ML_Time_Aff_Bool_Model.ModelInput()
                    //    {
                    //        Col0 = now.ToString("HH:mm"),
                    //        //Col0 = roundedTime.ToString("HH:mm"),
                    //        //Col0 = hourFloat,
                    //        Col1 = affordance.Name,
                    //    };

                    //    //Load model and predict output
                    //    var result = ML_Time_Aff_Bool_Model.Predict(sampleData, _calcPerson.Name);

                    //    if (result.PredictedLabel == "1")
                    //    {
                    //        Debug.WriteLine("ML: " + _calcPerson.Name + " Time:   " + now + "  Name:  " + affordance.Name);
                    //        setNewWeight(affordance.Name, 0.1m);
                    //        continue;
                    //    }
                    //}

                    bestDiff = desireDiff;
                    bestAffordance = affordance;
                    bestWeightSum = weightSum;
                    //bestaffordances.Clear();
                    //bestaffordances.Add((affordance,weightSum));
                }

                //if (desireDiff == bestDiff)
                //{
                //    bestaffordances.Add((affordance,weightSum));
                //}
            }

            //if (bestaffordances.Count > 1)
            //{
            //    //choose the one with the highest weightSum from the bestaffordances
            //    double maxWeightSum = -1;
            //    foreach (var affordance in bestaffordances)
            //    {
            //        if (affordance.Item2 > maxWeightSum)
            //        {
            //            maxWeightSum = affordance.Item2;
            //            bestAffordance = affordance.Item1;
            //        }
            //    }

                
            //}

            if (_lookback)
            {
                // Look back
                //double mostWeighted = -1;
                double mostWeighted = -1000;
                double suitWeighted = -1000;
                decimal minDesireDiff = 10000;
                string? bestWeightName = null;
                string? suitableAffordanceName = null;
                string bestAffordanceName = bestAffordance.Name;

                //string? bestAffordanceName = bestAffordance.Name;
                string? bestShortAffordanceName = null;
                int bestRestTime = bestAffordance.GetRestTimeWindows(time);
                int bestDuration = bestAffordance.GetDuration();

                int mostWeightRestTime = -1000;
                int suitRestTime = -1000;

                //foreach (var kvp in affordanceDetails)
                //{
                //    var weightSum = kvp.Value.Item4;
                //    var restTimeWindows = kvp.Value.Item3;
                //    var duration = kvp.Value.Item2;
                //    var desireDiff = kvp.Value.Item1;

                //    if (weightSum >= 100)
                //    {
                //        if (_debug_print)
                //        {
                //            Debug.WriteLine("   name: " + kvp.Key + "      restTime: " + restTimeWindows + "    weight: " + weightSum);
                //        }
                    
                //    }

                //    // Check and update the affordance with maximum weightSum
                //    if (weightSum > mostWeighted)
                //    {
                //        mostWeighted = weightSum;
                //        mostWeightRestTime = restTimeWindows;
                //        bestWeightName = kvp.Key;
                //    }

                //    // Check and update the affordance with smallest desireDiff and duration <= 60
                //    if (desireDiff < minDesireDiff && duration <= 60)
                //    {
                //        minDesireDiff = desireDiff;
                //        bestShortAffordanceName = kvp.Key;
                //    }

                //    // Check for suitableAffordance
                //    if (weightSum >= 100 && restTimeWindows < 3 * bestDuration && restTimeWindows > 0 && kvp.Key != bestAffordanceName)
                //    {
                //        if (weightSum > suitWeighted)
                //        {
                //            suitWeighted = weightSum;
                //            suitRestTime = restTimeWindows;
                //            suitableAffordanceName = kvp.Key;
                //        }
                //    }
                //}

                // Now, fetch the required affordances only once
                ICalcAffordanceBase? bestWeightAffordance = allAvailableAffordances.FirstOrDefault(aff => aff.Name == bestWeightName);
                ICalcAffordanceBase? bestShortAffordance = allAvailableAffordances.FirstOrDefault(aff => aff.Name == bestShortAffordanceName);
                ICalcAffordanceBase? suitableAffordance = allAvailableAffordances.FirstOrDefault(aff => aff.Name == suitableAffordanceName);

                // If the importance of the best affordance exceeds 100 and its restTimeWindows is negative
                //int bestWeightRestTime = bestWeightAffordance.GetRestTimeWindows(time);
                //int bestWeightDuration = bestWeightAffordance.GetDuration();
                DateTime newTime = now.AddMinutes(bestDuration);
                if (suitableAffordance != null)
                {
                    if (_debug_print)
                    {
                        Debug.WriteLine("case 3" + "   Rather:  " + bestAffordance.Name + "  be:  " + suitableAffordance.Name);

                    }
                    return suitableAffordance;
                }
                else if (mostWeighted >= 100 && mostWeightRestTime < 0)
                {
                    if (bestDuration <= 60)
                    {
                        if (_debug_print)
                        {
                            Debug.WriteLine("case 1" + "   Still:  " + bestAffordance.Name);
                        }
                    
                        return bestAffordance;
                    }
                    else if (bestShortAffordance != null)
                    {
                        if (_debug_print)
                        {
                            Debug.WriteLine("case 2" + "   Rather:  " + bestAffordance.Name + "  be:  " + bestShortAffordance.Name);
                        }
                    
                        return bestShortAffordance;
                    }
                }
                else if (bestDuration > 120 && newTime.TimeOfDay > new TimeSpan(1, 0, 0) && newTime.Date > now.Date && bestShortAffordance != null)
                {
                    return bestShortAffordance;//avoid no slepp at night
                }
                // If found an affordance that meets the criteria, return it.

            }
            
            return bestAffordance;


        }
        public void SaveQTableToFile()
        {
            SaveQTableToFile(false);
            SaveQTableToFile(true);
        }

        public void SaveQTableToFile(Boolean ismax)
        {
            string baseDir2 = @"C:\Work\ML\Models";
            //var qtable_toSave = qTable;

            var convertedQTable = new Dictionary<string, string>();

            var qTable_tosave = ismax ? max_qTable : qTable;

            foreach (var outerEntry in qTable_tosave)
            {
                var outerKeyDictSerialized = string.Join("±", outerEntry.Key.Item1.Select(d => $"{d.Key}⦿{d.Value.ToString()}"));
                var outerKey = $"{outerKeyDictSerialized}§{outerEntry.Key.Item2}";
                var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                    $"{innerEntry.Key}¶{innerEntry.Value.Item1}‖{innerEntry.Value.Item2}‖{String.Join("∥", innerEntry.Value.Item3.Select(d => $"{d.Key}⨁{d.Value}"))}"
                );

                convertedQTable[outerKey] = String.Join("★", innerDictSerialized);
            }

            if (!Directory.Exists(baseDir2))
            {
                // 创建目录
                Directory.CreateDirectory(baseDir2);

            }
            string personName = _calcPerson.Name.Replace("/", "_");
            //string filePath = Path.Combine(baseDir2, $"qTable-{personName}.json");
            string filePath = ismax ? Path.Combine(baseDir2, $"qTable-{personName}-max.json") : Path.Combine(baseDir2, $"qTable-{personName}.json");
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(convertedQTable, options);
                File.WriteAllText(filePath, jsonString);
                Debug.WriteLine("QTable has been successfully saved to " + filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving QTable: " + ex.Message);
            }
        }

        public void SaveTwoQTableToFile()
        {
            for (int i = 0; i < 1; i++)
            {
                bool ismax = i == 1;

                for (int j=0; j<2; j++)
                {
                    
                    bool isTableA = j == 1;

                    string baseDir2 = @"C:\Work\ML\Models";
                    //var qtable_toSave = qTable;

                    var convertedQTable = new Dictionary<string, string>();

                    //var qTable_tosave = ismax ? max_qTable : qTable;
                    var qTable_tosave = qTable;

                    if(isTableA)
                    {
                        qTable_tosave = qTable;
                    }
                    else
                    {
                        qTable_tosave = qTable2;
                    }

                    foreach (var outerEntry in qTable_tosave)
                    {
                        var outerKeyDictSerialized = string.Join("±", outerEntry.Key.Item1.Select(d => $"{d.Key}⦿{d.Value.ToString()}"));
                        var outerKey = $"{outerKeyDictSerialized}§{outerEntry.Key.Item2}";
                        var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                            $"{innerEntry.Key}¶{innerEntry.Value.Item1}‖{innerEntry.Value.Item2}‖{String.Join("∥", innerEntry.Value.Item3.Select(d => $"{d.Key}⨁{d.Value}"))}"
                        );

                        convertedQTable[outerKey] = String.Join("★", innerDictSerialized);
                    }

                    if (!Directory.Exists(baseDir2))
                    {
                        // 创建目录
                        Directory.CreateDirectory(baseDir2);

                    }
                    string personName = _calcPerson.Name.Replace("/", "_");
                    //string filePath = Path.Combine(baseDir2, $"qTable-{personName}.json");
                    string qTableType = j == 0 ? "qTable-1" : "qTable-2";
                    string filePath = ismax ? Path.Combine(baseDir2, $"{qTableType}-{personName}-max.json") : Path.Combine(baseDir2, $"{qTableType}-{personName}.json");
                    try
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        string jsonString = JsonSerializer.Serialize(convertedQTable, options);
                        File.WriteAllText(filePath, jsonString);
                        Debug.WriteLine("QTable has been successfully saved to " + filePath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Error saving QTable: " + ex.Message);
                    }
                }
                
                
            }
            
        }

        public void LoadQTableFromFile()
        {
            LoadQTableFromFile(false);
            LoadQTableFromFile(true);
            if(this.qTable.Count == 0 || this.max_qTable.Count == 0)
            {
                Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
                Logger.Info("No saved QTable found. Initializing a new QTable.");
                this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                this.max_qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
            }
        }

        public void LoadQTableFromFile(Boolean isMax)
        {
            if(isMax)
            {
                Debug.WriteLine("Now Loading Max QTable from file...");
            }
            else
            {
                Debug.WriteLine("Now Loading QTable from file...");
            }
            string baseDir = @"C:\Work\ML\Models";
            // 确保目录存在
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            // 处理文件名以避免路径问题
            string personName = _calcPerson.Name.Replace("/", "_");
            string filePath = Path.Combine(baseDir, $"qTable-{personName}.json");
            if (isMax)
            {
                filePath = Path.Combine(baseDir, $"qTable-{personName}-max.json");
            }

            // 检查文件是否存在
            if (File.Exists(filePath))
            {
                try
                {
                    var jsonString = File.ReadAllText(filePath);
                    var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);

                    var readed_QTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());

                    foreach (var outerEntry in convertedQTable)
                    {
                        var outerKeyParts = outerEntry.Key.Split('§');
                        var outerKeyDictParts = outerKeyParts[0].Split('±').Select(p => p.Split('⦿')).ToDictionary(p => p[0], p => int.Parse(p[1]));
                        var outerKey = (outerKeyDictParts, outerKeyParts[1]);
                        var innerDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();

                        var innerEntries = outerEntry.Value.Split(new string[] { "★" }, StringSplitOptions.None);
                        foreach (var innerEntry in innerEntries)
                        {
                            var parts = innerEntry.Split('¶');
                            var key = parts[0];
                            var valueParts = parts[1].Split('‖');
                            var decimalValue = double.Parse(valueParts[0]);
                            var intValue = int.Parse(valueParts[1]);
                            var dictParts = valueParts[2].Split(new string[] { "∥" }, StringSplitOptions.None);
                            var dict = dictParts.Select(p => p.Split('⨁')).ToDictionary(p => int.Parse(p[0]), p => double.Parse(p[1]));

                            innerDict[key] = (decimalValue, intValue, dict);
                        }

                        readed_QTable[outerKey] = innerDict;
                    }
                    if(isMax)
                    {
                        this.max_qTable = readed_QTable;
                    }
                    else
                    {
                        this.qTable = readed_QTable;
                    }
                    //this.qTable = readed_QTable;

                    //var firstKeyItem1 = this.qTable.Keys.First().Item1;

                    //// 遍历并打印所有键值对
                    //foreach (var kvp in firstKeyItem1)
                    //{
                    //    Debug.WriteLine($"Key: {kvp.Key}, Value: {kvp.Value}");
                    //}

                    Debug.WriteLine("QTable has been successfully loaded from " + filePath);
                    Logger.Info("QTable has been successfully loaded from " + filePath);


                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error loading QTable: " + ex.Message);
                    Logger.Info("Error loading QTable: " + ex.Message);
                    //Logger.Error("Error loading QTable: " + ex.Message);
                    // 出错时，初始化为新的字典
                    this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                    this.max_qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                }
            }
            else
            {
                // 文件不存在，初始化为新的字典
                Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
                Logger.Info("No saved QTable found. Initializing a new QTable.");
                this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                this.max_qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
            }
        }

        public void LoadTwoQTableFromFile()
        {
            for (int j=0; j< 1; j++)
            {
                bool isMax = j == 1;
                for (int i = 0; i < 2; i++)
                {
                    if (isMax)
                    {
                        Debug.WriteLine("Now Loading Max QTable from file...");
                    }
                    else
                    {
                        Debug.WriteLine("Now Loading QTable from file...");
                    }
                    string baseDir = @"C:\Work\ML\Models";
                    // 确保目录存在
                    if (!Directory.Exists(baseDir))
                    {
                        Directory.CreateDirectory(baseDir);
                    }

                    // 处理文件名以避免路径问题
                    string personName = _calcPerson.Name.Replace("/", "_");
                    string qTableType = i == 0 ? "qTable-1" : "qTable-2";
                    string filePath = Path.Combine(baseDir, $"{qTableType}-{personName}.json");
                    //if (isMax)
                    //{
                    //    filePath = Path.Combine(baseDir, $"{qTableType}-{personName}-max.json");
                    //}

                    // 检查文件是否存在
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            var jsonString = File.ReadAllText(filePath);
                            var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);

                            var readed_QTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());

                            foreach (var outerEntry in convertedQTable)
                            {
                                var outerKeyParts = outerEntry.Key.Split('§');
                                var outerKeyDictParts = outerKeyParts[0].Split('±').Select(p => p.Split('⦿')).ToDictionary(p => p[0], p => int.Parse(p[1]));
                                var outerKey = (outerKeyDictParts, outerKeyParts[1]);
                                var innerDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();

                                var innerEntries = outerEntry.Value.Split(new string[] { "★" }, StringSplitOptions.None);
                                foreach (var innerEntry in innerEntries)
                                {
                                    var parts = innerEntry.Split('¶');
                                    var key = parts[0];
                                    var valueParts = parts[1].Split('‖');
                                    var decimalValue = double.Parse(valueParts[0]);
                                    var intValue = int.Parse(valueParts[1]);
                                    var dictParts = valueParts[2].Split(new string[] { "∥" }, StringSplitOptions.None);
                                    var dict = dictParts.Select(p => p.Split('⨁')).ToDictionary(p => int.Parse(p[0]), p => double.Parse(p[1]));

                                    innerDict[key] = (decimalValue, intValue, dict);
                                }

                                readed_QTable[outerKey] = innerDict;
                            }
                            if (i == 0)
                            {
                                this.qTable = readed_QTable;
                            }
                            else
                            {
                                this.qTable2 = readed_QTable;
                            }
                            Debug.WriteLine("QTable has been successfully loaded from " + filePath);
                            Logger.Info("QTable has been successfully loaded from " + filePath);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Error loading QTable: " + ex.Message);
                            Logger.Info("Error loading QTable: " + ex.Message);
                            //Logger.Error("Error loading QTable: " + ex.Message);
                            // 出错时，初始化为新的字典
                            this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                            //this.max_qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                            //this.max_qTable2 = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                            this.qTable2 = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                            break;
                        }
                    }
                    else
                    {
                        // 文件不存在，初始化为新的字典
                        Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
                        Logger.Info("No saved QTable found. Initializing a new QTable.");
                        this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                        //this.max_qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                        //this.max_qTable2 = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                        this.qTable2 = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>>(new CustomKeyComparer());
                        break;
                    }
                }
            }
            
        }




        public List<double> ToDesireLevels(Dictionary<string, (double, double)> desireName_Value_Dict)
        {
            List<double> desire_value_Level = new List<double>();
            foreach (var kvp in desireName_Value_Dict)
            {
                var desire_Info = kvp.Value;
                double desire_weight = desire_Info.Item1;
                double desire_valueAfter = desire_Info.Item2;

                int slot = 2;

                if ((desire_weight >= 10))
                {
                    slot = 5;
                }
                if (desire_weight >= 100)
                {
                    slot = 10;
                }

                var desire_level = (int)(desire_valueAfter / (1 / slot));

                desire_value_Level.Add(desire_level);

            }

            return desire_value_Level;
        }

        public List<int> ToDesireLevels(Dictionary<string, (double, decimal)> desireName_Value_Dict)
        {
            List<int> desire_value_Level = new List<int>();
            foreach (var kvp in desireName_Value_Dict)
            {
                var desire_Info = kvp.Value;
                double desire_weight = desire_Info.Item1;
                double desire_valueAfter = (double)desire_Info.Item2;

                int slot = 2;

                if ((desire_weight >= 10))
                {
                    slot = 5;
                }
                if (desire_weight >= 100)
                {
                    slot = 10;
                }

                var desire_level = (int)(desire_valueAfter / (1 / slot));

                desire_value_Level.Add(desire_level);

            }

            return desire_value_Level;
        }

        public Dictionary<string, int> MergeDictAndLevels(Dictionary<string, (int, double)> desireName_Value_Dict)
        {
            var mergedDict = new Dictionary<string, int>(); // 创建新的字典以存储结果

            foreach (var kvp in desireName_Value_Dict)
            {
                var key = kvp.Key;
                var desire_Info = kvp.Value;
                int desire_weight = desire_Info.Item1;
                double desire_valueAfter = desire_Info.Item2;
                
                //Debug.WriteLine("Desire: " + key + "  Weight: " + desire_weight + "  Value: " + desire_valueAfter);

                int slot = 1;
                if (desire_weight >= 1) slot = 2;
                if (desire_weight >= 20) slot = 3;
                if (desire_weight >= 100) slot = 5;

                //var desire_level = (int)(desire_valueAfter / (1 / slot));
                var desire_level = (int)Math.Floor(desire_valueAfter * slot);

                if(desire_weight >= 20)//20
                {
                    mergedDict[key] = desire_level; // 将键和计算出的等级值直接合并到新字典中
                }
                
            }

            return mergedDict; // 返回合并后的字典
        }

        public string makeTimeSpan(DateTime time, int offset)
        {
            int unit = 15;
            var newTime = time.AddMinutes(offset);
            var rounded_minutes_new = newTime.Minute % unit;
            newTime = newTime.AddMinutes(-rounded_minutes_new);
            //TimeSpan newTimeState = new TimeSpan(newTime.Hour, newTime.Minute, 0);
            string prefix = newTime.DayOfWeek == DayOfWeek.Saturday || newTime.DayOfWeek == DayOfWeek.Sunday ? "R:" : "W:";
            string newTimeState = prefix + newTime.ToString("HH:mm");
            //Debug.WriteLine("Time: "+ "  " + newTimeState);
            return newTimeState;
            //return "";
        }

        private ICalcAffordanceBase GetBestAffordanceFromListNewRL_Double_Q_Learning([JetBrains.Annotations.NotNull] TimeStep time,
                                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            if (qTable.Count == 0)
            {
                LoadTwoQTableFromFile();
            }

            //double bestAverageR_S_A = 0;
            //double sleepAverageR_S_A = 0;
            //string readed_next_affordance_name = next_affordance_name;
            //string best_affordance_name = "";
            (double, int, Dictionary<int, double>) bestQSA_inCurrentState = (0, 0, new Dictionary<int, double>());

            //double epsilon1 = 0.1;
            //Random rnd1 = new Random(time.InternalStep);
            //Random rnd2 = new Random(time.InternalStep+1);
            //bool random1 = (rnd1.NextDouble() < epsilon1);
            //bool random2 = (rnd2.NextDouble() < epsilon1);

            var random = new Random(time.InternalStep);
            bool updateA = random.NextDouble() < 0.5;

            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;
            //ICalcAffordanceBase sarsa_affordacne = null;

            Dictionary<string, int> desire_level_before = null;

            double alpha = 0.2;// 0.2 is defult

            //double alpha = 0.5 * Math.Pow(0.997, 0.5 * (qTable.Count + qTable2.Count));
            //if (TableNumberEachDay.Count > 3)
            //{
            //    int TableNumberToday = this.qTable.Count + this.qTable2.Count;
            //    int TableNumberLastWeek = TableNumberToday;
                
            //    if (this.TableNumberEachDay.TryGetValue(now.Date.AddDays(-3), out var lastWeekTableNumber))
            //    {
            //        TableNumberLastWeek = lastWeekTableNumber;
            //        int DeltaTableNumber = TableNumberToday - TableNumberLastWeek;
            //        //alpha = 0.0148 * DeltaTableNumber + 0.01;
            //        alpha = ((DeltaTableNumber + 10 - 1) / 10) * 0.1;
            //        if (alpha > 0.5)
            //        {
            //            alpha = 0.5;
            //        }
            //    }
            //    else
            //    {
            //        alpha = 0.2;
            //    }
            //}

            //TimeSpan sleepDiff = now - lastSleepTime;
            //if(sleepDiff.TotalHours > 23)
            //{
            //    alpha = 0.5;
            //}

            foreach (var affordance in allAvailableAffordances)
            {
                if (affordance.Name.Contains("Replacement Activity"))
                {
                    continue;
                }

                var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(affordance, time, careForAll, out var thoughtstring, now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
                var desire_ValueBefore = calcTotalDeviationResult.desireName_ValueBeforeApply_Dict;
                var duration = calcTotalDeviationResult.realDuration;

                //string sarsa_next_affordance_candi = null;

                string nowTimeState = makeTimeSpan(now, 0);
                string newTimeState = makeTimeSpan(now, duration);
                Dictionary<string, int> desire_level_after = MergeDictAndLevels(desire_ValueAfter);

                


                double gamma = 0.95; //defult 0.8

                if (desire_level_before == null)
                {
                    desire_level_before = MergeDictAndLevels(desire_ValueBefore);
                    this.currentState = new(desire_level_before, nowTimeState);
                }

                (Dictionary<string, int>, string time) newState = (desire_level_after, newTimeState);

                var R_S_A = -desireDiff + 1000000; //desireDiff less is better, R_S_A more is better

                //var averageR_S_A = R_S_A / duration;

                var selectedQTable1 = updateA ? qTable : qTable2;


                if (!selectedQTable1.TryGetValue(currentState, out var Q_S))
                {
                    Q_S = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();
                    selectedQTable1[currentState] = Q_S;
                }

                (double, int, Dictionary<int, double>) Q_S_A;

                if (!Q_S.TryGetValue((affordance.Guid.ToString()), out Q_S_A))
                {
                    Q_S_A.Item1 = 0; // Initialize to 0 if the action is not found
                }

                //first prediction
                double maxQ_nS_nA = 0;
                string maxQ_nS_nA_name = "";
                
                
                
                ConcurrentDictionary<string, (double, int, Dictionary<int, double>)> Q_newState_actions;

                var selectedQTable2 = updateA ? qTable2 : qTable;

                if (selectedQTable1.TryGetValue(newState, out Q_newState_actions))
                {
                    var action = Q_newState_actions.OrderByDescending(a => a.Value.Item1).FirstOrDefault();

                    maxQ_nS_nA_name = action.Key;
                    
                    
                    
                }

                

                if (selectedQTable2.TryGetValue(newState, out var Q_newState_actions_fromTable2))
                {
                   
                    if (maxQ_nS_nA_name!=null && Q_newState_actions_fromTable2.TryGetValue(maxQ_nS_nA_name, out var actionValueFromTable2))
                    {
                        
                        maxQ_nS_nA = actionValueFromTable2.Item1;
                    }
                }

                // Update the Q value for the current state and action
                double new_Q_S_A = (1 - alpha) * Q_S_A.Item1 + alpha * (R_S_A + maxQ_nS_nA * gamma);
                var QSA_Info = (new_Q_S_A, affordance.GetDuration(), affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value));
                selectedQTable1[currentState][affordance.Name] = QSA_Info;

                //new_Q_S_A = Table1Value +  Table2Value
                if (selectedQTable2.TryGetValue(currentState, out var Q_CurrentState_actions_fromTable2))
                {
                    
                    if (Q_CurrentState_actions_fromTable2.TryGetValue(affordance.Name, out var CurrentActionValueFromTable2))
                    {
                        
                        new_Q_S_A += CurrentActionValueFromTable2.Item1;
                    }
                }

                if (new_Q_S_A > bestQ_S_A)
                {
                    bestQ_S_A = new_Q_S_A;
                    bestAffordance = affordance;
                    //bestAverageR_S_A = averageR_S_A;
                    //this.currentState = newState;

                }

                //V1 if sleep in the wait list, then direct run it
                if (weightSum >= 1000)
                {
                    sleep = affordance;
                    //sleepAverageR_S_A = averageR_S_A;
                }

                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }

            }

            this.TableNumberEachDay[now.Date] = this.qTable.Count + this.qTable2.Count;

            if (sleep != null)
            {
                //averageReward = sleepAverageR_S_A;
                this.lastSleepTime = now;
                return sleep;
            }


            //if (random1)
            //{
            //    return allAvailableAffordances[rnd1.Next(allAvailableAffordances.Count)];
            //}

            //averageReward = bestAverageR_S_A;
            return bestAffordance;

        }


        private ICalcAffordanceBase GetBestAffordanceFromListNewRL_Pre_Q_Learning([JetBrains.Annotations.NotNull] TimeStep time,
                                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            if(qTable.Count == 0)
            {
                LoadQTableFromFile();
            }
            string readed_next_affordance_name = next_affordance_name;
            string best_affordance_name = "";
            (double, int, Dictionary<int, double>) bestQSA_inCurrentState = (0, 0, new Dictionary<int, double>());

            //double epsilon1 = 0.1;
            //Random rnd1 = new Random(time.InternalStep);
            //Random rnd2 = new Random(time.InternalStep+1);
            //bool random1 = (rnd1.NextDouble() < epsilon1);
            //bool random2 = (rnd2.NextDouble() < epsilon1);

            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;
            ICalcAffordanceBase sarsa_affordacne = null;

            Dictionary<string, int> desire_level_before = null;

            
            foreach (var affordance in allAvailableAffordances)
            {
                if(affordance.Name.Contains("Replacement Activity"))
                {
                    continue;
                }

                var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(affordance, time, careForAll, out var thoughtstring, now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
                var desire_ValueBefore = calcTotalDeviationResult.desireName_ValueBeforeApply_Dict;
                var duration = calcTotalDeviationResult.realDuration;

                string sarsa_next_affordance_candi = null;

                string nowTimeState = makeTimeSpan(now, 0);
                string newTimeState = makeTimeSpan(now, duration);
                Dictionary<string, int> desire_level_after = MergeDictAndLevels(desire_ValueAfter);

                double alpha = 0.2;
                double gamma = 0.8;

                if (desire_level_before == null)
                {
                    desire_level_before = MergeDictAndLevels(desire_ValueBefore);
                    this.currentState = new (desire_level_before, nowTimeState);
                }

                (Dictionary<string, int>, string time) newState = (desire_level_after, newTimeState);

                 var R_S_A = -desireDiff + 1000000;

                if (!qTable.TryGetValue(currentState, out var Q_S))
                {
                    Q_S = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();
                    qTable[currentState] = Q_S;
                }

                (double, int, Dictionary<int, double>) Q_S_A;
                
                if (!Q_S.TryGetValue((affordance.Guid.ToString()), out Q_S_A))
                {
                    Q_S_A.Item1 = 0; // Initialize to 0 if the action is not found
                }

                //first prediction
                double maxQ_nS_nA = 0;
                int maxQ_nS_nA_duration = 0;
                Dictionary<int, double> maxQ_nS_nA_satValus = null;
                ConcurrentDictionary<string, (double, int, Dictionary<int, double>)> Q_newState_actions;
                
                if (max_qTable.TryGetValue(newState, out Q_newState_actions))
                {
                   
                    var action = Q_newState_actions.First();
                    maxQ_nS_nA = action.Value.Item1;
                    maxQ_nS_nA_duration = action.Value.Item2;
                    maxQ_nS_nA_satValus = action.Value.Item3;

                    sarsa_next_affordance_candi = action.Key;
                }
                else
                {
                    next_affordance_name = "";
                }

                //second prediction
                double maxQ_nnS_nnA = 0;

                // Update the Q value for the current state and action
                double new_Q_S_A = (1 - alpha) * Q_S_A.Item1 + alpha * (R_S_A + maxQ_nS_nA * gamma  + maxQ_nnS_nnA * gamma * gamma);
                var QSA_Info = (new_Q_S_A, affordance.GetDuration(), affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value));
                qTable[currentState][affordance.Name] = QSA_Info;

                if(new_Q_S_A > bestQ_S_A)
                {
                    bestQ_S_A = new_Q_S_A;
                    bestAffordance = affordance;
                    //this.currentState = newState;
                    next_affordance_name = sarsa_next_affordance_candi;
                    best_affordance_name = affordance.Name;
                    bestQSA_inCurrentState = QSA_Info;
                    
                }

                //V1 if sleep in the wait list, then direct run it
                if (weightSum >= 1000)
                {                
                    sleep = affordance;
                }

                if(affordance.Name == readed_next_affordance_name)
                {
                    sarsa_affordacne = affordance;
                }

                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }
                             
            }
            max_qTable.AddOrUpdate(
                currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>
                {
                    [best_affordance_name] = bestQSA_inCurrentState
                },
                (key, existingVal) =>
                {
                    var newDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();
                    newDict.TryAdd(best_affordance_name, bestQSA_inCurrentState);
                    return newDict; 
                }
            );

            if (sleep != null)
            {
                return sleep;
            }


            //if (random1)
            //{
            //    return allAvailableAffordances[rnd1.Next(allAvailableAffordances.Count)];
            //}

            return bestAffordance;

        }

        private ICalcAffordanceBase GetBestAffordanceFromListNewRL_Post_Q_Learning([JetBrains.Annotations.NotNull] TimeStep time,
                                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            if (qTable.Count == 0)
            {
                LoadQTableFromFile();
            }

            double epsilon1 = 0.1;
            Random rnd1 = new Random(time.InternalStep);
            bool random1 = (rnd1.NextDouble() < epsilon1);

            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;
            ICalcAffordanceBase sarsa_affordacne = null;

            Dictionary<string, int> desire_level_before = null;

            (double, int, Dictionary<int, double>) bestQSA_inCurrentState = (0, 0, new Dictionary<int, double>());


            foreach (var affordance in allAvailableAffordances)
            {
                if (affordance.Name.Contains("Replacement Activity"))
                {
                    continue;
                }

                var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(affordance, time, careForAll, out var thoughtstring, now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
                var desire_ValueBefore = calcTotalDeviationResult.desireName_ValueBeforeApply_Dict;
                var duration = calcTotalDeviationResult.realDuration;

                string sarsa_next_affordance_candi = null;

                string nowTimeState = makeTimeSpan(now, 0);
                string newTimeState = makeTimeSpan(now, duration);
                Dictionary<string, int> desire_level_after = MergeDictAndLevels(desire_ValueAfter);

                double alpha = 0.2;
                double gamma = 0.8;

                if (desire_level_before == null)
                {
                    desire_level_before = MergeDictAndLevels(desire_ValueBefore);
                    this.currentState = new(desire_level_before, nowTimeState);
                }

                //V1 if sleep in the wait list, then direct run it
                if (weightSum >= 1000)
                {
                    sleep = affordance;
                }

                (Dictionary<string, int>, string time) newState = (desire_level_after, newTimeState);

                var R_S_A = -desireDiff + 1000000;

                if (!qTable.TryGetValue(currentState, out var Q_S))
                {
                    Q_S = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();
                    qTable[currentState] = Q_S;
                }

                (double, int, Dictionary<int, double>) Q_S_A;

                if (!Q_S.TryGetValue((affordance.Guid.ToString()), out Q_S_A))
                {
                    Q_S_A.Item1 = 0; // Initialize to 0 if the action is not found
                }

                //first prediction
                double maxQ_nS_nA = 0;
                int maxQ_nS_nA_duration = 0;
                Dictionary<int, double> maxQ_nS_nA_satValus = null;
                ConcurrentDictionary<string, (double, int, Dictionary<int, double>)> Q_newState_actions;

                if (qTable.TryGetValue(newState, out Q_newState_actions))
                {
                    var action = Q_newState_actions.OrderByDescending(a => a.Value.Item1).FirstOrDefault();

                    maxQ_nS_nA = action.Value.Item1;
                    maxQ_nS_nA_duration = action.Value.Item2;
                    maxQ_nS_nA_satValus = action.Value.Item3;
                }
                //second prediction
                double maxQ_nnS_nnA = 0;

                // Update the Q value for the current state and action
                double new_Q_S_A = (1 - alpha) * Q_S_A.Item1 + alpha * (R_S_A + maxQ_nS_nA * gamma + maxQ_nnS_nnA * gamma * gamma);
                var QSA_Info = (new_Q_S_A, affordance.GetDuration(), affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value));
                //qTable[currentState][affordance.Name] = QSA_Info;

                if (Q_S_A.Item1 > bestQ_S_A)
                {
                    bestQ_S_A = Q_S_A.Item1;
                    bestAffordance = affordance;
                    bestQSA_inCurrentState= QSA_Info;
                    //this.currentState = newState;
                }

                if(weightSum >= 1000)
                {
                    bestQ_S_A = Q_S_A.Item1;
                    bestAffordance = affordance;
                    bestQSA_inCurrentState = QSA_Info;
                    break;
                }


                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }

            }

            qTable.AddOrUpdate(
                               currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>
                               {
                                   [bestAffordance.Name] = bestQSA_inCurrentState
                },
                                              (key, existingVal) =>
                                              {
                    var newDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();
                    newDict.TryAdd(bestAffordance.Name, bestQSA_inCurrentState);
                    return newDict;
                }
                                                         );

            if (sleep != null)
            {
                return sleep;
            }


            //if (random1)
            //{
            //    return allAvailableAffordances[rnd1.Next(allAvailableAffordances.Count)];
            //}

            return bestAffordance;

        }

        private ICalcAffordanceBase GetBestAffordanceFromList_RL([JetBrains.Annotations.NotNull] TimeStep time,
                                                      [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            //return GetBestAffordanceFromListNewRL_Pre_Q_Learning(time, allAvailableAffordances, careForAll, now);
            //return GetBestAffordanceFromListNewRL_Post_Q_Learning(time, allAvailableAffordances, careForAll, now);
            //return GetBestAffordanceFromListNewRL_3_step_SARSA(time, allAvailableAffordances, careForAll, now);
            return GetBestAffordanceFromListNewRL_2_step_SARSA(time, allAvailableAffordances, careForAll, now);
            //return GetBestAffordanceFromListNewRL_Double_Q_Learning(time, allAvailableAffordances, careForAll, now);
        }

        private ICalcAffordanceBase GetBestAffordanceFromListNewRL_2_step_SARSA([JetBrains.Annotations.NotNull] TimeStep time,
                                                      [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            if (qTable.Count == 0)
            {
                LoadQTableFromFile();
            }
            string readed_next_affordance_name = next_affordance_name;
            next_affordance_name = "";
            string best_affordance_name = "";
            (double, int, Dictionary<int, double>) bestQSA_inCurrentState = (0, 0, new Dictionary<int, double>());
            
            int currentSearchCounter = allAvailableAffordances.Count;
            int currentFoundCounter = 0;

            //double epsilon1 = 0.05;
            //Random rnd1 = new Random(time.InternalStep);
            //Random rnd2 = new Random(time.InternalStep + 1);
            //bool random1 = (rnd1.NextDouble() < epsilon1);
            //bool random2 = (rnd2.NextDouble() < epsilon1);
            //bool random1= false;
            //bool random2 = false;

            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;
            ICalcAffordanceBase sarsa_affordacne = null;
            Dictionary<string, int> desire_level_before = null;

            object locker = new object();

            //first prediction
            //foreach (var affordance in allAvailableAffordances)
            Parallel.ForEach(allAvailableAffordances, affordance =>
            {
                if (affordance.Name.Contains("Replacement Activity"))
                {
                    //continue;
                    lock (locker)
                    {
                        currentFoundCounter++;
                    }
                    return;
                }
                int affordanceSearchCounter = 0;
                int affordanceFoundCounter = 0;
                //ICalcAffordanceBase affordance = random1 ? allAvailableAffordances[rnd1.Next(allAvailableAffordances.Count)] : affordance1;

                int duration = affordance.GetRealDuration(time);
                bool isInterruptable = affordance.IsInterruptable;
                var satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);
                var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(null, time, careForAll, out var thoughtstring, now, satValue: satisfactionvalues, newDuration: duration, interruptable: isInterruptable);

                //var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(affordance, time, careForAll, out var thoughtstring, now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
                var desire_ValueBefore = calcTotalDeviationResult.desireName_ValueBeforeApply_Dict;
                //var duration = calcTotalDeviationResult.realDuration;

                string sarsa_next_affordance_candi = "";

                string nowTimeState = makeTimeSpan(now, 0);
                string newTimeState = makeTimeSpan(now, duration);

                TimeStep currentTimeStep = time;
                TimeStep nextTimeStep = currentTimeStep.AddSteps(duration);
                HashSet<string> nextAllAffordanceNames = new HashSet<string>();
                var srcList = _normalPotentialAffs.PotentialAffordances;
                foreach (var calcAffordanceBase in srcList)
                {
                    var busynessResult = calcAffordanceBase.GetRestTimeWindows(nextTimeStep);
                    //
                    //Debug.WriteLine(busynessResult);
                    if (busynessResult == 1 && !calcAffordanceBase.Name.Contains("Replacement Activity"))
                    {
                        //resultingAff.Add(calcAffordanceBase);
                        nextAllAffordanceNames.Add(calcAffordanceBase.Name);
                    }
                }

                Dictionary<string, int> desire_level_after = MergeDictAndLevels(desire_ValueAfter);

                double alpha = 0.2;
                double gamma = 0.8; //0.8

                if (desire_level_before == null)
                {
                    lock(locker)
                    {
                        if (desire_level_before == null)
                        {
                            desire_level_before = MergeDictAndLevels(desire_ValueBefore);
                            this.currentState = new(desire_level_before, nowTimeState);                        
                        }
                    }
                }

                (Dictionary<string, int>, string) newState = (desire_level_after, newTimeState);

                var R_S_A = -desireDiff + 1000000;

                //if (!qTable.TryGetValue(currentState, out var Q_S))
                //{
                //    Q_S = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>();
                //    //qTable[currentState] = Q_S;
                //    qTable.AddOrUpdate(currentState, Q_S, (key, existingVal) => Q_S);

                //}
                //else
                //{
                //    affordanceFoundCounter++;
                //}

                var Q_S = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>());

                if (Q_S.Count > 0)
                {
                    affordanceFoundCounter++;
                }

                (double, int, Dictionary<int, double>) Q_S_A;

                affordanceSearchCounter++;
                if (!Q_S.TryGetValue((affordance.Guid.ToString()), out Q_S_A))
                {
                    Q_S_A.Item1 = 0; // Initialize to 0 if the action is not found
                }else
                {
                    affordanceFoundCounter++;
                }

                //second prediction
                double max_prediction = 0;
                ConcurrentDictionary<string, (double, int, Dictionary<int, double>)> Q_newState_actions;

               affordanceSearchCounter+=nextAllAffordanceNames.Count;
                if (qTable.TryGetValue(newState, out Q_newState_actions))
                {
                    //affordanceSearchCounter += Q_newState_actions.Count;
                    foreach (var action in Q_newState_actions)
                    {
                        //KeyValuePair<string, (double, int, Dictionary<int, double>)> action = new KeyValuePair<string, (double, int, Dictionary<int, double>)>();                        
                        //action = random2 ? Q_newState_actions.ElementAt(rnd2.Next(Q_newState_actions.Count)) : action1;
                        //double Q_nS_nA = action.Value.Item1;

                        //if (!nextAllAffordanceNames.Contains(action.Key))
                        //{
                        //    continue;
                        //}
                        affordanceFoundCounter++;
                        double Q_nS_nA = action.Value.Item1;
                        int Q_nS_nA_duration = action.Value.Item2;
                        Dictionary<int, double> Q_nS_nA_satValus = action.Value.Item3;

                        var TimeAfter_nS = now.AddMinutes(duration).AddMinutes(Q_nS_nA_duration);
                        List<double> DesireValueAfter_nS = desire_ValueAfter.Values.Select(value => value.Item2).ToList();
                        var calcTotalDeviationResultAfter_nS = PersonDesires.CalcEffectPartlyRL_New(null, time, true, out var thoughtstrin_new, now, DesireValueAfter_nS, Q_nS_nA_satValus, Q_nS_nA_duration);

                        var next_desireDiff = calcTotalDeviationResultAfter_nS.totalDeviation;
                        var R_S_A_nS = -next_desireDiff + 1000000;

                        //third prediction (arg max)
                        var desire_ValueAfter_nS = calcTotalDeviationResultAfter_nS.desireName_ValueAfterApply_Dict;
                        (Dictionary<string, int>, string time) new_newState = (MergeDictAndLevels(desire_ValueAfter_nS), makeTimeSpan(TimeAfter_nS, 0));
                        double max_Q_nnS_nnA = 0;

                        affordanceSearchCounter++;
                        if (max_qTable.TryGetValue(new_newState, out var Q_newState_actions_nS))
                        {
                            //max_Q_nnS_nnA = Q_newState_actions_nS.Max(action2 => action2.Value.Item1);
                            max_Q_nnS_nnA = Q_newState_actions_nS.First().Value.Item1;
                            affordanceFoundCounter++;
                        }

                        //double prediction = R_S_A_nS * gamma + max_Q_nnS_nnA * gamma * gamma;
                        double prediction = (max_Q_nnS_nnA>0)? R_S_A_nS * gamma + max_Q_nnS_nnA * gamma * gamma : Q_nS_nA * gamma;
                        if (prediction > max_prediction)
                        {
                            max_prediction = prediction;
                            sarsa_next_affordance_candi = action.Key;
                        }

                        //if (random2)
                        //{
                        //    break;
                        //}
                    }
                }


                // Update the Q value for the current state and action
                double new_Q_S_A = (1 - alpha) * Q_S_A.Item1 + alpha * (R_S_A + max_prediction);
                var QSA_Info = (new_Q_S_A, affordance.GetDuration(), affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value));
                //qTable[currentState][affordance.Name] = QSA_Info;
                //qTable[currentState].AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);
                var currentStateData = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>());
                currentStateData.AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);

                //if (new_Q_S_A > bestQ_S_A)
                //{
                //    lock (locker)
                //    {
                //        if (new_Q_S_A > bestQ_S_A)
                //        {
                //            bestQ_S_A = new_Q_S_A;
                //            bestAffordance = affordance;
                //            best_affordance_name = affordance.Name;
                //            next_affordance_name = sarsa_next_affordance_candi;
                //            bestQSA_inCurrentState = QSA_Info;
                //        }
                //    }
                //}

                lock (locker)
                {
                    if (new_Q_S_A > bestQ_S_A)
                    {
                        bestQ_S_A = new_Q_S_A;
                        bestAffordance = affordance;
                        best_affordance_name = affordance.Name;
                        if(affordance.Name == readed_next_affordance_name)
                        {
                            next_affordance_name = sarsa_next_affordance_candi;
                        }
                        
                        bestQSA_inCurrentState = QSA_Info;
                    }
                    currentSearchCounter += affordanceSearchCounter;
                    currentFoundCounter += affordanceFoundCounter;

                }

                //V1 if sleep in the wait list, then direct run it
                if (weightSum >= 1000)
                {
                    sleep = affordance;
                }

                if (affordance.Name == readed_next_affordance_name)
                {
                    sarsa_affordacne = affordance;
                }

                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }

                //if(random1)
                //{
                //    bestAffordance = affordance;
                //    break;
                //}
            });

            max_qTable.AddOrUpdate(
                currentState, 
                new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>
                {
                    [best_affordance_name] = bestQSA_inCurrentState
                },                
                (key, existingVal) =>
                {
                    var newDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>
                    {
                        [best_affordance_name] = bestQSA_inCurrentState
                    };
                    return newDict; // 返回新的字典作为该键的值
                }
            );

            searchCounter += currentSearchCounter;
            foundCounter += currentFoundCounter;
            

            if (sleep != null)
            {
                return sleep;
                next_affordance_name = "";
            }

            if (sarsa_affordacne != null)
            {
                return sarsa_affordacne;
            }

            return bestAffordance;

        }

        private ICalcAffordanceBase GetBestAffordanceFromListNewRL_3_step_SARSA([JetBrains.Annotations.NotNull] TimeStep time,
                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            if (qTable == null)
            {
                LoadQTableFromFile();
            }
            string readed_next_affordance_name = next_affordance_name;
            next_affordance_name = "";
            string best_affordance_name = "";
            (double, int, Dictionary<int, double>) bestQSA_inCurrentState = (0, 0, new Dictionary<int, double>());

            int currentSearchCounter = allAvailableAffordances.Count;
            int currentFoundCounter = 0;

            //double epsilon1 = 0.05;
            //Random rnd1 = new Random(time.InternalStep);
            //Random rnd2 = new Random(time.InternalStep + 1);
            //bool random1 = (rnd1.NextDouble() < epsilon1);
            //bool random2 = (rnd2.NextDouble() < epsilon1);
            //bool random1= false;
            //bool random2 = false;

            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;
            ICalcAffordanceBase sarsa_affordacne = null;
            Dictionary<string, int> desire_level_before = null;

            object locker = new object();

            //first prediction
            //foreach (var affordance in allAvailableAffordances)
            Parallel.ForEach(allAvailableAffordances, affordance =>
            {
                if (affordance.Name.Contains("Replacement Activity"))
                {
                    //continue;
                    lock (locker)
                    {
                        currentFoundCounter++;
                    }
                    return;
                }
                int affordanceSearchCounter = 0;
                int affordanceFoundCounter = 0;
                //ICalcAffordanceBase affordance = random1 ? allAvailableAffordances[rnd1.Next(allAvailableAffordances.Count)] : affordance1;

                int duration = affordance.GetRealDuration(time);
                bool isInterruptable = affordance.IsInterruptable;
                var satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);
                var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(null, time, careForAll, out var thoughtstring, now, satValue: satisfactionvalues,newDuration: duration, interruptable:isInterruptable);
                
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
                var desire_ValueBefore = calcTotalDeviationResult.desireName_ValueBeforeApply_Dict;
                //var duration = calcTotalDeviationResult.realDuration;

                string sarsa_next_affordance_candi = "";

                string nowTimeState = makeTimeSpan(now, 0);
                string newTimeState = makeTimeSpan(now, duration);

                TimeStep currentTimeStep = time;
                TimeStep nextTimeStep = currentTimeStep.AddSteps(duration);
                HashSet<string> nextAllAffordanceNames = new HashSet<string>();
                var srcList = _normalPotentialAffs.PotentialAffordances;
                foreach (var calcAffordanceBase in srcList)
                {
                    var busynessResult = calcAffordanceBase.GetRestTimeWindows(nextTimeStep);
                    //
                    //Debug.WriteLine(busynessResult);
                    if (busynessResult == 1 && !calcAffordanceBase.Name.Contains("Replacement Activity"))
                    {
                        //resultingAff.Add(calcAffordanceBase);
                        nextAllAffordanceNames.Add(calcAffordanceBase.Name);
                    }
                }

                Dictionary<string, int> desire_level_after = MergeDictAndLevels(desire_ValueAfter);

                double alpha = 0.2;
                double gamma = 0.8;

                if (desire_level_before == null)
                {
                    lock (locker)
                    {
                        if (desire_level_before == null)
                        {
                            desire_level_before = MergeDictAndLevels(desire_ValueBefore);
                            this.currentState = new(desire_level_before, nowTimeState);
                        }
                    }
                }

                (Dictionary<string, int>, string) newState = (desire_level_after, newTimeState);

                var R_S_A = -desireDiff + 1000000;

                var Q_S = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>());

                if (Q_S.Count > 0)
                {
                    affordanceFoundCounter++;
                }

                (double, int, Dictionary<int, double>) Q_S_A;

                affordanceSearchCounter++;
                if (!Q_S.TryGetValue((affordance.Guid.ToString()), out Q_S_A))
                {
                    Q_S_A.Item1 = 0; // Initialize to 0 if the action is not found
                }
                else
                {
                    affordanceFoundCounter++;
                }

                //second prediction
                double max_prediction = 0;
                ConcurrentDictionary<string, (double, int, Dictionary<int, double>)> Q_newState_actions;

                affordanceSearchCounter += nextAllAffordanceNames.Count;
                if (qTable.TryGetValue(newState, out Q_newState_actions))
                {
                    //affordanceSearchCounter += Q_newState_actions.Count;
                    foreach (var action in Q_newState_actions)
                    {
                        //KeyValuePair<string, (double, int, Dictionary<int, double>)> action = new KeyValuePair<string, (double, int, Dictionary<int, double>)>();                        
                        //action = random2 ? Q_newState_actions.ElementAt(rnd2.Next(Q_newState_actions.Count)) : action1;
                        //double Q_nS_nA = action.Value.Item1;

                        affordanceFoundCounter++;
                        double Q_nS_nA = action.Value.Item1;
                        int Q_nS_nA_duration = action.Value.Item2;
                        Dictionary<int, double> Q_nS_nA_satValus = action.Value.Item3;

                        var TimeAfter_nS = now.AddMinutes(duration).AddMinutes(Q_nS_nA_duration);
                        List<double> DesireValueAfter_nS = desire_ValueAfter.Values.Select(value => value.Item2).ToList();
                        var calcTotalDeviationResultAfter_nS = PersonDesires.CalcEffectPartlyRL_New(null, time, careForAll, out var thoughtstrin_new, now, DesireValueAfter_nS, Q_nS_nA_satValus, Q_nS_nA_duration);

                        var next_desireDiff = calcTotalDeviationResultAfter_nS.totalDeviation;
                        var R_S_A_nS = -next_desireDiff + 1000000;

                        //third prediction
                        var desire_ValueAfter_nS = calcTotalDeviationResultAfter_nS.desireName_ValueAfterApply_Dict;
                        (Dictionary<string, int>, string time) new_newState = (MergeDictAndLevels(desire_ValueAfter_nS), makeTimeSpan(TimeAfter_nS, 0));
                        
                        ConcurrentDictionary<string, (double, int, Dictionary<int, double>)> Q_new_nextState_actions;
                        if (qTable.TryGetValue(new_newState, out Q_new_nextState_actions) && affordance.Name==readed_next_affordance_name)
                        {
                            foreach (var action2 in Q_new_nextState_actions)
                            {
                                double Q_nnS_nnA = action2.Value.Item1;
                                int Q_nnS_nnA_duration = action2.Value.Item2;
                                Dictionary<int, double> Q_nnS_nnA_satValus = action2.Value.Item3;

                                var TimeAfter_nnS = TimeAfter_nS.AddMinutes(Q_nS_nA_duration);
                                List<double> DesireValueAfter_nnS = desire_ValueAfter_nS.Values.Select(value => value.Item2).ToList();
                                var calcTotalDeviationResultAfter_nnS = PersonDesires.CalcEffectPartlyRL_New(null, time, careForAll, out var thoughtstrin_new_nnS, now, DesireValueAfter_nnS, Q_nnS_nnA_satValus, Q_nnS_nnA_duration);

                                var next_desireDiff_nnS = calcTotalDeviationResultAfter_nnS.totalDeviation;
                                var R_S_A_nnS = -next_desireDiff_nnS + 1000000;

                                var desire_ValueAfter_nnS = calcTotalDeviationResultAfter_nnS.desireName_ValueAfterApply_Dict;
                                (Dictionary<string, int>, string time) new_new_newState = (MergeDictAndLevels(desire_ValueAfter_nnS), makeTimeSpan(TimeAfter_nnS, 0));
                                double max_Q_nnnS_nnnA = 0;

                                //fourth prediction (arg max)
                                affordanceSearchCounter++;
                                if (max_qTable.TryGetValue(new_new_newState, out var Q_newState_actions_nS))
                                {
                                    //max_Q_nnS_nnA = Q_newState_actions_nS.Max(action2 => action2.Value.Item1);
                                    max_Q_nnnS_nnnA = Q_newState_actions_nS.First().Value.Item1;
                                    affordanceFoundCounter++;
                                }

                                //double prediction = R_S_A_nS * gamma + R_S_A_nnS * gamma * gamma + max_Q_nnnS_nnnA * gamma * gamma * gamma;
                                double prediction = 0;
                                
                                prediction = (max_Q_nnnS_nnnA > 0) ? R_S_A_nS * gamma + R_S_A_nnS * Math.Pow(gamma, 2) + max_Q_nnnS_nnnA * Math.Pow(gamma, 3) : R_S_A_nS * gamma + Q_nnS_nnA * Math.Pow(gamma, 2);
                                
                                if (prediction > max_prediction)
                                {
                                    max_prediction = prediction;
                                    sarsa_next_affordance_candi = action.Key;
                                }

                            }
                        }
                        else
                        {
                            //third prediction (arg max)
                            double max_Q_nnS_nnA = 0;
                            if (max_qTable.TryGetValue(new_newState, out var Q_newState_actions_nS))
                            {
                                //max_Q_nnS_nnA = Q_newState_actions_nS.Max(action2 => action2.Value.Item1);
                                max_Q_nnS_nnA = Q_newState_actions_nS.First().Value.Item1;
                            }

                            //double prediction = Q_nS_nA * gamma;
                            double prediction =(max_Q_nnS_nnA > 0)? R_S_A_nS * gamma + max_Q_nnS_nnA * Math.Pow(gamma, 2) : Q_nS_nA * gamma;
                            if (prediction > max_prediction)
                            {
                                max_prediction = prediction;
                                sarsa_next_affordance_candi = action.Key;
                            }

                        }

                        //if (random2)
                        //{
                        //    break;
                        //}
                    }
                }


                // Update the Q value for the current state and action
                double new_Q_S_A = (1 - alpha) * Q_S_A.Item1 + alpha * (R_S_A + max_prediction);
                var QSA_Info = (new_Q_S_A, affordance.GetDuration(), affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value));
                //qTable[currentState][affordance.Name] = QSA_Info;
                //qTable[currentState].AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);
                var currentStateData = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>());
                currentStateData.AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);


                lock (locker)
                {
                    if (new_Q_S_A > bestQ_S_A)
                    {
                        bestQ_S_A = new_Q_S_A;
                        bestAffordance = affordance;
                        best_affordance_name = affordance.Name;
                        if (affordance.Name == readed_next_affordance_name)
                        {
                            next_affordance_name = sarsa_next_affordance_candi;
                        }

                        bestQSA_inCurrentState = QSA_Info;
                    }
                    currentSearchCounter += affordanceSearchCounter;
                    currentFoundCounter += affordanceFoundCounter;

                }

                //V1 if sleep in the wait list, then direct run it
                if (weightSum >= 1000)
                {
                    sleep = affordance;
                }

                if (affordance.Name == readed_next_affordance_name)
                {
                    sarsa_affordacne = affordance;
                }

                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }
                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                            "Desirediff for " + affordance.Name + " is :" +
                            desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }
            });

            if (best_affordance_name != "")
            {
                max_qTable.AddOrUpdate(
                    currentState,
                    new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>
                    {
                        [best_affordance_name] = bestQSA_inCurrentState
                    },
                    (key, existingVal) =>
                    {
                        var newDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>)>
                        {
                            [best_affordance_name] = bestQSA_inCurrentState
                        };
                        return newDict; // 返回新的字典作为该键的值
                    }
                );
            }

            searchCounter += currentSearchCounter;
            foundCounter += currentFoundCounter;


            if (sleep != null)
            {
                return sleep;
                next_affordance_name = "";
            }

            if (sarsa_affordacne != null)
            {
                return sarsa_affordacne;
            }

            return bestAffordance;

        }

        public void setNewWeight(string aff, decimal ratio)
        {
            
            if(updatedWeight.TryGetValue(aff, out var weight))
            {
                updatedWeight[aff] = Math.Max(weight + ratio,0.1m);
            }
            else
            {
                updatedWeight[aff] = 1+ratio;
            }
        }

        static decimal TunningDeviation(double deviationRAW, int duration)
        {
            //double tunning = (2 - Math.Exp(-duration));
            //double result = deviationRAW*tunning;
            //return (decimal)result;

            double rate = duration / (24 * 60);
            double alpha = 4;//8
            double tunning = (2 - Math.Exp(-alpha * rate));
            
            double result = deviationRAW * tunning;
            //double result = deviationRAW;
            //return (decimal)result/ (decimal)weight_sum;
            return (decimal)result;
        }

        //Parallel Compute
        private ICalcAffordanceBase GetBestAffordanceFromListParallelNew([JetBrains.Annotations.NotNull] TimeStep time,
                                                  [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            ConcurrentBag<Tuple<decimal, ICalcAffordanceBase>> results = new ConcurrentBag<Tuple<decimal, ICalcAffordanceBase>>();

            Parallel.ForEach(allAvailableAffordances, affordance =>
            {
                var duration = affordance.GetDuration();
                //var desireDiff = PersonDesires.CalcEffectPartly(affordance.Satisfactionvalues, out var thoughtstring, affordance.Name, affordance.IsInterruptable, careForAll, duration, time);
                //var desireDiff = PersonDesires.CalcEffectPartly(affordance, time, careForAll, out var thoughtstring);
                var calcTotalDeviationResult = PersonDesires.CalcEffectPartly(affordance, time, careForAll, out var thoughtstring, now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                // Log thoughts
                if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile))
                {
                    if (_calcRepo.Logfile.ThoughtsLogFile1 == null)
                    {
                        throw new LPGException("Logfile was null.");
                    }

                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time,
                        "Desirediff for " + affordance.Name + " is :" +
                        desireDiff.ToString("#,##0.0", Config.CultureInfo) + " In detail: " + thoughtstring),
                        _calcPerson.HouseholdKey);
                }

                results.Add(Tuple.Create(desireDiff, affordance));
            });

            var bestResult = results.OrderBy(r => r.Item1).First();
            var bestdiff = bestResult.Item1;
            var bestaff = bestResult.Item2;
            var bestaffordances = results.Where(r => r.Item1 == bestdiff).Select(r => r.Item2).ToList();

            // if multiple best
            if (bestaffordances.Count > 1)
            {
                bestaff = PickRandomAffordanceFromEquallyAttractiveOnes(bestaffordances, time, this, _calcPerson.HouseholdKey);
            }

            return bestaff;
        }


        private void Init([JetBrains.Annotations.NotNull][ItemNotNull] List<CalcLocation> locs, [JetBrains.Annotations.NotNull] PotentialAffs pa, bool sickness)
        {
            pa.PotentialAffordances.Clear();
            pa.PotentialInterruptingAffordances.Clear();
            pa.PotentialAffordancesWithSubAffordances.Clear();
            pa.PotentialAffordancesWithInterruptingSubAffordances.Clear();
            // alle affordanzen finden f黵 alle orte
            foreach (var loc in locs) {
                foreach (var calcAffordance in loc.Affordances) {
                    if (NewIsBasicallyValidAffordance(calcAffordance, sickness, false)) {
                        pa.PotentialAffordances.Add(calcAffordance);
                        if (calcAffordance.IsInterrupting) {
                            pa.PotentialInterruptingAffordances.Add(calcAffordance);
                        }
                    }

                    foreach (var subAffordance in calcAffordance.SubAffordances) {
                        if (NewIsBasicallyValidAffordance(subAffordance, sickness, false)) {
                            if (!pa.PotentialAffordancesWithSubAffordances.Contains(calcAffordance)) {
                                pa.PotentialAffordancesWithSubAffordances.Add(calcAffordance);
                            }

                            if (subAffordance.IsInterrupting) {
                                if (!pa.PotentialAffordancesWithInterruptingSubAffordances.Contains(calcAffordance)) {
                                    pa.PotentialAffordancesWithInterruptingSubAffordances.Add(calcAffordance);
                                }
                            }
                        }
                    }
                }
            }

            pa.PotentialAffordances.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            pa.PotentialInterruptingAffordances.Sort(
                (x, y) => string.CompareOrdinal(x.Name, y.Name));
            pa.PotentialAffordancesWithSubAffordances.Sort(
                (x, y) => string.CompareOrdinal(x.Name, y.Name));
            pa.PotentialAffordancesWithInterruptingSubAffordances.Sort(
                (x, y) => string.CompareOrdinal(x.Name, y.Name));
        }

        [JetBrains.Annotations.NotNull]
        [ItemNotNull]
        private List<ICalcAffordanceBase> NewGetAllViableAffordancesAndSubsNew([JetBrains.Annotations.NotNull] TimeStep timeStep,
                                                                            AffordanceStatusClass? errors,
                                                                            bool getOnlyInterrupting,
                                                                            [JetBrains.Annotations.NotNull] PotentialAffs potentialAffs, bool tryHarder)
        {
            
            var getOnlyRelevantDesires = getOnlyInterrupting; // just for clarity
            // normal affs
            var resultingAff = new List<ICalcAffordanceBase>();
            List<ICalcAffordanceBase> srcList;
            if (getOnlyInterrupting) {
                srcList = potentialAffs.PotentialInterruptingAffordances;
            }
            else {
                srcList = potentialAffs.PotentialAffordances;
            }
            
            //bool foundWorkAsTeacher1 = srcList.Any(a => a.Name == "work as teacher");
            //bool foundVisit1 = srcList.Any(a => a.Name == "visit the theater");


            foreach (var calcAffordanceBase in srcList) {
                if (NewIsAvailableAffordanceNew(timeStep, calcAffordanceBase, errors, getOnlyRelevantDesires,
                    CurrentLocation.CalcSite, tryHarder)) {
                    resultingAff.Add(calcAffordanceBase);
                }
                //here exist a filter!!!
            }
            
            //if(getOnlyInterrupting == false)
            //{
            //    Debug.WriteLine("Found worker before " + foundWorkAsTeacher1 + "    found worker after " + foundWorkAsTeacher2
            //    + "  Found visit before  " + foundVisit1 + "   found visit after  " + foundVisit2);
            //}
            

            // subaffs

            List<ICalcAffordanceBase> subSrcList;
            if (getOnlyInterrupting) {
                subSrcList = potentialAffs.PotentialAffordancesWithInterruptingSubAffordances;
            }
            else {
                subSrcList = potentialAffs.PotentialAffordancesWithSubAffordances;
            }

            foreach (var affordance in subSrcList) {
                var spezsubaffs =
                    affordance.CollectSubAffordances(timeStep, getOnlyInterrupting,  CurrentLocation);
                if (spezsubaffs.Count > 0) {
                    foreach (var spezsubaff in spezsubaffs) {
                        if (NewIsAvailableAffordanceNew(timeStep, spezsubaff, errors,
                            getOnlyRelevantDesires, CurrentLocation.CalcSite,tryHarder)) {
                            resultingAff.Add(spezsubaff);
                        }
                    }
                }
            }

            if (getOnlyInterrupting) {
                foreach (var affordance in resultingAff) {
                    if (!PersonDesires.HasAtLeastOneDesireBelowThreshold(affordance)) {
                        throw new LPGException("something went wrong while getting an interrupting affordance!");
                    }
                }
            }

            return resultingAff;
        }

        private bool NewIsAvailableAffordanceNew([JetBrains.Annotations.NotNull] TimeStep timeStep,
                                              [JetBrains.Annotations.NotNull] ICalcAffordanceBase aff,
                                              AffordanceStatusClass? errors, bool checkForRelevance,
                                              CalcSite? srcSite, bool ignoreAlreadyExecutedActivities)
        {
            //Debug.WriteLine(aff.Name);
            if (_calcRepo.CalcParameters.TransportationEnabled) {
                if (aff.Site != srcSite && !(aff is AffordanceBaseTransportDecorator)) {
                    //person is not at the right place and can't transport -> not available.
                    //Debug.WriteLine("   Not at the right place");
                    return false;
                }
            }

            if (!ignoreAlreadyExecutedActivities && _previousAffordances.Contains(aff))
            {
                if (errors != null)
                {
                    errors.Reasons.Add(new AffordanceStatusTuple(aff, "Just did this."));
                }

                return false;
            }

            //Debug.WriteLine(aff.Name);

            var busynessResult = aff.IsBusyNew(timeStep, CurrentLocation, _calcPerson);
            //Debug.WriteLine(busynessResult);
            if (busynessResult != BusynessType.NotBusy) {
                if (errors != null) {
                    errors.Reasons.Add(new AffordanceStatusTuple(aff, "Affordance is busy:" + busynessResult.ToString()));
                }
                //Debug.WriteLine("   Affordance is busy");
                return false;
                //here is a filter
            }

            if (checkForRelevance && !PersonDesires.HasAtLeastOneDesireBelowThreshold(aff)) {
                if (errors != null) {
                    errors.Reasons.Add(new AffordanceStatusTuple(aff,
                        "Person has no desires below the threshold for this affordance, so it is not relevant right now."));
                }
                //Debug.WriteLine("   No desires below");
                return false;
            }

            return true;
        }

        private int SetBusy([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull] ICalcProfile personCalcProfile, [JetBrains.Annotations.NotNull] CalcLocation loc, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                            bool needsLight)
        {
            if (_calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (//_lf == null ||
                    _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                    throw new LPGException("Logfile was null.");
                }

                _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                    new ThoughtEntry(this, time,
                        "Starting to execute " + personCalcProfile.Name + ", basis duration " +
                        personCalcProfile.StepValues.Count +
                        " time factor " + personCalcProfile.TimeFactor + ", total duration " +
                        personCalcProfile.StepValues.Count),
                    _calcPerson.HouseholdKey);
            }

            var isLightActivationneeded = false;
            //var stepvaluesCompressed = CalcProfile.CompressExpandDoubleArray(profile.StepValues, timeFactor);
            var lightprofile = new List<double>(personCalcProfile.StepValues.Count);
            for (var i = 0; i < personCalcProfile.StepValues.Count; i++) {
                lightprofile.Add(0);
            }

            for (var idx = 0; idx < personCalcProfile.StepValues.Count &&  idx+ time.InternalStep < _isBusy.Length; idx++) {
                if (personCalcProfile.StepValues[idx] > 0) {
                    _isBusy[time.InternalStep + idx] = true;
                    if (!isDaylight.Status[time.InternalStep + idx] && needsLight) {
                        lightprofile[idx] = 1;
                        isLightActivationneeded = true;
                    }
                }
            }

            if (isLightActivationneeded) {
                var cp = new CalcProfile(loc.Name + " - light", System.Guid.NewGuid().ToStrGuid(), lightprofile,  ProfileType.Relative,
                    "Synthetic for Light Device");

                // this function is for a light device so that the light is turned on, even if someone else was already in the room
                if (loc.LightDevices.Count > 0 && loc.LightDevices[0].LoadCount > 0 &&
                    !loc.LightDevices[0].IsBusyDuringTimespan(time, 1, 1, loc.LightDevices[0].Loads[0].LoadType)) {
                    for (var i = 0; i < loc.LightDevices.Count; i++) {
                        loc.LightDevices[i].SetAllLoadTypesToTimeprofile(cp, time, "Light", Name, 1);
                    }
                }
            }

            // log the light
            if ( _calcRepo.CalcParameters.IsSet(CalcOption.ThoughtsLogfile)) {
                if (isLightActivationneeded) {
                    if (//_lf == null ||
                        _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                        throw new LPGException("Logfile was null.");
                    }

                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(
                        new ThoughtEntry(this, time, "Turning on the light for " + loc.Name), _calcPerson.HouseholdKey);
                }
                else {
                    if (//_lf == null ||
                        _calcRepo.Logfile.ThoughtsLogFile1 == null) {
                        throw new LPGException("Logfile was null.");
                    }

                    _calcRepo.Logfile.ThoughtsLogFile1.WriteEntry(new ThoughtEntry(this, time, "No light needed for " + loc.Name),
                        _calcPerson.HouseholdKey);
                }
            }

            return personCalcProfile.StepValues.Count;
        }

        

        private class AffordanceStatusClass {
            public AffordanceStatusClass() => Reasons = new List<AffordanceStatusTuple>();

            [JetBrains.Annotations.NotNull]
            public List<AffordanceStatusTuple> Reasons { get; }
        }

        private class AffordanceStatusTuple {
            public AffordanceStatusTuple(ICalcAffordanceBase affordance, string reason)
            {
                Affordance = affordance;
                Reason = reason;
            }

            public ICalcAffordanceBase Affordance { get; }
            public string Reason { get; }
        }
        private class PotentialAffs {
            [JetBrains.Annotations.NotNull]
            [ItemNotNull]
            public List<ICalcAffordanceBase> PotentialAffordances { get; } = new List<ICalcAffordanceBase>();

            [JetBrains.Annotations.NotNull]
            [ItemNotNull]
            public List<ICalcAffordanceBase> PotentialAffordancesWithInterruptingSubAffordances { get; } =
                new List<ICalcAffordanceBase>();

            [JetBrains.Annotations.NotNull]
            [ItemNotNull]
            public List<ICalcAffordanceBase> PotentialAffordancesWithSubAffordances { get; } =
                new List<ICalcAffordanceBase>();

            [JetBrains.Annotations.NotNull]
            [ItemNotNull]
            public List<ICalcAffordanceBase> PotentialInterruptingAffordances { get; } =
                new List<ICalcAffordanceBase>();
        }

        

    }

    //public class State
    //    {
    //        public List<double> Values { get; set; }
    //        public TimeSpan Time { get; set; }

    //        // 构造函数
    //        public State(List<double> values, TimeSpan time)
    //        {
    //            Values = values;
    //            Time = time;
    //        }

    //        // 重写Equals和GetHashCode以便能够作为Dictionary的键
    //        public override bool Equals(object obj)
    //        {
    //            if (obj is State other)
    //            {
    //                return Values.SequenceEqual(other.Values) && Time == other.Time;
    //            }
    //            return false;
    //        }

    //        public override int GetHashCode()
    //        {
    //            unchecked // Overflow is fine, just wrap
    //            {
    //                int hash = 17;
    //                // Suitable nullity checks etc, of course :)
    //                foreach (var value in Values)
    //                {
    //                    hash = hash * 23 + value.GetHashCode();
    //                }
    //                hash = hash * 23 + Time.GetHashCode();
    //                return hash;
    //            }
    //        }
    //    }

    //public class HumanHeatGainManager {
    //    private readonly HumanHeatGainSpecification _hhgs;
    //    private readonly Dictionary<string, CalcDevice> _devices = new Dictionary<string, CalcDevice>();

    //    public HumanHeatGainManager(CalcPerson person, [JetBrains.Annotations.NotNull] List<CalcLocation> allLocations, [JetBrains.Annotations.NotNull] CalcRepo calcRepo)
    //    {
    //        _hhgs = calcRepo.HumanHeatGainSpecification;
    //        var sampleLoc = allLocations[0];
    //        foreach (BodilyActivityLevel activityLevel in Enum.GetValues(typeof(BodilyActivityLevel)))
    //        {
    //            //power load type split by location and activity level
    //            //register all possible combinations for the power
    //            foreach (var location in allLocations) {
    //            //register all possible combinations for the power
    //                var devicename = "Inner Heat Gain - " + person.Name + " - " + _hhgs.PowerLoadtype.Name + " - " + location.Name;
    //                CalcDeviceLoad cdl = new CalcDeviceLoad(_hhgs.PowerLoadtype.Name, 1, _hhgs.PowerLoadtype, 0, 0);
    //                var cdlList = new List<CalcDeviceLoad>();
    //                cdlList.Add(cdl);
    //                CalcDeviceDto cdd = new CalcDeviceDto(devicename, Guid.NewGuid().ToStrGuid(), person.HouseholdKey,
    //                    OefcDeviceType.HumanInnerGains, "Human Inner Gains", "", Guid.NewGuid().ToStrGuid(),
    //                    location.Guid,
    //                    location.Name);
    //                CalcDevice cd = new CalcDevice(cdlList, location, cdd, calcRepo);
    //                var key = MakePowerKey(person,  location, activityLevel);
    //                _devices.Add(key,cd);
    //            }
    //            //powercounts: one activity level per person for a single location, count as 0/1
    //            //register all possible combinations for the power
    //            var countDevicename = "Inner Heat Gain - " + person.Name + " - " + _hhgs.CountLoadtype.Name;
    //            CalcDeviceLoad countCdl = new CalcDeviceLoad(_hhgs.CountLoadtype.Name, 1, _hhgs.CountLoadtype, 0, 0);
    //            var countCdlList = new List<CalcDeviceLoad>();
    //            countCdlList.Add(countCdl);
    //            CalcDeviceDto countCdd = new CalcDeviceDto(countDevicename, Guid.NewGuid().ToStrGuid(), person.HouseholdKey,
    //                OefcDeviceType.HumanInnerGains, "Human Inner Gains", "", Guid.NewGuid().ToStrGuid(),
    //                sampleLoc.Guid,sampleLoc.Name);
    //            CalcDevice countCd = new CalcDevice(countCdlList, sampleLoc, countCdd, calcRepo);
    //            var ckey = MakeCountKey(person, activityLevel);
    //            _devices.Add(ckey, countCd);
    //        }
    //    }

    //    public double GetPowerForActivityLevel(BodilyActivityLevel bal)
    //    {
    //        switch (bal) {
    //            case BodilyActivityLevel.Unknown:
    //                return 0;
    //            case BodilyActivityLevel.Outside:
    //                return 0;
    //            case BodilyActivityLevel.Low:
    //                return 100;
    //            case BodilyActivityLevel.High:
    //                return 150;
    //            default:
    //                throw new ArgumentOutOfRangeException(nameof(bal), bal, null);
    //        }
    //    }

    //    public void Activate([JetBrains.Annotations.NotNull] CalcPerson person, BodilyActivityLevel level,  [JetBrains.Annotations.NotNull] CalcLocation loc, [JetBrains.Annotations.NotNull] TimeStep timeidx, [JetBrains.Annotations.NotNull] ICalcProfile personProfile,
    //                         [JetBrains.Annotations.NotNull] string affordanceName)
    //    {
    //        List<double> powerProfile = new List<double>();
    //        List<double> countProfile = new List<double>();
    //        //rectangle power profile
    //        double power = GetPowerForActivityLevel(level);
    //        foreach (var value in personProfile.StepValues)
    //        {
    //            if (value > 0)
    //            {
    //                powerProfile.Add(power);
    //                countProfile.Add(1);
    //            }
    //            else
    //            {
    //                powerProfile.Add(0);
    //                countProfile.Add(0);
    //            }
    //        }

    //        {
    //            var key = MakePowerKey(person, loc, level);
    //            var dev = _devices[key];

    //            CalcProfile cp = new CalcProfile("PersonProfile", personProfile.Guid, powerProfile,
    //                ProfileType.Absolute, "Inner Gains");
    //            dev.SetTimeprofile(cp, timeidx, _hhgs.PowerLoadtype, "", affordanceName, 1, true);
    //        }
    //        var countKey = MakeCountKey(person,  level);
    //        var dev2 = _devices[countKey];
    //        CalcProfile countCp = new CalcProfile("PersonProfile", personProfile.Guid, countProfile,
    //            ProfileType.Absolute, "Inner Gains Count");
    //        dev2.SetTimeprofile(countCp, timeidx, _hhgs.CountLoadtype, "", affordanceName, 1, true);

    //    }

    //    [JetBrains.Annotations.NotNull]
    //    public string MakePowerKey([JetBrains.Annotations.NotNull] CalcPerson person,  [JetBrains.Annotations.NotNull] CalcLocation location, BodilyActivityLevel level)
    //    {
    //        return person.HouseholdKey.Key + "#" + _hhgs.PowerLoadtype.Name + "#" + person.Name + "#" + location.Name + "#" +
    //               level.ToString();
    //    }

    //    [JetBrains.Annotations.NotNull]
    //    public string MakeCountKey([JetBrains.Annotations.NotNull] CalcPerson person, BodilyActivityLevel level)
    //    {
    //        return person.HouseholdKey.Key + "#" + person.Name + "#" + level.ToString();
    //    }
    //}

}

//public class State
//{
//    public List<double> Values { get; set; }
//    public TimeSpan Time { get; set; }

//    // 构造函数
//    public State(List<double> values, TimeSpan time)
//    {
//        Values = values;
//        Time = time;
//    }

//    // 重写Equals和GetHashCode以便能够作为Dictionary的键
//    public override bool Equals(object obj)
//    {
//        if (obj is State other)
//        {
//            return Values.SequenceEqual(other.Values) && Time == other.Time;
//        }
//        return false;
//    }

//    public override int GetHashCode()
//    {
//        unchecked // Overflow is fine, just wrap
//        {
//            int hash = 17;
//            // Suitable nullity checks etc, of course :)
//            foreach (var value in Values)
//            {
//                hash = hash * 23 + value.GetHashCode();
//            }
//            hash = hash * 23 + Time.GetHashCode();
//            return hash;
//        }
//    }


//}

public class CustomKeyComparer : IEqualityComparer<(Dictionary<string, int>, string)>
{
    public bool Equals((Dictionary<string, int>, string) x, (Dictionary<string, int>, string) y)
    {
        // 检查字符串部分是否相等
        if (!x.Item2.Equals(y.Item2))
        {
            return false;
        }

        // 检查字典部分的键值对数量是否相等
        if (x.Item1.Count != y.Item1.Count)
        {
            return false;
        }

        // 检查每个键是否存在于另一个字典中且对应的值相等
        foreach (var kvp in x.Item1)
        {
            if (!y.Item1.TryGetValue(kvp.Key, out var value) || kvp.Value != value)
            {
                return false;
            }
        }

        //Debug.WriteLine("Equal");
        return true;
        
    }

    public int GetHashCode((Dictionary<string, int>, string) obj)
    {
        // 计算哈希码
        int hash = 17;
        hash = hash * 23 + obj.Item2.GetHashCode();
        foreach (var kvp in obj.Item1.OrderBy(kvp => kvp.Key))
        {
            hash = hash * 23 + kvp.Key.GetHashCode();
            hash = hash * 23 + kvp.Value.GetHashCode();
        }
        return hash;
    }
}