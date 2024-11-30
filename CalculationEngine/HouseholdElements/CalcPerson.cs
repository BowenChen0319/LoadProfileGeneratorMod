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

namespace CalculationEngine.HouseholdElements
{

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

        public int remainExecutionSteps = 0;

        public int currentDuration = 0;

        public ICalcAffordanceBase executingAffordance = null;

        public bool _debug_print = false;        
        
        public QTable qTable =  new QTable();

        public int searchCounter = 0;

        public int foundCounter = 0;

        public int sumSearchCounter = 0;

        public int sumFoundCounter = 0;        
        
        public Dictionary<string, int> remainStepsFromOtherPerson = new Dictionary<string, int>();

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
        /// <summary>
        /// A linear version of the original NextStep method.
        /// Key differences from the original method are marked with "//NEW",
        /// indicating changes related to linear affordance effects and decay application.
        /// </summary>
        public void NextStep_Linear([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcLocation> locs, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
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
            //NEW
            if (executingAffordance != null)
            {
                PersonDesires.ApplyDecay_WithoutSome_Linear(time, executingAffordance.Satisfactionvalues);

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
                //NEW
                if (executingAffordance != null && remainExecutionSteps > 0)
                {
                    remainExecutionSteps--;
                    //here use ApplyAffordanceEffectPartly to get the correct affordance effect
                    PersonDesires.ApplyAffordanceEffect_Linear(executingAffordance.Satisfactionvalues, executingAffordance.RandomEffect, executingAffordance.Name, currentDuration, false, time, now);
                }
                InterruptIfNeeded_Linear(time, isDaylight, false,now);
                return;
                //NEW
            }

            if (IsOnVacation[time.InternalStep])
            {
                BeOnVacation(time);

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
            //NEW
            var bestaff = FindBestAffordance_RL(time,  persons,
                simulationSeed, now);
            if (_debug_print)
            {
                Debug.WriteLine("Time:   " + now + "  " + _calcPerson.Name + "    " + bestaff.Name);
            }
            //NEW

            ActivateAffordance_Linear(time, isDaylight,  bestaff, now);
            _isCurrentlyPriorityAffordanceRunning = false;

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

        /// <summary>
        /// A linear version of the InterruptIfNeeded method.
        /// Handles interruptions during ongoing activities by evaluating viable alternatives,
        /// based on current conditions like sickness, affordance interruptibility,
        /// and human intervention. Differences from the original method are marked with "//NEW",
        /// incorporating changes related to selecting the best affordance using 
        /// Adapted Q-Learning and linear activation processes.
        /// </summary>
        private void InterruptIfNeeded_Linear([JetBrains.Annotations.NotNull] TimeStep time, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
                                       bool ignoreAlreadyExecutedActivities, DateTime now)
        {
            bool otherPersonNotBusy = true; // other person's activity almost done
            if (isHumanInterventionInvolved)
            {
                otherPersonNotBusy = remainStepsFromOtherPerson.Values.Max() < 30;
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
                    NewGetAllViableAffordancesAndSubs(time, null, true, aff, ignoreAlreadyExecutedActivities);
                if (availableInterruptingAffordances.Count != 0) {
                    //NEW
                    var bestAffordance = GetBestAffordanceFromList_Adapted_Q_Learning_RL(time, availableInterruptingAffordances, now);
                    if (_debug_print)
                    {
                        Debug.WriteLine("Time:   " + now + "  " + _calcPerson.Name + "    " + bestAffordance.Name + "  !!! Interrupt  !!!   " + _currentAffordance.Name);
                    }
                    ActivateAffordance_Linear(time, isDaylight,  bestAffordance, now);
                    //NEW
                    
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

        private void ActivateAffordance_Linear([JetBrains.Annotations.NotNull] TimeStep currentTimeStep, [JetBrains.Annotations.NotNull] DayLightStatus isDaylight,
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
            //NEW
            PersonDesires.ApplyAffordanceEffect_Linear(bestaff.Satisfactionvalues, bestaff.RandomEffect, bestaff.Name, durationInMinutes, true, currentTimeStep, now);
            executingAffordance = bestaff;
            remainExecutionSteps = durationInMinutes - 1;
            currentDuration = durationInMinutes;
            //NEW
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


        /// <summary>
        /// A reinforcement learning (RL)-enhanced version of the original FindBestAffordance method.
        /// This method identifies the most suitable affordance for a person or household at a given time step,
        /// considering factors like sickness, available affordances, and simulation constraints.
        /// Key differences from the original version are marked with "//NEW", introducing the use of RL
        /// (specifically Adapted Q-Learning) for selecting the best affordance from the list of viable options.
        /// </summary>
        private ICalcAffordanceBase FindBestAffordance_RL([JetBrains.Annotations.NotNull] TimeStep time,
                                                       [JetBrains.Annotations.NotNull][ItemNotNull] List<CalcPerson> persons, int simulationSeed, DateTime now)
        {
            var allAffs = IsSick[time.InternalStep] ? _sicknessPotentialAffs : _normalPotentialAffs;

            if (_calcRepo.Rnd == null) {
                throw new LPGException("Random number generator was not initialized");
            }

            var allAffordances =
                NewGetAllViableAffordancesAndSubs(time, null, false,  allAffs, false);
            if(allAffordances.Count == 0 && (time.ExternalStep < 0 || _calcRepo.CalcParameters.IgnorePreviousActivitesWhenNeeded))
            {
                allAffordances =
                    NewGetAllViableAffordancesAndSubs(time, null,  false, allAffs, true);
            }
            allAffordances.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            //no affordances, so search again for the error messages
            if (allAffordances.Count == 0) {

                var status = new AffordanceStatusClass();
                NewGetAllViableAffordancesAndSubs(time, status,  false,  allAffs, false);
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
                    idleaff.IsBusy(time, CurrentLocation, _calcPerson);
                    //Logger.Info(s);
                    return idleaff;
                }
                throw new DataIntegrityException(s);
            }

            if (_calcRepo.Rnd == null) {
                throw new LPGException("Random number generator was not initialized");
            }

            //NEW
            return GetBestAffordanceFromList_Adapted_Q_Learning_RL(time, allAffordances, now);
            //NEW
        }


        /// <summary>
        /// Saves the Q-Table used in the reinforcement learning (RL) process to a JSON file.
        /// The method serializes the Q-Table into a structured format suitable for storage
        /// and debugging, ensuring all necessary data for RL is preserved.
        /// If the target directory does not exist, it is created.
        /// Handles any exceptions that may occur during the file write process.
        /// </summary>
        public void SaveQTableToFile_RL()
        {
            this.qTable.SaveQTableToFile_RL(_calcPerson.Name);
        }


        /// <summary>
        /// Loads the Q-Table for the reinforcement learning (RL) process from a JSON file.
        /// The method deserializes the stored Q-Table into the required in-memory structure,
        /// initializing a new Q-Table if the file does not exist or an error occurs during loading.
        /// Ensures compatibility with the serialized format and handles exceptions gracefully
        /// to prevent disruptions in the RL process.
        /// </summary>
        public void LoadQTableFromFile_RL()
        {
           this.qTable.LoadQTableFromFile_RL(_calcPerson.Name);
        }


        /// <summary>
        /// Merges a dictionary of desires with their weights and values into a simplified dictionary for RL.
        /// Each desire's level is calculated based on its weight and value, with higher weights increasing the granularity of levels.
        /// Only desires with a weight of 20 or higher are included in the resulting dictionary.
        /// This method is designed to support reinforcement learning by simplifying the representation of desire states.
        /// </summary>
        public Dictionary<string, int> MergeDictAndLevels_RL(Dictionary<string, (int, double)> desireName_Value_Dict)
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

                if(desire_weight >= 20)//20
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
        public string MakeTimeSpan_RL(DateTime time, int offset)
        {
            int unit = 15;
            var newTime = time.AddMinutes(offset);
            var rounded_minutes_new = newTime.Minute % unit;
            newTime = newTime.AddMinutes(-rounded_minutes_new);
            string prefix = newTime.DayOfWeek == DayOfWeek.Saturday || newTime.DayOfWeek == DayOfWeek.Sunday ? "R:" : "W:";
            string newTimeState = prefix + newTime.ToString("HH:mm");
            return newTimeState;
        }


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

        private ICalcAffordanceBase GetBestAffordanceFromList_Adapted_Q_Learning_RL([JetBrains.Annotations.NotNull] TimeStep time,
                                                      [JetBrains.Annotations.NotNull][ItemNotNull] List<ICalcAffordanceBase> allAvailableAffordances, DateTime now)
        {
            //If the QTable is empty, then load it from the file
            if (qTable.Table.IsEmpty)
            {
                LoadQTableFromFile_RL();
            }

            //Experience Replay for better Update Rate
            Q_Learning_Experience_Replay_RL(time.InternalStep);

            //Initilize the variables
            var bestQ_S_A = double.MinValue;
            var bestAffordance = allAvailableAffordances[0];
            ICalcAffordanceBase sleep = null;

            var desire_ValueBefore = PersonDesires.GetCurrentDesireValue_Linear();
            var desire_level_before = MergeDictAndLevels_RL(desire_ValueBefore);
            (Dictionary<string, int>, string) currentState = (desire_level_before, MakeTimeSpan_RL(now, 0));

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
                var firstStageQ_Learning_Info = Q_Learning_Stage1_RL(affordance, currentState, time, now);
                var R_S_A = firstStageQ_Learning_Info.R_S_A;
                var Q_S_A = firstStageQ_Learning_Info.Q_S_A;
                var newState = firstStageQ_Learning_Info.newState;
                var weightSum = firstStageQ_Learning_Info.weightSum;
                var TimeAfter = firstStageQ_Learning_Info.TimeAfter;

                affordanceFoundCounter += Q_S_A.Item1 > 0 ? 1 : 0; //Update Counter, if this state is already visited, then increase the FoundCounter


                //Start prediction, variable initialization
                double prediction = R_S_A;
                //(Dictionary<string, int>, string) nextState = newState; //new state
                StateInfo nextState = new StateInfo(newState.Item1, newState.Item2);
                DateTime nextTime = TimeAfter;

                affordanceSearchCounter++;// Update Counter

                //Get next-step prediction Infomation 

                DateTime endOfDay = now.Date.AddHours(23).AddMinutes(59);
                bool state_after_pass_day = TimeAfter > endOfDay;

                if (!state_after_pass_day)
                {
                    var max_Q_ns = Q_Learning_Stage2_RL(nextState);
                    prediction += gamma * max_Q_ns;
                    affordanceFoundCounter += max_Q_ns > 0 ? 1 : 0;
                }
                
                // Update the Q value for the current state and action

                double new_Q_S_A = Q_S_A.Item1 == 0 ? R_S_A : (1 - alpha) * Q_S_A.Item1 + alpha * prediction;
                double new_R_S_A = Q_S_A.Item1 == 0 ? R_S_A :  (1 - alpha) * Q_S_A.Item3 + alpha * R_S_A;
                //var QSA_Info = (new_Q_S_A, affordance.GetDuration(), R_S_A, firstStageQ_Learning_Info.newState);
                StateInfo stateToAdd = new StateInfo(currentState.Item1, currentState.Item2);
                StateInfo stateToAddNext = new StateInfo(newState.Item1, newState.Item2);
                ActionInfo actionInfo = new ActionInfo(new_Q_S_A, affordance.GetDuration(), R_S_A, stateToAddNext);
                qTable.AddOrUpdate(stateToAdd, affordance.Name, actionInfo);
                //var currentStateData = qTable.GetOrAdd(currentState, new ConcurrentDictionary<string, (double, int, double, (Dictionary<string, int>, string))>());
                //currentStateData.AddOrUpdate(affordance.Name, QSA_Info, (key, oldValue) => QSA_Info);

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

        private void Q_Learning_Experience_Replay_RL(int seed)
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
                        var newStateInfo = bestActionEntry.Value.nextState;
                        TimeSpan time_newState = TimeSpan.Parse(newStateInfo.TimeOfDay.Substring(2));
                        var R_S_A = bestActionEntry.Value.RValue;
                        var Q_S_A = bestActionEntry.Value.QValue;
                        bool inTheSameDay = time_newState >= time_state;

                        double prediction = R_S_A;

                        // 使用 QTable 获取新状态的动作字典
                        if (qTable.Table.TryGetValue(newStateInfo, out var nextStateActions))
                        {
                            if (inTheSameDay)
                            {
                                // 获取下一状态中 Q-value 最大的动作
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

                        // 使用 Bellman 方程更新 Q-value
                        double new_Q_S_A = (1 - alpha) * Q_S_A + alpha * prediction;

                        // 更新当前状态的动作信息
                        var updatedActionInfo = new ActionInfo(new_Q_S_A, bestActionEntry.Value.weightSum, R_S_A, newStateInfo);

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
        public ((double, int,double, (Dictionary<string, int>, string)) Q_S_A, double R_S_A, (Dictionary<string, int>, string) newState, double weightSum, DateTime TimeAfter) Q_Learning_Stage1_RL(ICalcAffordanceBase affordance, (Dictionary<string, int>, string) currentState, TimeStep time, DateTime now)
        {
            int duration = time==null? affordance.GetDuration() : affordance.GetRealDuration(time);
            //int duration = affordance.GetRealDuration(time);
            bool isInterruptable = affordance.IsInterruptable;
            var satisfactionvalues = affordance.Satisfactionvalues.ToDictionary(s => s.DesireID, s => (double)s.Value);
            var calcTotalDeviationResult = PersonDesires.CalcEffect_Partly_Linear(duration, out var thoughtstring, satValue: satisfactionvalues, interruptable: isInterruptable);

            var desireDiff = calcTotalDeviationResult.totalDeviation;
            var desire_ValueAfter = calcTotalDeviationResult.desireName_ValueAfterApply_Dict;
            var weightSum = calcTotalDeviationResult.WeightSum;

            string newTimeState = MakeTimeSpan_RL(now, duration);

            Dictionary<string, int> desire_level_after = MergeDictAndLevels_RL(desire_ValueAfter);

            (Dictionary<string, int>, string) newState = (desire_level_after, newTimeState);
            var R_S_A = -desireDiff + 1000000;
            if (weightSum >= 1000)
            {
                R_S_A = 20000000;
            }

            // 将 (Dictionary<string, int>, string) 转为 PersonDesireState
            var currentPersonDesireState = new StateInfo(currentState.Item1, currentState.Item2);

            // 使用 GetOrAdd 方法获取或添加当前状态和动作信息
            var actionDetails = qTable.GetOrAdd(
                currentPersonDesireState,
                affordance.Name,
                new ActionInfo(0, 0, 0, currentPersonDesireState) // 默认值：QValue=0, weightSum=0, RValue=0, nextState=currentPersonDesireState
            );

            // 转换 actionInfo 为 (double, int, double, (Dictionary<string, int>, string)) 格式
            var Q_S_A = (
                actionDetails.QValue,
                actionDetails.weightSum,
                actionDetails.RValue,
                (actionDetails.nextState.DesireStates, actionDetails.nextState.TimeOfDay)
            );

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

        public double Q_Learning_Stage2_RL(StateInfo currentPersonDesireState)
        {
            double maxQ_Value = 0;

            // 从 QTable 获取当前状态的动作字典
            if (qTable.Table.TryGetValue(currentPersonDesireState, out var Q_newState_actions_nS))
            {
                if (Q_newState_actions_nS != null && Q_newState_actions_nS.Any())
                {
                    // 查找 Q-value 最大的动作
                    var maxAction = Q_newState_actions_nS.MaxBy(action => action.Value.QValue);

                    if (maxAction.Key != null)
                    {
                        maxQ_Value = maxAction.Value.QValue;
                    }
                }
            }

            return maxQ_Value;
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
