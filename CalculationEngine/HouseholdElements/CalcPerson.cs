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
using System.Threading;
using System.Timers;
using System.Data.SqlTypes;

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

        
        public ICalcAffordanceBase _executingAffordance = null;

        public ConcurrentDictionary<(Dictionary<string,int> , string ), ConcurrentDictionary<string,(double,int,Dictionary<int,double>,double, (Dictionary<string, int>, string))>> qTable =  new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>,double, (Dictionary<string, int>, string))>>(new CustomKeyComparer());

        
        public (Dictionary<string, int>, string) currentState = (null, null);

        
        public int searchCounter = 0;

        public int foundCounter = 0;

        public int sumSearchCounter = 0;

        public int sumFoundCounter = 0;        
        
        public Dictionary<string, int> remainTimeOtherPerson = new Dictionary<string, int>();

        public Dictionary<DateTime, (string, string)> executedAffordance = new Dictionary<DateTime, (string, string)>();

        public bool isHumanInterventionInvolved = true;
        
        public double gamma = 0.8; //0.8

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
            bool otherPersonNotBusy = true; // other person's activity almost done
            if (isHumanInterventionInvolved)
            {
                otherPersonNotBusy = remainTimeOtherPerson.Values.Max() < 30;
            }

            if (_currentAffordance?.IsInterruptable == true && !_isCurrentlyPriorityAffordanceRunning && otherPersonNotBusy) {
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
                    var bestAffordance = GetBestAffordanceFromListNew_RL_Adapted_Q_Learning(time, availableInterruptingAffordances, now);
                    //Debug.WriteLine("Interrupting " + _currentAffordance + " with " + bestAffordance);
                    if (_debug_print)
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


            bestaff.Activate(currentTimeStep, Name,  CurrentLocation,
                out var personTimeProfile);

            //add to list of executed affordances
            executedAffordance[now] = (bestaff.Name, bestaff.AffCategory);
            
            int durationInMinutes = personTimeProfile.StepValues.Count;

            PersonDesires.ApplyAffordanceEffectNew(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name, durationInMinutes, true, currentTimeStep, now);
            _executingAffordance = bestaff;
            _remainingExecutionSteps = durationInMinutes - 1;
            _currentDuration = durationInMinutes;
            
            CurrentLocation = bestaff.ParentLocation;
            var duration = SetBusy(currentTimeStep, personTimeProfile, bestaff.ParentLocation, isDaylight,
                bestaff.NeedsLight);

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

            return GetBestAffordanceFromListNew_RL_Adapted_Q_Learning(time, allAffordances, now);
        }

        

        public void SaveQTableToFile()
        {
            string baseDir2 = @"C:\Work\ML\Models";

            var convertedQTable = new Dictionary<string, string>();

            foreach (var outerEntry in qTable)
            {
                var outerKeyDictSerialized = string.Join("±", outerEntry.Key.Item1.Select(d => $"{d.Key}⦿{d.Value.ToString()}"));
                var outerKey = $"{outerKeyDictSerialized}§{outerEntry.Key.Item2}";
                //var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                //    $"{innerEntry.Key}¶{innerEntry.Value.Item1}‖{innerEntry.Value.Item2}‖{String.Join("∥", innerEntry.Value.Item3.Select(d => $"{d.Key}⨁{d.Value}"))}‖{innerEntry.Value.Item4}"
                //);
                var innerDictSerialized = outerEntry.Value.Select(innerEntry =>
                        $"{innerEntry.Key}¶{innerEntry.Value.Item1}‖{innerEntry.Value.Item2}‖{String.Join("∥", innerEntry.Value.Item3.Select(d => $"{d.Key}⨁{d.Value}"))}‖{innerEntry.Value.Item4}‖{string.Join("¥", innerEntry.Value.Item5.Item1.Select(d => $"{d.Key}○{d.Value}"))}♯{innerEntry.Value.Item5.Item2}"
                    );
                convertedQTable[outerKey] = String.Join("★", innerDictSerialized);
            }

            if (!Directory.Exists(baseDir2))
            {
                Directory.CreateDirectory(baseDir2);
            }
            string personName = _calcPerson.Name.Replace("/", "_");
            //string filePath = Path.Combine(baseDir2, $"qTable-{personName}.json");
            string filePath = Path.Combine(baseDir2, $"qTable-{personName}.json");
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

 
        public void LoadQTableFromFile()
        {
            Debug.WriteLine("Now Loading QTable from file...");
            string baseDir = @"C:\Work\ML\Models";
            // 确保目录存在
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            // 处理文件名以避免路径问题
            string personName = _calcPerson.Name.Replace("/", "_");
            string filePath = Path.Combine(baseDir, $"qTable-{personName}.json");

            // 检查文件是否存在
            if (File.Exists(filePath))
            {
                try
                {
                    var jsonString = File.ReadAllText(filePath);
                    var convertedQTable = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);

                    var readed_QTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>, double, (Dictionary<string, int>, string))>>(new CustomKeyComparer());

                    foreach (var outerEntry in convertedQTable)
                    {
                        var outerKeyParts = outerEntry.Key.Split('§');
                        var outerKeyDictParts = outerKeyParts[0].Split('±').Select(p => p.Split('⦿')).ToDictionary(p => p[0], p => int.Parse(p[1]));
                        var outerKey = (outerKeyDictParts, outerKeyParts[1]);
                        var innerDict = new ConcurrentDictionary<string, (double, int, Dictionary<int, double>,double, (Dictionary<string, int>, string))>();

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
                            var r_value = double.Parse(valueParts[3]);

                            var newStateParts = valueParts[4].Split('♯');
                            var newStateDictParts = newStateParts[0].Split('¥').Select(p => p.Split('○')).ToDictionary(p => p[0], p => int.Parse(p[1]));
                            var newState = (newStateDictParts, newStateParts[1]);

                            innerDict[key] = (decimalValue, intValue, dict, r_value, newState);

                            //innerDict[key] = (decimalValue, intValue, dict, r_value);
                            
                        }

                        readed_QTable[outerKey] = innerDict;
                    }
                    this.qTable = readed_QTable;

                    Debug.WriteLine("QTable has been successfully loaded from " + filePath);
                    Logger.Info("QTable has been successfully loaded from " + filePath);


                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error loading QTable: " + ex.Message);
                    Logger.Info("Error loading QTable: " + ex.Message);
                    this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>, double, (Dictionary<string, int>, string))>>(new CustomKeyComparer());


                }
            }
            else
            {
                Debug.WriteLine("No saved QTable found. Initializing a new QTable.");
                Logger.Info("No saved QTable found. Initializing a new QTable.");
                this.qTable = new ConcurrentDictionary<(Dictionary<string, int>, string), ConcurrentDictionary<string, (double, int, Dictionary<int, double>, double, (Dictionary<string, int>, string))>>(new CustomKeyComparer());
            }
        }


        public Dictionary<string, int> MergeDictAndLevels(Dictionary<string, (int, double)> desireName_Value_Dict)
        {
            var mergedDict = new Dictionary<string, int>(); 

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
                    mergedDict[key] = desire_level; 
                }
                
            }

            return mergedDict; 
        }

        public string makeTimeSpan(DateTime time, int offset)
        {
            int unit = 15;
            var newTime = time.AddMinutes(offset);
            var rounded_minutes_new = newTime.Minute % unit;
            newTime = newTime.AddMinutes(-rounded_minutes_new);
            //TimeSpan newTimeState = new TimeSpan(newTime.Hour, newTime.Minute, 0);
            string prefix = newTime.DayOfWeek == DayOfWeek.Saturday || newTime.DayOfWeek == DayOfWeek.Sunday ? "R:" : "W:";
            //string prefix = newTime.DayOfWeek.ToString()+":";
            string newTimeState = prefix + newTime.ToString("HH:mm");
            //Debug.WriteLine("Time: "+ "  " + newTimeState);
            return newTimeState;
            //return "";
        }


         
        private ICalcAffordanceBase GetBestAffordanceFromListNew_RL_Adapted_Q_Learning([JetBrains.Annotations.NotNull] TimeStep time,
                                                      [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, DateTime now)
        {
            //If the QTable is empty, then load it from the file
            if (qTable.Count == 0)
            {
                LoadQTableFromFile();
            }

            this.RetrainBestActionsFromRandomStates(time.InternalStep);

            //Initilize the variables
            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;

            var desire_ValueBefore = PersonDesires.GetCurrentDesireValue();
            var desire_level_before = MergeDictAndLevels(desire_ValueBefore);
            this.currentState = new(desire_level_before, makeTimeSpan(now, 0));

            int currentSearchCounter = allAvailableAffordances.Count;
            int currentFoundCounter = 0;

            //Initilize the Lock object
            object locker = new object();

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
                double alpha = 0.2;
                
                bool existsInPastThreeHoursCurrent = false;
                DateTime threeHoursAgo = now.AddHours(-3);

                //initiliz the Search and Found Counter
                int affordanceSearchCounter = 0;
                int affordanceFoundCounter = 0;

                //Check is this affordance and his similar affordance already executed in the last 3 hours
                if (isHumanInterventionInvolved)
                {
                    existsInPastThreeHoursCurrent = executedAffordance
                       .Where(kvp => kvp.Key >= threeHoursAgo && kvp.Key <= now && kvp.Value.Item2 == affordance.AffCategory)
                       .Any();
                }


                //GetNextStateAndQ_R_Value(state, time,now)// input: State, time, now // output: Q, R, next state
                var firstStageQ_Learning_Info = Q_Learning_Stage1(affordance, currentState, time, now);
                var R_S_A = firstStageQ_Learning_Info.R_S_A;
                var Q_S_A = firstStageQ_Learning_Info.Q_S_A;
                var newState = firstStageQ_Learning_Info.newState;
                var weightSum = firstStageQ_Learning_Info.weightSum;
                var TimeAfter = firstStageQ_Learning_Info.TimeAfter;

                affordanceFoundCounter += Q_S_A.Item1 > 0 ? 1 : 0; //Update Counter, if this state is already visited, then increase the FoundCounter


                //Start prediction, variable initialization
                double prediction = R_S_A;
                (Dictionary<string, int>, string) nextState = newState; //new state
                DateTime nextTime = TimeAfter;

                affordanceSearchCounter++;// Update Counter

                //Get n-step prediction Infomation 

                DateTime endOfDay = now.Date.AddHours(23).AddMinutes(59);
                bool state_after_pass_day = TimeAfter > endOfDay;

                if (!state_after_pass_day)
                {
                    var max_Q_ns = Q_Learning_Stage2(nextState);
                    prediction += gamma * max_Q_ns;
                    affordanceFoundCounter += max_Q_ns > 0 ? 1 : 0;
                }
                
                // Update the Q value for the current state and action

                double new_Q_S_A = Q_S_A.Item1 == 0 ? R_S_A : (1 - alpha) * Q_S_A.Item1 + alpha * prediction;
                double new_R_S_A = Q_S_A.Item1 == 0 ? R_S_A :  (1 - alpha) * Q_S_A.Item4 + alpha * R_S_A;
                var QSA_Info = (new_Q_S_A, affordance.GetDuration(), affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value), R_S_A, firstStageQ_Learning_Info.newState);
                var currentStateData = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>, double, (Dictionary<string, int>, string))>());
                currentStateData.AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);

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

        private void RetrainBestActionsFromRandomStates(int seed)
        {
            if (qTable.Count == 0)
            {
                return;
            }
            int m = Math.Min(100,qTable.Count);

            // Hyperparameters
            double alpha = 0.2;
            //double gamma = 0.9;
            Random rand = new Random(seed);

            // Get all states from Q-Table
            var allStates = qTable.Keys.ToList();

            // Randomly select m states
            var randomStates = allStates.OrderBy(x => rand.Next()).Take(m).ToList();

            // Parallel loop to update Q-values for best actions in selected states
            Parallel.ForEach(randomStates, state =>
            {
                if (qTable.TryGetValue(state, out var current_State))
                {
                    //int numer_of_update_action = Math.Max(1, current_State.Count/4);
                    TimeSpan time_state = TimeSpan.Parse(state.Item2.Substring(2));
                    int numer_of_update_action = current_State.Count;
                    var topActions = current_State.OrderByDescending(action => action.Value.Item1).Take(numer_of_update_action).ToList();

                    foreach (var bestActionEntry in topActions)
                    {

                        if (bestActionEntry.Key == null)
                        {
                            continue;
                        }

                        var bestAction = bestActionEntry.Key;
                        var newStateInfo = bestActionEntry.Value.Item5;
                        TimeSpan time_newState = TimeSpan.Parse(newStateInfo.Item2.Substring(2));
                        var R_S_A = bestActionEntry.Value.Item4;
                        var Q_S_A = bestActionEntry.Value.Item1;
                        bool inTheSameDay = time_newState>=time_state;
                        
                        double prediction = R_S_A;
                        var newState = newStateInfo;

                        if (qTable.TryGetValue(newState, out var next_State))
                        {
                            if(inTheSameDay)
                            {
                                var next_Action_Entry = next_State.DefaultIfEmpty().MaxBy(action => action.Value.Item1);

                                if (next_Action_Entry.Key == null || next_Action_Entry.Value.Item1 == 0)
                                {
                                    continue;
                                }
                                prediction += gamma * next_Action_Entry.Value.Item1;
                            }                                                       
                        }

                        // Update the Q-value using the Bellman equation
                        double new_Q_S_A = (1 - alpha) * Q_S_A + alpha * prediction;
                        //
                        var QSA_Info = (new_Q_S_A, bestActionEntry.Value.Item2, bestActionEntry.Value.Item3, bestActionEntry.Value.Item4, bestActionEntry.Value.Item5);

                        current_State.AddOrUpdate(bestAction, QSA_Info, (key, oldValue) => QSA_Info);
                    }
                }
            });
        }

        public ((double, int, Dictionary<int, double>,double, (Dictionary<string, int>, string)) Q_S_A, double R_S_A, (Dictionary<string, int>, string) newState, double weightSum, DateTime TimeAfter) Q_Learning_Stage1(ICalcAffordanceBase affordance, (Dictionary<string, int>, string) currentState, TimeStep time, DateTime now)
        {
            int duration = time==null? affordance.GetDuration() : affordance.GetRealDuration(time);
            //int duration = affordance.GetRealDuration(time);
            bool isInterruptable = affordance.IsInterruptable;
            var satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);
            var calcTotalDeviationResult = PersonDesires.CalcEffectPartlyRL_New(null, time, true, out var thoughtstring, now, satValue: satisfactionvalues, newDuration: duration, interruptable: isInterruptable);

            var desireDiff = calcTotalDeviationResult.totalDeviation;
            var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
            var weightSum = calcTotalDeviationResult.WeightSum;

            string newTimeState = makeTimeSpan(now, duration);

            Dictionary<string, int> desire_level_after = MergeDictAndLevels(desire_ValueAfter);

            (Dictionary<string, int>, string) newState = (desire_level_after, newTimeState);
            var R_S_A = -desireDiff + 1000000;
            if (weightSum >= 1000)
            {
                R_S_A = 20000000;
            }

            var Q_S = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, Dictionary<int, double>,double, (Dictionary<string, int>, string))>());

            (double, int, Dictionary<int, double>,double, (Dictionary<string, int>, string)) Q_S_A;

            if (!Q_S.TryGetValue((affordance.Name), out Q_S_A))
            {
                Q_S_A.Item1 = 0; // Initialize to 0 if the action is not found
            }

            var TimeAfter = now.AddMinutes(duration);

            return (Q_S_A, R_S_A, newState,weightSum,TimeAfter);
        }


        public double Q_Learning_Stage2((Dictionary<string, int>, string) currentState)
        {
            double maxQ_Value = 0;

            if (qTable.TryGetValue(currentState, out var Q_newState_actions_nS))
            {
                if (Q_newState_actions_nS != null && Q_newState_actions_nS.Any())
                {
                    var maxAction = Q_newState_actions_nS
                    .MaxBy(action => action.Value.Item1); 

                    if (maxAction.Key != null)
                    {
                        maxQ_Value = maxAction.Value.Item1; 
                    }
                }
                
            }

            if (maxQ_Value == 0)
            {
                return 0;
            }
            else
            {
                return maxQ_Value;               
            }

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
            

            foreach (var calcAffordanceBase in srcList) {
                if (NewIsAvailableAffordanceNew(timeStep, calcAffordanceBase, errors, getOnlyRelevantDesires,
                    CurrentLocation.CalcSite, tryHarder)) {
                    resultingAff.Add(calcAffordanceBase);
                }
            }


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

}


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