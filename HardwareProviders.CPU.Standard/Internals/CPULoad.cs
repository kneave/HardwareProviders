﻿/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2011 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Runtime.InteropServices;

namespace HardwareProviders.CPU.Internals
{
    internal class CpuLoad
    {
        private readonly float[] _coreLoads;

        private readonly Cpuid[][] _cpuid;

        private long[] _idleTimes;

        private float _totalLoad;
        private long[] _totalTimes;

        public CpuLoad(Cpuid[][] cpuid)
        {
            _cpuid = cpuid;
            _coreLoads = new float[cpuid.Length];
            _totalLoad = 0;
            try
            {
                GetTimes(out _idleTimes, out _totalTimes);
            }
            catch (Exception)
            {
                _idleTimes = null;
                _totalTimes = null;
            }

            if (_idleTimes != null)
                IsAvailable = true;
        }

        public bool IsAvailable { get; }


        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(SystemInformationClass informationClass, [Out] SystemProcessorPerformanceInformation[] informations, int structSize, out IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct SystemProcessorPerformanceInformation
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long Reserved0;
            public long Reserved1;
            public ulong Reserved2;
        }

        public enum SystemInformationClass
        {
            SystemBasicInformation = 0,
            SystemCpuInformation = 1,
            SystemPerformanceInformation = 2,
            SystemTimeOfDayInformation = 3,
            SystemProcessInformation = 5,
            SystemProcessorPerformanceInformation = 8
        }


        private static bool GetTimes(out long[] idle, out long[] total)
        {
            var informations = new SystemProcessorPerformanceInformation[64];

            var size = Marshal.SizeOf(typeof(SystemProcessorPerformanceInformation));

            idle = null;
            total = null;

            if (NtQuerySystemInformation(
                    CpuLoad.SystemInformationClass.SystemProcessorPerformanceInformation,
                    informations, informations.Length * size, out var returnLength) != 0)
                return false;

            idle = new long[(int) returnLength / size];
            total = new long[(int) returnLength / size];

            for (var i = 0; i < idle.Length; i++)
            {
                idle[i] = informations[i].IdleTime;
                total[i] = informations[i].KernelTime + informations[i].UserTime;
            }

            return true;
        }

        public float GetTotalLoad()
        {
            return _totalLoad;
        }

        public float GetCoreLoad(int core)
        {
            return _coreLoads[core];
        }

        public void Update()
        {
            if (_idleTimes == null)
                return;

            if (!GetTimes(out var newIdleTimes, out var newTotalTimes))
                return;

            for (var i = 0; i < Math.Min(newTotalTimes.Length, _totalTimes.Length); i++)
                if (newTotalTimes[i] - _totalTimes[i] < 100000)
                    return;

            if (newIdleTimes == null)
                return;

            float total = 0;
            var count = 0;
            for (var i = 0; i < _cpuid.Length; i++)
            {
                float value = 0;
                for (var j = 0; j < _cpuid[i].Length; j++)
                {
                    long index = _cpuid[i][j].Thread;
                    if (index < newIdleTimes.Length && index < _totalTimes.Length)
                    {
                        var idle =
                            (newIdleTimes[index] - _idleTimes[index]) /
                            (float) (newTotalTimes[index] - _totalTimes[index]);
                        value += idle;
                        total += idle;
                        count++;
                    }
                }

                value = 1.0f - value / _cpuid[i].Length;
                value = value < 0 ? 0 : value;
                _coreLoads[i] = value * 100;
            }

            if (count > 0)
            {
                total = 1.0f - total / count;
                total = total < 0 ? 0 : total;
            }
            else
            {
                total = 0;
            }

            _totalLoad = total * 100;

            _totalTimes = newTotalTimes;
            _idleTimes = newIdleTimes;
        }
    }
}