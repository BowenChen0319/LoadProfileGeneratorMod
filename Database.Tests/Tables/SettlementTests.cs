﻿//-----------------------------------------------------------------------

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
using System.Collections.ObjectModel;
using System.Linq;
using Automation;
using Common;
using Common.Enums;
using Common.Tests;
using Database.Tables.Houses;
using Database.Tables.ModularHouseholds;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;


#endregion

namespace Database.Tests.Tables {

    public class SettlementTests : UnitTestBaseClass
    {
        [Fact]
        [Trait(UnitTestCategories.Category,UnitTestCategories.BasicTest)]
        public void LoadFromDatabaseTest()
        {
            using (var db = new DatabaseSetup(Utili.GetCurrentMethodAndClass()))
            {
                ObservableCollection<TraitTag> traitTags = db.LoadTraitTags();
                var affordances = db.LoadAffordances(out var timeprofiles, out _, out var deviceCategories,
                    out var devices, out _, out var loadtypes, out var timeLimits, out var deviceActions,
                    out var deviceActionGroups, out var locations, out var variables, out var dateBasedProfiles);
                var affordanceTaggingSets = db.LoadAffordanceTaggingSets(affordances, loadtypes);
                db.LoadTransportation(locations, out var transportationDeviceSets,
                    out var travelRouteSets, out var _,
                    out var _, loadtypes,
                    out var chargingStationSets, affordanceTaggingSets);
                db.LoadHouseholdsAndHouses(out var modularHouseholds,
                    out var houses, out _, traitTags, chargingStationSets,
                    travelRouteSets, transportationDeviceSets);
                var settlements = new ObservableCollection<Settlement>();
                var geoloc = db.LoadGeographicLocations(out _, timeLimits, dateBasedProfiles);
                var temperaturProfiles = db.LoadTemperatureProfiles();
                Settlement.LoadFromDatabase(settlements, db.ConnectionString, temperaturProfiles, geoloc,
                    modularHouseholds, houses, false);
                db.Cleanup();
            }
        }

        [Fact]
        [Trait(UnitTestCategories.Category,UnitTestCategories.BasicTest)]
        public void JsonCalcSpecTest()
        {
            using (var db = new DatabaseSetup(Utili.GetCurrentMethodAndClass()))
            {
                using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass()))
                {
                    Simulator sim = new Simulator(db.ConnectionString);
                    int idx = 0;
                    while (sim.Settlements[idx].Households.Any(x => x.CalcObjectType != CalcObjectType.House)) {
                        idx++;
                    }

                    Settlement sett = sim.Settlements[idx];

                    sett.WriteJsonCalculationSpecs(wd.WorkingDirectory, @"V:\Dropbox\LPGReleases\releases8.6.0\simulationengine.exe");
                }
                db.Cleanup();
            }
        }


        [Fact]
        [Trait(UnitTestCategories.Category,UnitTestCategories.BasicTest)]
        public void SaveToDatabaseTest()
        {
            using (var db = new DatabaseSetup(Utili.GetCurrentMethodAndClass()))
            {
                ObservableCollection<TraitTag> traitTags = db.LoadTraitTags();
                var affordances = db.LoadAffordances(out var timeprofiles, out _, out var deviceCategories,
                    out var devices, out _, out var loadtypes, out var timeLimits, out var deviceActions,
                    out var deviceActionGroups, out var locations, out var variables, out var dateBasedProfiles);
                var affordanceTaggingSets = db.LoadAffordanceTaggingSets(affordances, loadtypes);
                db.LoadTransportation(locations, out var transportationDeviceSets,
                    out var travelRouteSets, out var _,
                    out var _, loadtypes,
                    out var chargingStationSets, affordanceTaggingSets);
                db.LoadHouseholdsAndHouses(out var modularHouseholds,
                    out var houses, out _, traitTags,
                    chargingStationSets, travelRouteSets, transportationDeviceSets);
                var settlements = new ObservableCollection<Settlement>();
                var geoloc = db.LoadGeographicLocations(out _, timeLimits, dateBasedProfiles);
                var temperaturProfiles = db.LoadTemperatureProfiles();
                Settlement.LoadFromDatabase(settlements, db.ConnectionString, temperaturProfiles, geoloc,
                    modularHouseholds, houses, false);
                settlements.Clear();
                db.ClearTable(Settlement.TableName);
                db.ClearTable(SettlementHH.TableName);
                JsonCalcSpecification jcs = JsonCalcSpecification.MakeDefaultsForTesting();
                jcs.EnergyIntensityType = EnergyIntensityType.EnergySaving;
                var se = new Settlement("blub", null, "blub",
                     "fdasdf", "blub", "blub", "asdf", db.ConnectionString, geoloc[0], temperaturProfiles[0], "Testing",
                    CreationType.ManuallyCreated, Guid.NewGuid().ToStrGuid(), jcs);
                se.SaveToDB();

                se.AddHousehold(modularHouseholds[0], 10);
                Settlement.LoadFromDatabase(settlements, db.ConnectionString, temperaturProfiles, geoloc,
                    modularHouseholds, houses, false);
                (settlements.Count).Should().Be(1);
                (settlements[0].Households.Count).Should().Be(1);
                (geoloc[0]).Should().Be(settlements[0].GeographicLocation);
                (temperaturProfiles[0]).Should().Be(settlements[0].TemperatureProfile);
                (EnergyIntensityType.EnergySaving).Should().Be(settlements[0].EnergyIntensityType);
                db.Cleanup();
            }
        }

        public SettlementTests([JetBrains.Annotations.NotNull] ITestOutputHelper testOutputHelper) : base(testOutputHelper)
        {
        }
    }
}