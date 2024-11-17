﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Automation;
using Automation.ResultFiles;
using Common;
using Common.CalcDto;
using Common.Enums;
using Common.JSON;
using JetBrains.Annotations;

namespace CalculationEngine.HouseholdElements {
    [SuppressMessage("ReSharper", "ConvertToAutoProperty")]
    public class VacationAffordance : CalcAffordanceBase {
        //TODO: figure out if this is still needed
        public VacationAffordance( StrGuid guid,
                                  [ItemNotNull] [JetBrains.Annotations.NotNull] BitArray isBusy, [JetBrains.Annotations.NotNull] CalcRepo calcRepo, HouseholdKey key)
            : base(
                Constants.TakingAVacationString, new CalcLocation("Vacation", System.Guid.NewGuid().ToStrGuid()),
                new List<CalcDesire>(), 0, 99, PermittedGender.All, false,
                false, "Vacation", false, false, ActionAfterInterruption.GoBackToOld, 0, false,
                CalcAffordanceType.Vacation, guid, isBusy, BodilyActivityLevel.Outside, calcRepo, key)
        {
        }

        public override ColorRGB AffordanceColor { get; } = new ColorRGB(0,255,0);

        public override int DefaultPersonProfileLength => 0;

        [JetBrains.Annotations.NotNull]
        public override List<CalcAffordance.DeviceEnergyProfileTuple> Energyprofiles { get; } =
            new List<CalcAffordance.DeviceEnergyProfileTuple>();

        public override string SourceTrait { get; } = "Vacation";

        public override List<CalcSubAffordance> SubAffordances { get; } = new List<CalcSubAffordance>();

        [JetBrains.Annotations.NotNull]
        public override string TimeLimitName { get; } = "None";

        //private static VacationAffordance _vacationAffordance;
        /* public VacationAffordance Get()
        {
            if (_vacationAffordance == null) {
                _vacationAffordance = new VacationAffordance(_calcParameters);
            }
            return _vacationAffordance;
        }*/

        public override void Activate(TimeStep startTime, string activatorName,
                                      CalcLocation personSourceLocation,
                                      out ICalcProfile personTimeProfile) =>
            throw new LPGException("This function should never be called");

        [JetBrains.Annotations.NotNull]
        public override string AreDeviceProfilesEmpty() => throw new NotImplementedException();

        public override bool AreThereDuplicateEnergyProfiles() => false;

        public override int GetDuration()
        {
            return 10080;
        }

        public override int GetRealDuration(TimeStep now) => 10080;

        public override List<CalcSubAffordance> CollectSubAffordances(TimeStep time,
                                                                      bool onlyInterrupting,
                                                                      CalcLocation srcLocation) =>
            new List<CalcSubAffordance>();

        //public override ICalcProfile CollectPersonProfile() => throw new LPGException("This function should never be called");

        public override BusynessType IsBusy(TimeStep time,
                                    CalcLocation srcLocation, CalcPersonDto calcPerson,
                                    bool clearDictionaries = true) =>
            throw new LPGException("This function should never be called");
    }
}