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

        public Dictionary<State,Dictionary<string,double>> qTable = new Dictionary<State, Dictionary<string, double>>();
  
        public State currentState = null;


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
                    var bestAffordance = GetBestAffordanceFromListNewRL(time, availableInterruptingAffordances, true, now);
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

            //var dura = bestaff.GetDuration();
            //Debug.WriteLine("Activating " + bestaff.Name+" Time: "+dura);
            //Debug.WriteLine(bestaff.GetDuration);
            bestaff.Activate(currentTimeStep, Name,  CurrentLocation,
                out var personTimeProfile);
            
            //PersonDesires.ApplyAffordanceEffect(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name);
            int durationInMinutes = personTimeProfile.StepValues.Count;
            //int testDuration = 
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

            return GetBestAffordanceFromListNewRL(time,  allAffordances, true, now);
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

        //public void BuildQtable()
        //{

        //}

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

        public TimeSpan makeTimeSpan(DateTime time, int offset)
        {
            var newTime = time.AddMinutes(offset);
            var rounded_minutes_new = newTime.Minute - ((newTime.Minute % 15) * 15);
            newTime = newTime.AddMinutes(-rounded_minutes_new);
            TimeSpan newTimeState = new TimeSpan(newTime.Hour, newTime.Minute, 0);
            return newTimeState;
        }

        private ICalcAffordanceBase GetBestAffordanceFromListNewRL([JetBrains.Annotations.NotNull] TimeStep time,
                                                              [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, Boolean careForAll, DateTime now)
        {
            //var bestDiff = decimal.MaxValue;
            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            //var bestaffordances = new List<(ICalcAffordanceBase, double)>();
            //bestaffordances.Add(bestAffordance);
            //double bestWeightSum = -1;
            //var affordanceDetails = new Dictionary<string, Tuple<decimal, int, int, double>>();

            List<double> desire_level_before = null;

            foreach (var affordance in allAvailableAffordances)
            {
                var duration = affordance.GetDuration();

                var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL(affordance, time, careForAll, out var thoughtstring, now);
                var desireDiff = calcTotalDeviationResult.totalDeviation;
                var weightSum = calcTotalDeviationResult.WeightSum;
                var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
                var desire_ValueBefore = calcTotalDeviationResult.desireName_ValueBeforeApply_Dict;

                TimeSpan nowTimeState = makeTimeSpan(now, 0);

                TimeSpan newTimeState = makeTimeSpan(now, duration);

                List<double> desire_level_after = ToDesireLevels(desire_ValueAfter);
                
                double alpha = 0.2;

                double gamma = 0.8;

                //List<double> desire_value_before 
                if (desire_level_before == null)
                {
                    desire_level_before = ToDesireLevels(desire_ValueBefore);
                    this.currentState = new State(desire_level_before, nowTimeState);
                }

                State newState = new State(desire_level_after, newTimeState);

                //State testState1 = new State(desire_value_after, nowTimeState);

                //State testState2 = new State(desire_value_before, newTimeState);

                var R_S_A = desireDiff + 1000000;


                //if(qTable.TryGetValue(currentState, out var Q_S_test))
                //{
                //    Debug.WriteLine("Q_S_Found!!   " + "Q_S_test" + "  Date:" + now.Date);
                //}

                if (!qTable.TryGetValue(currentState, out var Q_S))
                {
                    // Initialize with an empty dictionary if the current state is not found
                    Q_S = new Dictionary<string, double>();
                    qTable[currentState] = Q_S;
                }

                //Debug.WriteLine("should false:  "+currentState.Equals(testState1)+ "should false:  "+currentState.Equals(testState2));

                double Q_S_A;
                // Check if the current state has the action, initialize Q_S_A accordingly
                //double Q_S_A_test;
                //if(Q_S.TryGetValue(affordance.Guid.ToString(), out Q_S_A_test))
                //{
                //    Debug.WriteLine("Q_S_A_Found!!   " + Q_S_A_test+ "  Date:"  +now.Date+"  Aff:  "+affordance.Name);
                //}


                if (!Q_S.TryGetValue(affordance.Guid.ToString(), out Q_S_A))
                {
                    Q_S_A = 0; // Initialize to 0 if the action is not found
                }

                double maxQ_nS_nA = 0;
                Dictionary<string, double> Q_newState_actions;
                if (qTable.TryGetValue(newState, out Q_newState_actions))
                {
                    // If found, iterate to find the max Q value among actions
                    foreach (var action in Q_newState_actions)
                    {
                        if (action.Value > maxQ_nS_nA)
                        {
                            maxQ_nS_nA = action.Value;
                        }
                    }
                }

                // Update the Q value for the current state and action
                var new_Q_S_A = (1 - alpha) * Q_S_A + alpha * ((double)R_S_A + gamma * maxQ_nS_nA);
                qTable[currentState][affordance.Guid.ToString()] = new_Q_S_A;

                if(new_Q_S_A > bestQ_S_A)
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

                    bestQ_S_A = new_Q_S_A;
                    bestAffordance = affordance;
                    //this.currentState = newState;

                }

                //if (desireDiff == 1000000000000000)
                //{
                //    continue;
                //}



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
            
            //bool foundWorkAsTeacher2 = resultingAff.Any(a => a.Name == "work as teacher");
            //bool foundVisit2 = resultingAff.Any(a => a.Name == "visit the theater");

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

    public class State
        {
            public List<double> Values { get; set; }
            public TimeSpan Time { get; set; }

            // 构造函数
            public State(List<double> values, TimeSpan time)
            {
                Values = values;
                Time = time;
            }

            // 重写Equals和GetHashCode以便能够作为Dictionary的键
            public override bool Equals(object obj)
            {
                if (obj is State other)
                {
                    return Values.SequenceEqual(other.Values) && Time == other.Time;
                }
                return false;
            }

            public override int GetHashCode()
            {
                unchecked // Overflow is fine, just wrap
                {
                    int hash = 17;
                    // Suitable nullity checks etc, of course :)
                    foreach (var value in Values)
                    {
                        hash = hash * 23 + value.GetHashCode();
                    }
                    hash = hash * 23 + Time.GetHashCode();
                    return hash;
                }
            }
        }

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

public class State
{
    public List<double> Values { get; set; }
    public TimeSpan Time { get; set; }

    // 构造函数
    public State(List<double> values, TimeSpan time)
    {
        Values = values;
        Time = time;
    }

    // 重写Equals和GetHashCode以便能够作为Dictionary的键
    public override bool Equals(object obj)
    {
        if (obj is State other)
        {
            return Values.SequenceEqual(other.Values) && Time == other.Time;
        }
        return false;
    }

    public override int GetHashCode()
    {
        unchecked // Overflow is fine, just wrap
        {
            int hash = 17;
            // Suitable nullity checks etc, of course :)
            foreach (var value in Values)
            {
                hash = hash * 23 + value.GetHashCode();
            }
            hash = hash * 23 + Time.GetHashCode();
            return hash;
        }
    }
}