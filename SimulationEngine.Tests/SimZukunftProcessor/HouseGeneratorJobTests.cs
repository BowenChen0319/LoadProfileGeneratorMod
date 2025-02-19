﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Automation;
using Automation.ResultFiles;
using Common;
using Common.SQLResultLogging;
using Common.SQLResultLogging.InputLoggers;
using Common.SQLResultLogging.Loggers;
using Common.Tests;
using Database;
using Database.Helpers;
using Database.Tests;
using FluentAssertions;
using Newtonsoft.Json;
using SimulationEngineLib.HouseJobProcessor;
using Xunit;
using Xunit.Abstractions;

namespace SimulationEngine.Tests.SimZukunftProcessor {
    [SuppressMessage("ReSharper", "RedundantNameQualifier")]
    public class HouseGeneratorJobTests : UnitTestBaseClass {
        public HouseGeneratorJobTests([JetBrains.Annotations.NotNull] ITestOutputHelper testOutputHelper) : base(testOutputHelper)
        {
        }


        private static void MakeAndCalculateHouseJob([JetBrains.Annotations.NotNull] HouseCreationAndCalculationJob houseJob,
                                                     [JetBrains.Annotations.NotNull] Simulator sim, [JetBrains.Annotations.NotNull] WorkingDir wd,
                                                     [JetBrains.Annotations.NotNull] DatabaseSetup db)
        {
//calcSpec
            houseJob.CalcSpec = new JsonCalcSpecification {
                DefaultForOutputFiles = OutputFileDefault.Reasonable,
                StartDate = new DateTime(2017, 1, 1),
                EndDate = new DateTime(2017, 1, 3),
                GeographicLocation = sim.GeographicLocations.FindFirstByName("Berlin", FindMode.Partial)?.GetJsonReference() ??
                                     throw new LPGException("No Berlin in the DB"),
                TemperatureProfile = sim.TemperatureProfiles[0].GetJsonReference(),
                OutputDirectory = wd.Combine("Results"),
                EnableTransportation = false

            };
            var dstDir = wd.Combine("profilegenerator.db3");
            File.Copy(db.FileName, dstDir, true);
            houseJob.PathToDatabase = dstDir;

            StartHouseJob(houseJob, wd, "undefined");
        }

        private static void StartHouseJob([JetBrains.Annotations.NotNull] HouseCreationAndCalculationJob houseJob,
                                          [JetBrains.Annotations.NotNull] WorkingDir wd, string fnSuffix)
        {
            string houseJobFile = wd.Combine("houseJob." + fnSuffix + ".json");
            if (File.Exists(houseJobFile)) {
                File.Delete(houseJobFile);
            }

            using (StreamWriter sw = new StreamWriter(houseJobFile)) {
                sw.WriteLine(JsonConvert.SerializeObject(houseJob, Formatting.Indented));
                sw.Close();
            }

            Logger.Info("======================================================");

            Logger.Info("======================================================");
            Logger.Info("starting house generation");
            Logger.Info("======================================================");
            HouseGenerator houseGenerator = new HouseGenerator();
            houseGenerator.ProcessSingleHouseJob(houseJobFile);
        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.ManualOnly)]
        public void HouseGeneratorTestForPrecreated()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                Simulator sim = new Simulator(db.ConnectionString);
                using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                    const string fn = @"C:\work\LPGUnitTest\HouseJob.Felseggstrasse 29.json";
                    string txt = File.ReadAllText(fn);
                    HouseCreationAndCalculationJob houseJob = JsonConvert.DeserializeObject<HouseCreationAndCalculationJob>(txt);
                    MakeAndCalculateHouseJob(houseJob, sim, wd, db);
                }
            }
        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void HouseGeneratorTestWithHouseholdSpec()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass());
            Simulator sim = new Simulator(db.ConnectionString);
            using WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass());
            //housedata
            HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(), "HT01", 10000, 1000, "HouseGeneratorJobHouse");
            var householdData = new HouseholdData(Guid.NewGuid().ToString(), "blub", sim.ChargingStationSets[0].GetJsonReference(),
                sim.TransportationDeviceSets[0].GetJsonReference(), sim.TravelRouteSets[0].GetJsonReference(), null,
                HouseholdDataSpecificationType.ByHouseholdName);
            houseData.Households.Add(householdData);
            householdData.HouseholdNameSpec = new HouseholdNameSpecification(sim.ModularHouseholds[0].GetJsonReference());
            HouseCreationAndCalculationJob houseJob =
                new HouseCreationAndCalculationJob("present", "2019", "trafokreis", HouseDefinitionType.HouseData);
            houseJob.House = houseData;

            MakeAndCalculateHouseJob(houseJob, sim, wd, db);
        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void HouseGeneratorTestWithPersonSpec()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                Simulator sim = new Simulator(db.ConnectionString);
                using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                    //housedata
                    HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(), "HT01", 10000, 1000, "HouseGeneratorJobHouse");
                    var householdData = new HouseholdData(Guid.NewGuid().ToString(), "blub", sim.ChargingStationSets[0].GetJsonReference(),
                        sim.TransportationDeviceSets[0].GetJsonReference(), sim.TravelRouteSets[0].GetJsonReference(), null,
                        HouseholdDataSpecificationType.ByPersons);
                    houseData.Households.Add(householdData);
                    var persons = new List<PersonData> {
                        new PersonData(30, Gender.Male, "name")
                    };
                    householdData.HouseholdDataPersonSpec = new HouseholdDataPersonSpecification(persons);
                    HouseCreationAndCalculationJob houseJob =
                        new HouseCreationAndCalculationJob("present", "2019", "trafokreis", HouseDefinitionType.HouseData);
                    houseJob.House = houseData;

                    MakeAndCalculateHouseJob(houseJob, sim, wd, db);
                }
            }
        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void HouseGeneratorTestWithPersonSpecAndTransport()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using (WorkingDir workingDir = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                    Simulator sim = new Simulator(db.ConnectionString);
                    WorkingDir wd = workingDir;

                    //housedata
                    HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(), "HT01", 10000, 1000, "HouseGeneratorJobHouse");
                    var chargingStationSet = sim.ChargingStationSets
                        .SafeFindByName("Charging At Home with 03.7 kW, output results to Car Electricity").GetJsonReference();
                    Logger.Info("Using charging station " + chargingStationSet);
                    var transportationDeviceSet = sim.TransportationDeviceSets[0].GetJsonReference();
                    var travelRouteSet = sim.TravelRouteSets[0].GetJsonReference();
                    var work = new TransportationDistanceModifier("Work", "Car", 0);
                    var entertainment = new TransportationDistanceModifier("Entertainment", "Car", 12000);
                    List<TransportationDistanceModifier> tdm = new List<TransportationDistanceModifier> {work, entertainment};
                    var householdData = new HouseholdData(Guid.NewGuid().ToString(), "blub", chargingStationSet, transportationDeviceSet,
                        travelRouteSet, tdm, HouseholdDataSpecificationType.ByPersons);
                    houseData.Households.Add(householdData);
                    var persons = new List<PersonData> {
                        new PersonData(30, Gender.Male, "name")
                    };
                    householdData.HouseholdDataPersonSpec = new HouseholdDataPersonSpecification(persons);
                    HouseCreationAndCalculationJob houseJob =
                        new HouseCreationAndCalculationJob("present", "2019", "trafokreis", HouseDefinitionType.HouseData);
                    houseJob.House = houseData;
                    MakeAndCalculateHouseJob(houseJob, sim, wd, db);
                }
            }
        }


        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void StrGuidSerialisationTest()
        {
            Logger.Get().StartCollectingAllMessages();
            using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                StrGuid myguid = StrGuid.New();

                var guidfile = wd.Combine("guid.json");
                using (StreamWriter sw = new StreamWriter(guidfile)) {
                    sw.WriteLine(JsonConvert.SerializeObject(myguid, Formatting.Indented));
                    sw.Close();
                }

                char[] charsToTrim = {'\n', ' '};
                string guidStr = File.ReadAllText(guidfile).Trim(charsToTrim);
                var newguid = JsonConvert.DeserializeObject<StrGuid>(guidStr);
                myguid.Should().BeEquivalentTo(newguid);
            }

        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void HouseJobDeserializeTest() {
            Logger.Get().StartCollectingAllMessages();
            using (WorkingDir workingDir = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                    Simulator sim = new Simulator(db.ConnectionString);
                    WorkingDir wd = workingDir;

                    //housedata
                    HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(), "HT01", 10000, 1000, "HouseGeneratorJobHouse");
                    var chargingStationSet = sim.ChargingStationSets
                        .SafeFindByName("Charging At Home with 03.7 kW, output results to Car Electricity").GetJsonReference();
                    Logger.Info("Using charging station " + chargingStationSet);
                    var transportationDeviceSet = sim.TransportationDeviceSets[0].GetJsonReference();
                    var travelRouteSet = sim.TravelRouteSets[0].GetJsonReference();
                    var work = new TransportationDistanceModifier("Work", "Car", 0);
                    var entertainment = new TransportationDistanceModifier("Entertainment", "Car", 12000);
                    List<TransportationDistanceModifier> tdm = new List<TransportationDistanceModifier> {work, entertainment};
                    var householdData = new HouseholdData(Guid.NewGuid().ToString(), "blub", chargingStationSet, transportationDeviceSet,
                        travelRouteSet, tdm, HouseholdDataSpecificationType.ByPersons);
                    houseData.Households.Add(householdData);
                    var persons = new List<PersonData> {
                        new PersonData(30, Gender.Male, "name")
                    };
                    householdData.HouseholdDataPersonSpec = new HouseholdDataPersonSpecification(persons);
                    HouseCreationAndCalculationJob houseJob =
                        new HouseCreationAndCalculationJob("present", "2019", "trafokreis", HouseDefinitionType.HouseData);
                    houseJob.House = houseData;
                    var housejobfile = wd.Combine("housejob.json");
                    using (StreamWriter sw = new StreamWriter(housejobfile)) {
                        sw.WriteLine(JsonConvert.SerializeObject(houseJob, Formatting.Indented));
                        sw.Close();
                    }
                    char[] charsToTrim = { '\n', ' ' };
                    string houseJobStr = File.ReadAllText(housejobfile).Trim(charsToTrim);
                    HouseCreationAndCalculationJob deserializedHouseJob = JsonConvert.DeserializeObject<HouseCreationAndCalculationJob>(houseJobStr);
                    deserializedHouseJob.Should().BeEquivalentTo(houseJob);
                }

            }
        }


    [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void HouseGeneratorTestWithTemplateSpec()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass());
            Simulator sim = new Simulator(db.ConnectionString);
            using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                //housedata
                HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(),
                    "HT01", 10000, 1000, "HouseGeneratorJobHouse");
                var householdData = new HouseholdData(Guid.NewGuid().ToString(),
                    "blub", sim.ChargingStationSets[0].GetJsonReference(),
                    sim.TransportationDeviceSets[0].GetJsonReference(),
                    sim.TravelRouteSets[0].GetJsonReference(),  null,
                    HouseholdDataSpecificationType.ByTemplateName);
                houseData.Households.Add(householdData);
                householdData.HouseholdTemplateSpec = new HouseholdTemplateSpecification("CHR01");
                HouseCreationAndCalculationJob houseJob =
                    new HouseCreationAndCalculationJob("present", "2019", "trafokreis",
                        HouseDefinitionType.HouseData);
                houseJob.House = houseData;

                MakeAndCalculateHouseJob(houseJob, sim, wd, db);
            }
        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.BasicTest)]
        public void HouseJobForHeatpump()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                Simulator sim = new Simulator(db.ConnectionString);
                using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                    File.Copy(db.FileName, wd.Combine("profilegenerator.db3"));
                    Directory.SetCurrentDirectory(wd.WorkingDirectory);
                    //housedata
                    HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(),
                        "HT01", 10000, 1000, "HouseGeneratorJobHouse");
                    HouseCreationAndCalculationJob houseJob = new HouseCreationAndCalculationJob(
                        "present", "2019", "trafokreis", HouseDefinitionType.HouseData);
                    houseJob.House = houseData;
                    var householdData = new HouseholdData(Guid.NewGuid().ToString(),
                        "blub",
                        sim.ChargingStationSets[0].GetJsonReference(),
                        sim.TransportationDeviceSets[0].GetJsonReference(),
                        sim.TravelRouteSets[0].GetJsonReference(),
                        null, HouseholdDataSpecificationType.ByPersons);
                    houseData.Households.Add(householdData);
                    var persons = new List<PersonData> {
                        new PersonData(30, Gender.Male, "name")
                    };
                    householdData.HouseholdDataPersonSpec = new HouseholdDataPersonSpecification(persons);
                    houseJob.CalcSpec = new JsonCalcSpecification {
                        DefaultForOutputFiles = OutputFileDefault.NoFiles,
                        StartDate = new DateTime(2017, 1, 1),
                        EndDate = new DateTime(2017, 1, 3),
                        GeographicLocation = sim.GeographicLocations.FindFirstByName("Berlin", FindMode.Partial)
                                                 ?.GetJsonReference() ??
                                             throw new LPGException("No Berlin in the DB"),
                        TemperatureProfile = sim.TemperatureProfiles[0].GetJsonReference(),
                        OutputDirectory = wd.Combine("Results"),
                        CalcOptions = new List<CalcOption> {
                            CalcOption.HouseSumProfilesFromDetailedDats, CalcOption.DeviceProfilesIndividualHouseholds,
                            CalcOption.EnergyStorageFile,
                            //CalcOption.EnergyCarpetPlot,
                            CalcOption.HouseholdContents
                        }
                    };
                    StartHouseJob(houseJob, wd, "xxx");
                }
            }
        }


        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.LongTest3)]
        public void HouseJobForHouseTypes()
        {
            //setup
            Logger.Get().StartCollectingAllMessages();
            using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                Simulator sim = new Simulator(db.ConnectionString);
                using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                    var count = 0;
                    foreach (var houseType in sim.HouseTypes.Items) {
                        count++;
                        if (count < 22) {
                            continue;
                        }

                        Logger.Info("================================================");
                        Logger.Info("================================================");
                        Logger.Info("================================================");
                        Logger.Info("Starting " + houseType.Name);
                        Logger.Info("================================================");
                        Logger.Info("================================================");
                        Logger.Info("================================================");
                        Logger.Get().StartCollectingAllMessages();
                        string htcode = houseType.Name.Substring(0, 4);
                        //housedata
                        const int targetheatdemand = 10000;
                        HouseData houseData = new HouseData(Guid.NewGuid().ToStrGuid(), htcode, targetheatdemand, 1000,
                            "HouseGeneratorJobHouse");
                        HouseCreationAndCalculationJob houseJob = new HouseCreationAndCalculationJob("present", "2019",
                            "trafokreis", HouseDefinitionType.HouseData);
                        houseJob.House = houseData;
                        houseJob.PathToDatabase = db.FileName;
                        houseJob.CalcSpec = new JsonCalcSpecification {
                            DefaultForOutputFiles = OutputFileDefault.Reasonable,
                            StartDate = new DateTime(2017, 1, 1),
                            EndDate = new DateTime(2017, 12, 31),
                            GeographicLocation = sim.GeographicLocations.FindFirstByName("Berlin", FindMode.Partial)
                                                     ?.GetJsonReference() ??
                                                 throw new LPGException("No Berlin in the DB"),
                            TemperatureProfile = sim.TemperatureProfiles[0].GetJsonReference(),
                            OutputDirectory = wd.Combine("Results." + htcode),
                            SkipExisting = false,

                            CalcOptions = new List<CalcOption> {
                                CalcOption.HouseSumProfilesFromDetailedDats, CalcOption.DeviceProfilesIndividualHouseholds,
                                CalcOption.EnergyStorageFile,
                                //CalcOption.EnergyCarpetPlot,
                                CalcOption.HouseholdContents, CalcOption.TotalsPerLoadtype
                            }
                        };
                        StartHouseJob(houseJob, wd, htcode);
                        SqlResultLoggingService srls = new SqlResultLoggingService(houseJob.CalcSpec.OutputDirectory);
                        HouseholdKeyLogger hhkslogger = new HouseholdKeyLogger(srls);
                        var hhks = hhkslogger.Load();
                        TotalsPerLoadtypeEntryLogger tel = new TotalsPerLoadtypeEntryLogger(srls);
                        foreach (var entry in hhks) {
                            if (entry.KeyType == HouseholdKeyType.General) {
                                continue;
                            }

                            var dbs = srls.LoadDatabases();
                            if(dbs.Count == 1) {
                                continue;
                            }

                            Logger.Info(entry.HHKey.ToString());
                            var res = tel.Read(entry.HHKey);
                            foreach (var totalsEntry in res) {
                                Logger.Info(totalsEntry.Loadtype + ": " + totalsEntry.Value);
                                if (totalsEntry.Loadtype.Name == "Space Heating") {
                                    if (Math.Abs(totalsEntry.Value - targetheatdemand) > 10) {
                                        throw new LPGException("Target heat demand didn't match for " + houseType.Name);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        [Trait(UnitTestCategories.Category, UnitTestCategories.ManualOnly)]
        public void RunSinglePredefinedJson()
        {
            Logger.Get().StartCollectingAllMessages();
            Logger.Threshold = Severity.Debug;
            const string srcfile = @"V:\Dropbox\LPGReleases\releases9.4.0\ExampleHouseJob-1.json";
            using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass())) {
                using (DatabaseSetup db = new DatabaseSetup(Utili.GetCurrentMethodAndClass())) {
                    FileInfo srcfi = new FileInfo(srcfile);
                    string targetfile = wd.Combine(srcfi.Name);
                    string targetdb = wd.Combine("profilegenerator.db3");
                    File.Copy(db.FileName, targetdb, true);
                    srcfi.CopyTo(targetfile, true);
                    Directory.SetCurrentDirectory(wd.WorkingDirectory);
                    HouseGenerator houseGenerator = new HouseGenerator();
                    houseGenerator.ProcessSingleHouseJob(targetfile);
                }
            }
        }
    }
}