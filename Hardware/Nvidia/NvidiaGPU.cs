﻿/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2015 Michael Möller <mmoeller@openhardwaremonitor.org>
	Copyright (C) 2011 Christian Vallières
 
*/

using System;
using System.Globalization;
using System.Text;

namespace OpenHardwareMonitor.Hardware.Nvidia
{
    internal class NvidiaGPU : Hardware
    {
        private readonly int adapterIndex;
        private readonly Sensor[] clocks;
        private readonly Sensor control;
        private readonly NvDisplayHandle? displayHandle;
        private readonly Sensor fan;
        private readonly Control fanControl;
        private readonly NvPhysicalGpuHandle handle;
        private readonly Sensor[] loads;
        private readonly Sensor memoryAvail;
        private readonly Sensor memoryFree;
        private readonly Sensor memoryLoad;
        private readonly Sensor memoryUsed;

        private readonly Sensor[] temperatures;

        public NvidiaGPU(int adapterIndex, NvPhysicalGpuHandle handle,
            NvDisplayHandle? displayHandle, ISettings settings)
            : base(GetName(handle), new Identifier("nvidiagpu",
                adapterIndex.ToString(CultureInfo.InvariantCulture)))
        {
            this.adapterIndex = adapterIndex;
            this.handle = handle;
            this.displayHandle = displayHandle;

            var thermalSettings = GetThermalSettings();
            temperatures = new Sensor[thermalSettings.Count];
            for (var i = 0; i < temperatures.Length; i++)
            {
                var sensor = thermalSettings.Sensor[i];
                string name;
                switch (sensor.Target)
                {
                    case NvThermalTarget.BOARD:
                        name = "GPU Board";
                        break;
                    case NvThermalTarget.GPU:
                        name = "GPU Core";
                        break;
                    case NvThermalTarget.MEMORY:
                        name = "GPU Memory";
                        break;
                    case NvThermalTarget.POWER_SUPPLY:
                        name = "GPU Power Supply";
                        break;
                    case NvThermalTarget.UNKNOWN:
                        name = "GPU Unknown";
                        break;
                    default:
                        name = "GPU";
                        break;
                }

                temperatures[i] = new Sensor(name, i, SensorType.Temperature, this,
                    new ParameterDescription[0]);
                ActivateSensor(temperatures[i]);
            }

            int value;
            if (NVAPI.NvAPI_GPU_GetTachReading != null &&
                NVAPI.NvAPI_GPU_GetTachReading(handle, out value) == NvStatus.OK)
                if (value >= 0)
                {
                    fan = new Sensor("GPU", 0, SensorType.Fan, this);
                    ActivateSensor(fan);
                }

            clocks = new Sensor[3];
            clocks[0] = new Sensor("GPU Core", 0, SensorType.Clock, this);
            clocks[1] = new Sensor("GPU Memory", 1, SensorType.Clock, this);
            clocks[2] = new Sensor("GPU Shader", 2, SensorType.Clock, this);
            for (var i = 0; i < clocks.Length; i++)
                ActivateSensor(clocks[i]);

            loads = new Sensor[3];
            loads[0] = new Sensor("GPU Core", 0, SensorType.Load, this);
            loads[1] = new Sensor("GPU Memory Controller", 1, SensorType.Load, this);
            loads[2] = new Sensor("GPU Video Engine", 2, SensorType.Load, this);
            memoryLoad = new Sensor("GPU Memory", 3, SensorType.Load, this);
            memoryFree = new Sensor("GPU Memory Free", 1, SensorType.SmallData, this);
            memoryUsed = new Sensor("GPU Memory Used", 2, SensorType.SmallData, this);
            memoryAvail = new Sensor("GPU Memory Total", 3, SensorType.SmallData, this);
            control = new Sensor("GPU Fan", 0, SensorType.Control, this);

            var coolerSettings = GetCoolerSettings();
            if (coolerSettings.Count > 0)
            {
                fanControl = new Control(control, settings,
                    coolerSettings.Cooler[0].DefaultMin,
                    coolerSettings.Cooler[0].DefaultMax);
                fanControl.ControlModeChanged += ControlModeChanged;
                fanControl.SoftwareControlValueChanged += SoftwareControlValueChanged;
                ControlModeChanged(fanControl);
                control.Control = fanControl;
            }

            Update();
        }

        public override HardwareType HardwareType => HardwareType.GpuNvidia;

        private static string GetName(NvPhysicalGpuHandle handle)
        {
            string gpuName;
            if (NVAPI.NvAPI_GPU_GetFullName(handle, out gpuName) == NvStatus.OK)
                return "NVIDIA " + gpuName.Trim();
            return "NVIDIA";
        }

        private NvGPUThermalSettings GetThermalSettings()
        {
            var settings = new NvGPUThermalSettings();
            settings.Version = NVAPI.GPU_THERMAL_SETTINGS_VER;
            settings.Count = NVAPI.MAX_THERMAL_SENSORS_PER_GPU;
            settings.Sensor = new NvSensor[NVAPI.MAX_THERMAL_SENSORS_PER_GPU];
            if (!(NVAPI.NvAPI_GPU_GetThermalSettings != null &&
                  NVAPI.NvAPI_GPU_GetThermalSettings(handle, (int) NvThermalTarget.ALL,
                      ref settings) == NvStatus.OK))
                settings.Count = 0;
            return settings;
        }

        private NvGPUCoolerSettings GetCoolerSettings()
        {
            var settings = new NvGPUCoolerSettings();
            settings.Version = NVAPI.GPU_COOLER_SETTINGS_VER;
            settings.Cooler = new NvCooler[NVAPI.MAX_COOLER_PER_GPU];
            if (!(NVAPI.NvAPI_GPU_GetCoolerSettings != null &&
                  NVAPI.NvAPI_GPU_GetCoolerSettings(handle, 0,
                      ref settings) == NvStatus.OK))
                settings.Count = 0;
            return settings;
        }

        private uint[] GetClocks()
        {
            var allClocks = new NvClocks();
            allClocks.Version = NVAPI.GPU_CLOCKS_VER;
            allClocks.Clock = new uint[NVAPI.MAX_CLOCKS_PER_GPU];
            if (NVAPI.NvAPI_GPU_GetAllClocks != null &&
                NVAPI.NvAPI_GPU_GetAllClocks(handle, ref allClocks) == NvStatus.OK)
                return allClocks.Clock;
            return null;
        }

        public override void Update()
        {
            var settings = GetThermalSettings();
            foreach (var sensor in temperatures)
                sensor.Value = settings.Sensor[sensor.Index].CurrentTemp;

            if (fan != null)
            {
                var value = 0;
                NVAPI.NvAPI_GPU_GetTachReading(handle, out value);
                fan.Value = value;
            }

            var values = GetClocks();
            if (values != null)
            {
                clocks[1].Value = 0.001f * values[8];
                if (values[30] != 0)
                {
                    clocks[0].Value = 0.0005f * values[30];
                    clocks[2].Value = 0.001f * values[30];
                }
                else
                {
                    clocks[0].Value = 0.001f * values[0];
                    clocks[2].Value = 0.001f * values[14];
                }
            }

            var states = new NvPStates();
            states.Version = NVAPI.GPU_PSTATES_VER;
            states.PStates = new NvPState[NVAPI.MAX_PSTATES_PER_GPU];
            if (NVAPI.NvAPI_GPU_GetPStates != null &&
                NVAPI.NvAPI_GPU_GetPStates(handle, ref states) == NvStatus.OK)
            {
                for (var i = 0; i < 3; i++)
                    if (states.PStates[i].Present)
                    {
                        loads[i].Value = states.PStates[i].Percentage;
                        ActivateSensor(loads[i]);
                    }
            }
            else
            {
                var usages = new NvUsages();
                usages.Version = NVAPI.GPU_USAGES_VER;
                usages.Usage = new uint[NVAPI.MAX_USAGES_PER_GPU];
                if (NVAPI.NvAPI_GPU_GetUsages != null &&
                    NVAPI.NvAPI_GPU_GetUsages(handle, ref usages) == NvStatus.OK)
                {
                    loads[0].Value = usages.Usage[2];
                    loads[1].Value = usages.Usage[6];
                    loads[2].Value = usages.Usage[10];
                    for (var i = 0; i < 3; i++)
                        ActivateSensor(loads[i]);
                }
            }


            var coolerSettings = GetCoolerSettings();
            if (coolerSettings.Count > 0)
            {
                control.Value = coolerSettings.Cooler[0].CurrentLevel;
                ActivateSensor(control);
            }

            var memoryInfo = new NvMemoryInfo();
            memoryInfo.Version = NVAPI.GPU_MEMORY_INFO_VER;
            memoryInfo.Values = new uint[NVAPI.MAX_MEMORY_VALUES_PER_GPU];
            if (NVAPI.NvAPI_GPU_GetMemoryInfo != null && displayHandle.HasValue &&
                NVAPI.NvAPI_GPU_GetMemoryInfo(displayHandle.Value, ref memoryInfo) ==
                NvStatus.OK)
            {
                var totalMemory = memoryInfo.Values[0];
                var freeMemory = memoryInfo.Values[4];
                float usedMemory = Math.Max(totalMemory - freeMemory, 0);
                memoryFree.Value = (float) freeMemory / 1024;
                memoryAvail.Value = (float) totalMemory / 1024;
                memoryUsed.Value = usedMemory / 1024;
                memoryLoad.Value = 100f * usedMemory / totalMemory;
                ActivateSensor(memoryAvail);
                ActivateSensor(memoryUsed);
                ActivateSensor(memoryFree);
                ActivateSensor(memoryLoad);
            }
        }

        public override string GetReport()
        {
            var r = new StringBuilder();

            r.AppendLine("Nvidia GPU");
            r.AppendLine();

            r.AppendFormat("Name: {0}{1}", Name, Environment.NewLine);
            r.AppendFormat("Index: {0}{1}", adapterIndex, Environment.NewLine);

            if (displayHandle.HasValue && NVAPI.NvAPI_GetDisplayDriverVersion != null)
            {
                var driverVersion = new NvDisplayDriverVersion();
                driverVersion.Version = NVAPI.DISPLAY_DRIVER_VERSION_VER;
                if (NVAPI.NvAPI_GetDisplayDriverVersion(displayHandle.Value,
                        ref driverVersion) == NvStatus.OK)
                {
                    r.Append("Driver Version: ");
                    r.Append(driverVersion.DriverVersion / 100);
                    r.Append(".");
                    r.Append((driverVersion.DriverVersion % 100).ToString("00",
                        CultureInfo.InvariantCulture));
                    r.AppendLine();
                    r.Append("Driver Branch: ");
                    r.AppendLine(driverVersion.BuildBranch);
                }
            }

            r.AppendLine();

            if (NVAPI.NvAPI_GPU_GetPCIIdentifiers != null)
            {
                uint deviceId, subSystemId, revisionId, extDeviceId;

                var status = NVAPI.NvAPI_GPU_GetPCIIdentifiers(handle,
                    out deviceId, out subSystemId, out revisionId, out extDeviceId);

                if (status == NvStatus.OK)
                {
                    r.Append("DeviceID: 0x");
                    r.AppendLine(deviceId.ToString("X", CultureInfo.InvariantCulture));
                    r.Append("SubSystemID: 0x");
                    r.AppendLine(subSystemId.ToString("X", CultureInfo.InvariantCulture));
                    r.Append("RevisionID: 0x");
                    r.AppendLine(revisionId.ToString("X", CultureInfo.InvariantCulture));
                    r.Append("ExtDeviceID: 0x");
                    r.AppendLine(extDeviceId.ToString("X", CultureInfo.InvariantCulture));
                    r.AppendLine();
                }
            }

            if (NVAPI.NvAPI_GPU_GetThermalSettings != null)
            {
                var settings = new NvGPUThermalSettings();
                settings.Version = NVAPI.GPU_THERMAL_SETTINGS_VER;
                settings.Count = NVAPI.MAX_THERMAL_SENSORS_PER_GPU;
                settings.Sensor = new NvSensor[NVAPI.MAX_THERMAL_SENSORS_PER_GPU];

                var status = NVAPI.NvAPI_GPU_GetThermalSettings(handle,
                    (int) NvThermalTarget.ALL, ref settings);

                r.AppendLine("Thermal Settings");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    for (var i = 0; i < settings.Count; i++)
                    {
                        r.AppendFormat(" Sensor[{0}].Controller: {1}{2}", i,
                            settings.Sensor[i].Controller, Environment.NewLine);
                        r.AppendFormat(" Sensor[{0}].DefaultMinTemp: {1}{2}", i,
                            settings.Sensor[i].DefaultMinTemp, Environment.NewLine);
                        r.AppendFormat(" Sensor[{0}].DefaultMaxTemp: {1}{2}", i,
                            settings.Sensor[i].DefaultMaxTemp, Environment.NewLine);
                        r.AppendFormat(" Sensor[{0}].CurrentTemp: {1}{2}", i,
                            settings.Sensor[i].CurrentTemp, Environment.NewLine);
                        r.AppendFormat(" Sensor[{0}].Target: {1}{2}", i,
                            settings.Sensor[i].Target, Environment.NewLine);
                    }
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            if (NVAPI.NvAPI_GPU_GetAllClocks != null)
            {
                var allClocks = new NvClocks();
                allClocks.Version = NVAPI.GPU_CLOCKS_VER;
                allClocks.Clock = new uint[NVAPI.MAX_CLOCKS_PER_GPU];
                var status = NVAPI.NvAPI_GPU_GetAllClocks(handle, ref allClocks);

                r.AppendLine("Clocks");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    for (var i = 0; i < allClocks.Clock.Length; i++)
                        if (allClocks.Clock[i] > 0)
                            r.AppendFormat(" Clock[{0}]: {1}{2}", i, allClocks.Clock[i],
                                Environment.NewLine);
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            if (NVAPI.NvAPI_GPU_GetTachReading != null)
            {
                int tachValue;
                var status = NVAPI.NvAPI_GPU_GetTachReading(handle, out tachValue);

                r.AppendLine("Tachometer");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    r.AppendFormat(" Value: {0}{1}", tachValue, Environment.NewLine);
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            if (NVAPI.NvAPI_GPU_GetPStates != null)
            {
                var states = new NvPStates();
                states.Version = NVAPI.GPU_PSTATES_VER;
                states.PStates = new NvPState[NVAPI.MAX_PSTATES_PER_GPU];
                var status = NVAPI.NvAPI_GPU_GetPStates(handle, ref states);

                r.AppendLine("P-States");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    for (var i = 0; i < states.PStates.Length; i++)
                        if (states.PStates[i].Present)
                            r.AppendFormat(" Percentage[{0}]: {1}{2}", i,
                                states.PStates[i].Percentage, Environment.NewLine);
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            if (NVAPI.NvAPI_GPU_GetUsages != null)
            {
                var usages = new NvUsages();
                usages.Version = NVAPI.GPU_USAGES_VER;
                usages.Usage = new uint[NVAPI.MAX_USAGES_PER_GPU];
                var status = NVAPI.NvAPI_GPU_GetUsages(handle, ref usages);

                r.AppendLine("Usages");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    for (var i = 0; i < usages.Usage.Length; i++)
                        if (usages.Usage[i] > 0)
                            r.AppendFormat(" Usage[{0}]: {1}{2}", i,
                                usages.Usage[i], Environment.NewLine);
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            if (NVAPI.NvAPI_GPU_GetCoolerSettings != null)
            {
                var settings = new NvGPUCoolerSettings();
                settings.Version = NVAPI.GPU_COOLER_SETTINGS_VER;
                settings.Cooler = new NvCooler[NVAPI.MAX_COOLER_PER_GPU];
                var status =
                    NVAPI.NvAPI_GPU_GetCoolerSettings(handle, 0, ref settings);

                r.AppendLine("Cooler Settings");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    for (var i = 0; i < settings.Count; i++)
                    {
                        r.AppendFormat(" Cooler[{0}].Type: {1}{2}", i,
                            settings.Cooler[i].Type, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].Controller: {1}{2}", i,
                            settings.Cooler[i].Controller, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].DefaultMin: {1}{2}", i,
                            settings.Cooler[i].DefaultMin, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].DefaultMax: {1}{2}", i,
                            settings.Cooler[i].DefaultMax, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].CurrentMin: {1}{2}", i,
                            settings.Cooler[i].CurrentMin, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].CurrentMax: {1}{2}", i,
                            settings.Cooler[i].CurrentMax, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].CurrentLevel: {1}{2}", i,
                            settings.Cooler[i].CurrentLevel, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].DefaultPolicy: {1}{2}", i,
                            settings.Cooler[i].DefaultPolicy, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].CurrentPolicy: {1}{2}", i,
                            settings.Cooler[i].CurrentPolicy, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].Target: {1}{2}", i,
                            settings.Cooler[i].Target, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].ControlType: {1}{2}", i,
                            settings.Cooler[i].ControlType, Environment.NewLine);
                        r.AppendFormat(" Cooler[{0}].Active: {1}{2}", i,
                            settings.Cooler[i].Active, Environment.NewLine);
                    }
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            if (NVAPI.NvAPI_GPU_GetMemoryInfo != null && displayHandle.HasValue)
            {
                var memoryInfo = new NvMemoryInfo();
                memoryInfo.Version = NVAPI.GPU_MEMORY_INFO_VER;
                memoryInfo.Values = new uint[NVAPI.MAX_MEMORY_VALUES_PER_GPU];
                var status = NVAPI.NvAPI_GPU_GetMemoryInfo(displayHandle.Value,
                    ref memoryInfo);

                r.AppendLine("Memory Info");
                r.AppendLine();
                if (status == NvStatus.OK)
                {
                    for (var i = 0; i < memoryInfo.Values.Length; i++)
                        r.AppendFormat(" Value[{0}]: {1}{2}", i,
                            memoryInfo.Values[i], Environment.NewLine);
                }
                else
                {
                    r.Append(" Status: ");
                    r.AppendLine(status.ToString());
                }

                r.AppendLine();
            }

            return r.ToString();
        }

        private void SoftwareControlValueChanged(IControl control)
        {
            var coolerLevels = new NvGPUCoolerLevels();
            coolerLevels.Version = NVAPI.GPU_COOLER_LEVELS_VER;
            coolerLevels.Levels = new NvLevel[NVAPI.MAX_COOLER_PER_GPU];
            coolerLevels.Levels[0].Level = (int) control.SoftwareValue;
            coolerLevels.Levels[0].Policy = 1;
            NVAPI.NvAPI_GPU_SetCoolerLevels(handle, 0, ref coolerLevels);
        }

        private void ControlModeChanged(IControl control)
        {
            switch (control.ControlMode)
            {
                case ControlMode.Undefined:
                    return;
                case ControlMode.Default:
                    SetDefaultFanSpeed();
                    break;
                case ControlMode.Software:
                    SoftwareControlValueChanged(control);
                    break;
                default:
                    return;
            }
        }

        private void SetDefaultFanSpeed()
        {
            var coolerLevels = new NvGPUCoolerLevels();
            coolerLevels.Version = NVAPI.GPU_COOLER_LEVELS_VER;
            coolerLevels.Levels = new NvLevel[NVAPI.MAX_COOLER_PER_GPU];
            coolerLevels.Levels[0].Policy = 0x20;
            NVAPI.NvAPI_GPU_SetCoolerLevels(handle, 0, ref coolerLevels);
        }

        public override void Close()
        {
            if (fanControl != null)
            {
                fanControl.ControlModeChanged -= ControlModeChanged;
                fanControl.SoftwareControlValueChanged -=
                    SoftwareControlValueChanged;

                if (fanControl.ControlMode != ControlMode.Undefined)
                    SetDefaultFanSpeed();
            }

            base.Close();
        }
    }
}